using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SlackOfDescent;

public sealed class NativeModManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Cover { get; set; } = "";
    public string EntryType { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public List<string> ManagedDependencies { get; set; } = new();
    public NativeModContent? Content { get; set; }
    public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.Ordinal);
    public NativeMapPack? MapPack { get; set; }

    internal static NativeModManifest Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return Read(document.RootElement);
    }
    internal static NativeModManifest Read(JsonElement json)
    {
        var legacy = json.Deserialize<LegacyManifest>(NativeContentStore.Json) ?? throw new InvalidDataException("mod.json 内容为空。");
        return legacy.FormatVersion == 1 ? new() {
            FormatVersion = 1, Id = legacy.Id, Name = legacy.Id, EntryType = legacy.EntryType,
            Sources = legacy.Sources, ManagedDependencies = legacy.ManagedDependencies
        } : json.Deserialize<NativeModManifest>(NativeContentStore.Json)!;
    }
    private sealed class LegacyManifest
    {
        public LegacyManifest() { }
        public int FormatVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string EntryType { get; set; } = "";
        public List<string> Sources { get; set; } = new();
        public List<string> ManagedDependencies { get; set; } = new();
    }
}

public sealed class NativeModContent
{
    public Dictionary<string, string> Resources { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Maps { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Characters { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Languages { get; set; } = new(StringComparer.Ordinal);
    public NativeModCode? Code { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NativeModCode
{
    public string EntryType { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public List<string> ManagedDependencies { get; set; } = new();
}

public sealed class NativeMapPack
{
    public List<string> RoomOrder { get; set; } = new();
    public string StartRoom { get; set; } = "";
}

public sealed record NativeModResult(bool Success, string Id, string Diagnostics, WeakReference? PreviousContext = null);
public sealed record NativeModBatchResult(bool Success, string Diagnostics, IReadOnlyList<WeakReference> PreviousContexts);
public sealed record NativeModInfo(string Id, string Directory, string Diagnostics)
{
    public NativeModManifest Manifest { get; init; } = new();
}

public sealed class NativeModManager : IDisposable
{
    private readonly ContentRegistry content;
    private readonly Dictionary<string, Loaded> loaded = new(StringComparer.Ordinal);
    private readonly string? referencesDirectory;
    public event Action? Changed;
    public IReadOnlyList<NativeModInfo> LoadedMods => loaded.Values.Select(m => new NativeModInfo(m.Id, m.Directory, m.Diagnostics) {
        Manifest = JsonSerializer.Deserialize<NativeModManifest>(JsonSerializer.SerializeToUtf8Bytes(m.Manifest, NativeContentStore.Json), NativeContentStore.Json)! }).ToArray();
    public IReadOnlyList<WeakReference> RetiredContexts => retired.ToArray();
    private readonly List<WeakReference> retired = new();
    public NativeModManager(ContentRegistry content, string? referencesDirectory = null)
    { this.content = content; this.referencesDirectory = referencesDirectory; }

    public NativeModResult Reload(string directory)
    {
        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        try
        {
            id = NativeModManifest.Read(Path.Combine(directory, "mod.json")).Id;
            var result = ReloadMany([directory]);
            return new(result.Success, id, result.Diagnostics, result.PreviousContexts.FirstOrDefault());
        }
        catch (Exception error) { return new(false, id, error.GetBaseException().Message); }
    }

    public NativeModBatchResult ReloadMany(IEnumerable<string> directories)
    {
        var candidates = new List<Loaded>();
        try
        {
            foreach (string directory in directories)
            {
                var candidate = BuildCandidate(Path.GetFullPath(directory));
                candidates.Add(candidate);
                if (candidates.Count(mod => mod.Id == candidate.Id) != 1) throw new InvalidDataException("重载批次包含重复模组 ID：" + candidate.Id);
            }
            if (candidates.Count == 0) throw new InvalidDataException("请选择需要重载的内容包。");
            var previous = candidates.Where(mod => loaded.ContainsKey(mod.Id)).Select(mod => loaded[mod.Id]).ToArray();
            var next = loaded.Values.Where(mod => candidates.All(candidate => candidate.Id != mod.Id)).Concat(candidates).ToArray();
            ValidateDependencies(next.Select(mod => mod.Manifest));
            var occupied = content.ContributionIds().Except(previous.SelectMany(mod => mod.Content.ContributionIds())).ToHashSet();
            foreach (var candidate in candidates)
                foreach (var contribution in candidate.Content.ContributionIds())
                    if (!occupied.Add(contribution)) throw new InvalidDataException("内容 ID 已被其他来源占用：" + contribution.Id);
            var resourcePaths = previous.Concat(candidates).SelectMany(ResourcePaths).ToArray();
            foreach (var mod in previous) Remove(mod);
            foreach (var mod in candidates) { Add(mod); loaded[mod.Id] = mod; }
            content.DeferredCommit = true;
            try { content.Refresh(); }
            catch
            {
                foreach (var mod in candidates) { Remove(mod); loaded.Remove(mod.Id); }
                foreach (var mod in previous) { Add(mod); loaded[mod.Id] = mod; }
                try { content.Refresh(); } catch (Exception rollback) { Console.Error.WriteLine("MOD_ROLLBACK " + rollback.Message); }
                throw;
            }
            finally { content.DeferredCommit = false; }
            content.LifecycleDiagnostics.Clear();
            content.Commit();
            content.CommitResources(resourcePaths);
            var retiredContexts = previous.Select(Retire).OfType<WeakReference>().ToArray();
            string diagnostics = string.Join("\n", candidates.Select(mod => mod.Diagnostics).Where(value => value.Length > 0).Concat(content.LifecycleDiagnostics));
            if (content.LifecycleDiagnostics.Count > 0)
                foreach (var mod in loaded.Values.ToArray())
                {
                    var owned = content.LifecycleDiagnostics.Where(message => message.Contains(" " + mod.Id + "/", StringComparison.Ordinal)).ToArray();
                    if (owned.Length > 0) loaded[mod.Id] = mod with { Diagnostics = string.Join("\n", new[] { mod.Diagnostics }.Where(value => value.Length > 0).Concat(owned).Distinct()) };
                }
            NotifyChanged();
            return new(true, diagnostics.Length == 0 ? candidates.Any(mod => mod.Context is not null) ? "编译并加载成功。" : "内容包已加载。" : diagnostics, retiredContexts);
        }
        catch (Exception error)
        {
            foreach (var mod in candidates.Where(mod => !loaded.Values.Contains(mod))) Retire(mod);
            return new(false, error.GetBaseException().Message, Array.Empty<WeakReference>());
        }
    }

    private Loaded BuildCandidate(string directory)
    {
        var (manifest, code, declared) = ReadPackage(directory);
        string id = manifest.Id;
        if (code is null) { declared.AssignOwner(id); return new(id, directory, "", null, null, declared, new NativeModResources(), manifest); }
        string ResolveCode(string path) => manifest.FormatVersion == 1 ? ResolveWithin(directory, path) : ResolvePackageFile(directory, path);
        var dependencies = code.ManagedDependencies.Select(ResolveCode).ToArray();
        foreach (string dependency in dependencies)
            if (!File.Exists(dependency)) throw new FileNotFoundException("缺少声明的托管依赖", dependency);
        var trees = code.Sources.Select(ResolveCode)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp12), path)).ToArray();
        var references = ReferenceFiles().Concat(dependencies).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("NativeMod_" + Guid.NewGuid().ToString("N"), trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        using var assembly = new MemoryStream();
        var emitted = compilation.Emit(assembly);
        string diagnostics = string.Join("\n", emitted.Diagnostics.Where(value => value.Severity >= DiagnosticSeverity.Warning).Select(value => value.ToString()));
        if (!emitted.Success) throw new InvalidDataException(diagnostics);
        return Activate(id, directory, manifest, code, assembly.ToArray(), dependencies, declared, diagnostics);
    }

    public static NativeModManifest InspectPackage(string directory) => ReadPackage(Path.GetFullPath(directory)).Manifest;

    private static (NativeModManifest Manifest, NativeModCode? Code, ContentRegistry Declared) ReadPackage(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "mod.json")));
        var manifest = NativeModManifest.Read(document.RootElement);
        var code = ReadCode(manifest, document.RootElement);
        var declared = new ContentRegistry();
        if (manifest.FormatVersion == 1)
        {
            foreach (string path in code!.Sources.Concat(code.ManagedDependencies))
                if (!File.Exists(ResolveWithin(directory, path))) throw new FileNotFoundException("缺少声明的代码文件", path);
            return (manifest, code, declared);
        }
        if (!string.IsNullOrEmpty(manifest.Cover)) ResolvePackageFile(directory, manifest.Cover);
        static void AddFiles(string root, Dictionary<string, string> source, Dictionary<string, string> target)
        {
            foreach (var item in source)
            {
                if (string.IsNullOrWhiteSpace(item.Key)) throw new InvalidDataException("内容缺少稳定标识。");
                target.Add(item.Key, ResolvePackageFile(root, item.Value));
            }
        }
        AddFiles(directory, manifest.Content!.Resources, declared.Resources);
        AddFiles(directory, manifest.Content.Maps, declared.Maps);
        AddFiles(directory, manifest.Content.Languages, declared.Languages);
        foreach (var item in declared.Maps)
        {
            var map = NativeContentStore.Load<NativeMapDocument>(item.Value);
            if (map.FormatVersion != 1 || map.Id != item.Key || map.Room is null || map.Room["instances"] is not System.Text.Json.Nodes.JsonArray
                || map.Room["tiles"] is not System.Text.Json.Nodes.JsonArray || map.Room["width"]?.GetValue<int>() is not > 0 || map.Room["height"]?.GetValue<int>() is not > 0)
                throw new InvalidDataException("地图声明与文件格式或 ID 不一致：" + item.Key);
        }
        foreach (var item in declared.Languages)
        {
            using var language = JsonDocument.Parse(File.ReadAllBytes(item.Value));
            UniqueProperties(language.RootElement, true);
            if (language.RootElement.ValueKind != JsonValueKind.Object || language.RootElement.EnumerateObject().Any(value => value.Value.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("语言文件必须是文字到文字的对象：" + item.Key);
            string locale = item.Key.Split('.').Last().Replace('-', '_');
            if (locale is not "en" and not "zh_CN") throw new InvalidDataException("语言声明 ID 必须以 .en 或 .zh_CN 结尾：" + item.Key);
            foreach (var value in language.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(value.Name)) throw new InvalidDataException("语言词条缺少稳定标识。");
                if (!declared.Translations.TryAdd(locale + "/" + value.Name, value.Value.GetString()!)) throw new InvalidDataException("语言词条重复：" + value.Name);
            }
        }
        foreach (var item in manifest.Content.Characters)
        {
            var definition = NativeContentStore.Load<CharacterDefinition>(ResolvePackageFile(directory, item.Value));
            if (string.IsNullOrWhiteSpace(item.Key) || definition.Id != item.Key) throw new InvalidDataException("角色声明与文件 ID 不一致：" + item.Key);
            var pixels = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var part in definition.Parts)
                foreach (var frame in part.Frames)
                {
                    var image = NativeCharacterPng.Decode(File.ReadAllBytes(ResolvePackageFile(directory, frame.Png)));
                    if (image.Width != part.Width || image.Height != part.Height) throw new InvalidDataException("角色帧尺寸不符：" + frame.Png);
                    if (!pixels.TryAdd(frame.Id, image.Rgba)) throw new InvalidDataException("角色帧 ID 重复：" + frame.Id);
                }
            _ = new NativeCharacterDocument(definition, pixels);
            declared.Characters.Add(item.Key, definition); declared.CharacterDirectories.Add(item.Key, directory);
        }
        if (code is not null)
        {
            foreach (string path in code.Sources.Concat(code.ManagedDependencies)) ResolvePackageFile(directory, path);
        }
        return (manifest, code, declared);
    }

    private static void ValidateDependencies(IEnumerable<NativeModManifest> manifests)
    {
        var byId = manifests.ToDictionary(manifest => manifest.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        void Visit(NativeModManifest manifest)
        {
            if (visited.Contains(manifest.Id)) return;
            if (!pending.Add(manifest.Id)) throw new InvalidDataException("模组依赖存在循环：" + manifest.Id);
            foreach (var dependency in manifest.Dependencies)
            {
                if (!byId.TryGetValue(dependency.Key, out var target)) throw new InvalidDataException("缺少模组依赖：" + dependency.Key);
                if (dependency.Value != "*" && dependency.Value != target.Version) throw new InvalidDataException("模组依赖版本不符：" + dependency.Key + " 需要 " + dependency.Value + "，当前 " + target.Version);
                Visit(target);
            }
            pending.Remove(manifest.Id); visited.Add(manifest.Id);
        }
        foreach (var manifest in byId.Values) Visit(manifest);
    }

    private static NativeModCode? ReadCode(NativeModManifest manifest, JsonElement json)
    {
        if (manifest.FormatVersion == 1)
        {
            if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.EntryType))
                throw new InvalidDataException("mod.json 需要 formatVersion=1、id 和 entryType。");
            if (manifest.Sources is null || manifest.Sources.Count == 0) throw new InvalidDataException("mod.json 没有声明 C# 源文件。");
            if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = manifest.Id;
            return new() { EntryType = manifest.EntryType, Sources = manifest.Sources, ManagedDependencies = manifest.ManagedDependencies };
        }
        if (manifest.FormatVersion != 2) throw new InvalidDataException("不支持 mod.json 格式版本：" + manifest.FormatVersion);
        UniqueProperties(json);
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("mod.json v2 需要 id、name 和作品 version。");
        if (!string.IsNullOrEmpty(manifest.EntryType) || manifest.Sources is null || manifest.Sources.Count != 0
            || manifest.ManagedDependencies is null || manifest.ManagedDependencies.Count != 0)
            throw new InvalidDataException("mod.json v2 的代码必须声明在 content.code 中。");
        if (manifest.Dependencies is null || manifest.Dependencies.Any(value => string.IsNullOrWhiteSpace(value.Key)
            || value.Value != "*" && (string.IsNullOrEmpty(value.Value) || !System.Text.RegularExpressions.Regex.IsMatch(value.Value, @"^[A-Za-z0-9][A-Za-z0-9._+\-]*$"))))
            throw new InvalidDataException("依赖版本仅支持 * 或精确版本号。");
        if (manifest.Content is not {} content || content.Resources is null || content.Maps is null || content.Characters is null || content.Languages is null)
            throw new InvalidDataException("mod.json v2 需要有效的 content 内容对象。");
        if (content.Extra is { Count: > 0 }) throw new InvalidDataException("尚不支持的内容类型：" + string.Join(", ", content.Extra.Keys));
        if (content.Code is null && content.Resources.Count + content.Maps.Count + content.Characters.Count + content.Languages.Count == 0)
            throw new InvalidDataException("content 必须声明资源或代码。");
        if (content.Code is {} code && (string.IsNullOrWhiteSpace(code.EntryType) || code.Sources is null || code.Sources.Count == 0 || code.ManagedDependencies is null))
            throw new InvalidDataException("content.code 需要 entryType、非空 sources 和有效的 managedDependencies。");
        if (manifest.MapPack is {} maps && (maps.RoomOrder is null || maps.RoomOrder.Distinct(StringComparer.Ordinal).Count() != maps.RoomOrder.Count
            || maps.RoomOrder.Any(id => !content.Maps.ContainsKey(id)) || !string.IsNullOrEmpty(maps.StartRoom) && !content.Maps.ContainsKey(maps.StartRoom)))
            throw new InvalidDataException("地图包顺序或入口需要引用本包唯一的地图 ID。");
        return content.Code;
    }

    private static void UniqueProperties(JsonElement value, bool resourceIds = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(resourceIds ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("mod.json 字段或资源标识重复：" + property.Name);
                UniqueProperties(property.Value, new[] { "resources", "maps", "characters", "languages", "dependencies" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }

    internal static string ResolvePackageFile(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1 && relative[1] == ':') throw new InvalidDataException("模组文件必须使用包内相对路径。");
        string root = Path.TrimEndingDirectorySeparator(directory);
        string path = ResolveWithin(root, relative.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException("缺少声明的模组文件", relative);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (string? current = path; current is not null && !current.Equals(root, comparison); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模组文件路径不支持符号链接：" + relative);
        return path;
    }

    public NativeModResult Unload(string id)
    {
        string[] dependents = loaded.Values.Where(mod => mod.Id != id && mod.Manifest.Dependencies.ContainsKey(id)).Select(mod => mod.Id).ToArray();
        if (dependents.Length > 0) return new(false, id, "请先卸载依赖此包的模组：" + string.Join(", ", dependents));
        if (!loaded.Remove(id, out var mod)) return new(false, id, "模组尚未加载。");
        Remove(mod);
        string diagnostics = "模组已卸载。";
        try { content.Refresh(); } catch (Exception error) { diagnostics += "\n内容刷新失败：" + error.GetBaseException().Message; content.Commit(); }
        if (content.LifecycleDiagnostics.Count > 0) diagnostics += "\n" + string.Join("\n", content.LifecycleDiagnostics);
        content.CommitResources(ResourcePaths(mod));
        var weak = Retire(mod); NotifyChanged();
        return new(true, id, diagnostics, weak);
    }

    private void NotifyChanged()
    {
        if (Changed is null) return;
        foreach (Action subscriber in Changed.GetInvocationList())
            try { subscriber(); } catch (Exception error) { Console.Error.WriteLine("MOD_OBSERVER " + error.Message); }
    }

    public static bool VerifyUnloaded(WeakReference context, int attempts = 8)
    {
        for (int i = 0; i < attempts && context.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        return !context.IsAlive;
    }

    private IEnumerable<string> ReferenceFiles()
    {
        string? packaged = referencesDirectory;
        if (packaged is null)
            packaged = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(AssemblyFile(typeof(NativeModManager).Assembly))!, Environment.CurrentDirectory,
                Path.GetDirectoryName(Godot.OS.GetExecutablePath())!, Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? AppContext.BaseDirectory }
                .SelectMany(p => new[] { Path.Combine(p, "Tools", "ModCompiler", "References"), Path.Combine(p, "ModCompiler", "References") })
                .FirstOrDefault(Directory.Exists);
        if (packaged is not null && File.Exists(Path.Combine(packaged, "System.Runtime.dll")))
        {
            foreach (string path in Directory.EnumerateFiles(packaged, "*.dll"))
                if (Path.GetFileName(path) is not "SlackOfDescent.dll" and not "GodotSharp.dll") yield return path;
        }
        else
        {
            foreach (string path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) yield return path;
        }
        yield return AssemblyFile(typeof(NativeModManager).Assembly);
        yield return AssemblyFile(typeof(Godot.Node).Assembly);
    }

    internal static string AssemblyFile(Assembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location)) return assembly.Location;
        string name = assembly.GetName().Name + ".dll";
        string project = Godot.ProjectSettings.GlobalizePath("res://");
        foreach (string directory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory,
            Path.Combine(project, ".godot", "mono", "temp", "bin", "Debug"), Path.Combine(project, ".godot", "mono", "temp", "bin", "Release") })
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("缺少运行时匹配的编译引用：" + name);
    }

    internal static string ResolveWithin(string directory, string relative)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(directory, relative));
        if (Path.IsPathRooted(relative) || !result.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("模组路径必须位于模组目录内：" + relative);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Loaded Activate(string id, string directory, NativeModManifest manifest, NativeModCode code, byte[] image, string[] dependencies, ContentRegistry declared, string diagnostics)
    {
        var context = new ModAssemblyContext(dependencies);
        var resources = new NativeModResources();
        IMod? entry = null;
        try
        {
            using var stream = new MemoryStream(image);
            var assembly = context.LoadFromStream(stream);
            var type = assembly.GetType(code.EntryType, true)!;
            entry = Activator.CreateInstance(type) as IMod ?? throw new InvalidDataException("entryType 必须实现 IMod。");
            if (entry.Id != id) throw new InvalidDataException("IMod.Id 与 mod.json 不一致。");
            var staged = new ContentRegistry();
            entry.Load(new(staged, directory, resources.Cancellation, resources.Track) { OwnerId = id, DataRoot = content.ModDataRoot });
            var duplicate = staged.ContributionIds().Intersect(declared.ContributionIds()).FirstOrDefault();
            if (duplicate.Id is not null) throw new InvalidDataException("代码与声明的内容标识重复：" + duplicate.Id);
            declared.AddContributionsTo(staged);
            staged.AssignOwner(id);
            return new(id, directory, diagnostics, context, entry, staged, resources, manifest);
        }
        catch
        {
            try { entry?.Unload(); } catch { }
            resources.Dispose(); retired.Add(new WeakReference(context)); context.Unload(); throw;
        }
    }

    private void Add(Loaded mod) => mod.Content.AddContributionsTo(content);
    private void Remove(Loaded mod) => mod.Content.RemoveContributionsFrom(content);
    private static IEnumerable<string> ResourcePaths(Loaded mod)
    {
        foreach (string path in mod.Content.Resources.Values) yield return path;
        foreach (var directory in mod.Content.CharacterDirectories)
            if (mod.Content.Characters.TryGetValue(directory.Key, out var character))
                foreach (var frame in character.Parts.SelectMany(part => part.Frames))
                    yield return Path.Combine(directory.Value, frame.Png);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference? Retire(Loaded mod)
    {
        var weak = mod.Context is null ? null : new WeakReference(mod.Context);
        content.RetireOwner(mod.Content.OwnerScope);
        try { mod.Entry?.Unload(); }
        catch (Exception error) { Console.Error.WriteLine("MOD_UNLOAD " + mod.Id + ": " + error.Message); }
        finally { mod.Resources.Dispose(); mod.Content.ClearContributions(); mod.Context?.Unload(); }
        if (weak is not null) retired.Add(weak); return weak;
    }
    public void Dispose()
    {
        while (loaded.Count > 0)
        {
            string id = loaded.Keys.First(id => loaded.Values.All(mod => !mod.Manifest.Dependencies.ContainsKey(id)));
            Unload(id);
        }
    }
    private sealed record Loaded(string Id, string Directory, string Diagnostics, ModAssemblyContext? Context, IMod? Entry, ContentRegistry Content, NativeModResources Resources, NativeModManifest Manifest);
    private sealed class ModAssemblyContext(string[] dependencies) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IMod).Assembly.GetName().Name) return typeof(IMod).Assembly;
            if (name.Name == typeof(Godot.Node).Assembly.GetName().Name) return typeof(Godot.Node).Assembly;
            string? dependency = dependencies.FirstOrDefault(p => AssemblyName.GetAssemblyName(p).Name == name.Name);
            if (dependency is not null) { using var stream = File.OpenRead(dependency); return LoadFromStream(stream); }
            return null;
        }
    }
}

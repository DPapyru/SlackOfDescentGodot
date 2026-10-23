using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SlackOfDescent;

public sealed class NativeModManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string EntryType { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public List<string> ManagedDependencies { get; set; } = new();
}

public sealed record NativeModResult(bool Success, string Id, string Diagnostics, WeakReference? PreviousContext = null);
public sealed record NativeModInfo(string Id, string Directory, string Diagnostics);

/// <summary>Compiles C# inside the shipped runtime; an installed SDK is never needed.</summary>
public sealed class NativeModManager : IDisposable
{
    private readonly ContentRegistry content;
    private readonly Dictionary<string, Loaded> loaded = new(StringComparer.Ordinal);
    private readonly string? referencesDirectory;
    public event Action? Changed;
    public IReadOnlyList<NativeModInfo> LoadedMods => loaded.Values.Select(m => new NativeModInfo(m.Id, m.Directory, m.Diagnostics)).ToArray();
    public IReadOnlyList<WeakReference> RetiredContexts => retired.ToArray();
    private readonly List<WeakReference> retired = new();
    public NativeModManager(ContentRegistry content, string? referencesDirectory = null)
    { this.content = content; this.referencesDirectory = referencesDirectory; }

    public NativeModResult Reload(string directory)
    {
        Loaded? candidate = null;
        string id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        try
        {
            directory = Path.GetFullPath(directory);
            var manifest = NativeContentStore.Load<NativeModManifest>(Path.Combine(directory, "mod.json"));
            id = manifest.Id;
            if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(manifest.EntryType))
                throw new InvalidDataException("mod.json 需要 formatVersion=1、id 和 entryType。");
            if (manifest.Sources.Count == 0) throw new InvalidDataException("mod.json 没有声明 C# 源文件。");
            var dependencies = manifest.ManagedDependencies.Select(p => ResolveWithin(directory, p)).ToArray();
            foreach (string dependency in dependencies)
                if (!File.Exists(dependency)) throw new FileNotFoundException("缺少声明的托管依赖", dependency);
            var trees = manifest.Sources.Select(p => ResolveWithin(directory, p))
                .Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), new CSharpParseOptions(LanguageVersion.CSharp12), p)).ToArray();
            var references = ReferenceFiles().Concat(dependencies).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
            var compilation = CSharpCompilation.Create("NativeMod_" + Guid.NewGuid().ToString("N"), trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var assembly = new MemoryStream();
            var emitted = compilation.Emit(assembly);
            string diagnostics = string.Join("\n", emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()));
            if (!emitted.Success) return new(false, id, diagnostics);
            candidate = Activate(id, directory, manifest, assembly.ToArray(), dependencies, diagnostics);
            loaded.TryGetValue(id, out var previous);
            Validate(candidate, previous);
            // Registry subscribers detach their instances before any old assembly is released.
            if (previous is not null) Remove(previous);
            Add(candidate);
            loaded[id] = candidate;
            try { content.Refresh(); }
            catch
            {
                Remove(candidate); loaded.Remove(id);
                if (previous is not null) { Add(previous); loaded[id] = previous; }
                try { content.Refresh(); } catch (Exception rollback) { Console.Error.WriteLine("MOD_ROLLBACK " + rollback.Message); }
                throw;
            }
            WeakReference? retiredContext = previous is null ? null : Retire(previous);
            NotifyChanged();
            return new(true, id, diagnostics.Length == 0 ? "编译并加载成功。" : diagnostics, retiredContext);
        }
        catch (Exception error)
        {
            if (candidate is not null && !loaded.Values.Contains(candidate)) Retire(candidate);
            return new(false, id, error.GetBaseException().Message);
        }
    }

    public NativeModResult Unload(string id)
    {
        if (!loaded.Remove(id, out var mod)) return new(false, id, "模组尚未加载。");
        Remove(mod); content.Refresh();
        var weak = Retire(mod); NotifyChanged();
        return new(true, id, "模组已卸载。", weak);
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
        if (packaged is not null && Directory.Exists(packaged))
        {
            foreach (string path in Directory.EnumerateFiles(packaged, "*.dll"))
                if (Path.GetFileName(path) is not "SlackOfDescent.dll" and not "GodotSharp.dll") yield return path;
        }
        else
        {
            // Editor/development fallback uses the running runtime, never searches for an SDK.
            foreach (string path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) yield return path;
        }
        yield return AssemblyFile(typeof(NativeModManager).Assembly);
        yield return AssemblyFile(typeof(Godot.Node).Assembly);
    }

    private static string AssemblyFile(Assembly assembly)
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
    private static Loaded Activate(string id, string directory, NativeModManifest manifest, byte[] image, string[] dependencies, string diagnostics)
    {
        var context = new ModAssemblyContext(dependencies);
        var resources = new NativeModResources();
        IMod? entry = null;
        try
        {
            using var stream = new MemoryStream(image);
            var assembly = context.LoadFromStream(stream);
            var type = assembly.GetType(manifest.EntryType, true)!;
            entry = Activator.CreateInstance(type) as IMod ?? throw new InvalidDataException("entryType 必须实现 IMod。");
            if (entry.Id != id) throw new InvalidDataException("IMod.Id 与 mod.json 不一致。");
            var staged = new ContentRegistry();
            entry.Load(new(staged, directory, resources.Cancellation, resources.Track));
            return new(id, directory, diagnostics, context, entry, staged, resources);
        }
        catch
        {
            try { entry?.Unload(); } catch { }
            resources.Dispose(); context.Unload(); throw;
        }
    }

    private void Validate(Loaded candidate, Loaded? previous)
    {
        static void Unique<T>(Dictionary<string,T> target, Dictionary<string,T> incoming, Dictionary<string,T>? outgoing)
        {
            foreach (string key in incoming.Keys)
                if (target.ContainsKey(key) && outgoing?.ContainsKey(key) != true) throw new InvalidDataException("内容 ID 已被其他来源占用：" + key);
        }
        Unique(content.Actions, candidate.Content.Actions, previous?.Content.Actions);
        Unique(content.Entities, candidate.Content.Entities, previous?.Content.Entities);
        Unique(content.Characters, candidate.Content.Characters, previous?.Content.Characters);
        Unique(content.Resources, candidate.Content.Resources, previous?.Content.Resources);
    }
    private void Add(Loaded mod)
    {
        foreach (var p in mod.Content.Actions) content.Actions.Add(p.Key, p.Value);
        foreach (var p in mod.Content.Entities) content.Entities.Add(p.Key, p.Value);
        foreach (var p in mod.Content.Characters) content.Characters.Add(p.Key, p.Value);
        foreach (var p in mod.Content.Resources) content.Resources.Add(p.Key, p.Value);
    }
    private void Remove(Loaded mod)
    {
        foreach (string key in mod.Content.Actions.Keys) content.Actions.Remove(key);
        foreach (string key in mod.Content.Entities.Keys) content.Entities.Remove(key);
        foreach (string key in mod.Content.Characters.Keys) content.Characters.Remove(key);
        foreach (string key in mod.Content.Resources.Keys) content.Resources.Remove(key);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference Retire(Loaded mod)
    {
        var weak = new WeakReference(mod.Context);
        try { mod.Entry.Unload(); }
        catch (Exception error) { Console.Error.WriteLine("MOD_UNLOAD " + mod.Id + ": " + error.Message); }
        finally { mod.Resources.Dispose(); mod.Content.Actions.Clear(); mod.Content.Entities.Clear(); mod.Content.Characters.Clear(); mod.Content.Resources.Clear(); mod.Context.Unload(); }
        retired.Add(weak); return weak;
    }
    public void Dispose() { foreach (string id in loaded.Keys.ToArray()) Unload(id); }
    private sealed record Loaded(string Id, string Directory, string Diagnostics, ModAssemblyContext Context, IMod Entry, ContentRegistry Content, NativeModResources Resources);
    private sealed class ModAssemblyContext(string[] dependencies) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IMod).Assembly.GetName().Name) return typeof(IMod).Assembly;
            if (name.Name == typeof(Godot.Node).Assembly.GetName().Name) return typeof(Godot.Node).Assembly;
            string? dependency = dependencies.FirstOrDefault(p => AssemblyName.GetAssemblyName(p).Name == name.Name);
            if (dependency is not null) { using var stream = File.OpenRead(dependency); return LoadFromStream(stream); }
            // Only framework/host assemblies can fall through; undeclared sibling DLLs never load.
            return null;
        }
    }
}

using System.IO.Compression;
using System.Text.Json;

namespace SlackOfDescent;

public sealed record NativeModProjectInfo(string Directory, NativeModManifest Manifest);
public sealed record NativeModArchivePreview(NativeModManifest Manifest, IReadOnlyList<string> Files, bool ReplacesInstalled, string? InstalledVersion, bool ContainsCode);
public sealed record NativeModInstallResult(string Directory, NativeModManifest Manifest, bool Replaced);

public sealed class NativeModLibrary(NativeContentStore store, NativeModManager? manager = null)
{
    private const int MaximumEntries = 4096;
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;
    public IReadOnlyList<NativeModProjectInfo> Installed() => List(store.Mods);
    public IReadOnlyList<NativeModProjectInfo> Projects() => List(store.Projects);

    private static IReadOnlyList<NativeModProjectInfo> List(string root)
    {
        var projects = new List<NativeModProjectInfo>();
        foreach (string directory in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(directory).StartsWith('.')) continue;
            try { projects.Add(new(directory, NativeModManifest.Read(Path.Combine(directory, "mod.json")))); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { }
        }
        return projects;
    }
    public string CreateProject(string id, string name)
    {
        ValidateSegment(id);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("请输入项目名称。");
        string directory = Path.Combine(store.Projects, id);
        if (System.IO.Directory.Exists(directory)) throw new IOException("同名项目已存在。");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            string references = Path.Combine(store.Root, "Tools", "ModCompiler", "References");
            foreach (var type in new[] { typeof(NativeModLibrary), typeof(Godot.Node) })
            {
                string source = NativeModManager.AssemblyFile(type.Assembly);
                NativeContentStore.WriteAtomic(Path.Combine(references, Path.GetFileName(source)), File.ReadAllBytes(source));
            }
            NativeContentStore.Save(Path.Combine(directory, "mod.json"), new NativeModManifest {
                FormatVersion = 2, Id = id, Name = name, Version = "1.0.0", Content = new NativeModContent()
            });
            File.WriteAllText(Path.Combine(directory, id + ".csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>12</LangVersion>
                    <GameAssemblyDirectory Condition="'$(GameAssemblyDirectory)' == ''">$(MSBuildThisFileDirectory){{System.Security.SecurityElement.Escape(Path.GetRelativePath(directory, references).Replace('\\', '/'))}}</GameAssemblyDirectory>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="SlackOfDescent">
                      <HintPath>$(GameAssemblyDirectory)/SlackOfDescent.dll</HintPath>
                      <Private>false</Private>
                    </Reference>
                    <Reference Include="GodotSharp">
                      <HintPath>$(GameAssemblyDirectory)/GodotSharp.dll</HintPath>
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            return directory;
        }
        catch { System.IO.Directory.Delete(directory, true); throw; }
    }
    public NativeModManifest OpenProject(string directory)
    {
        directory = ProjectDirectory(directory);
        return NativeModManifest.Read(Path.Combine(directory, "mod.json"));
    }
    public string CloneInstalled(string installedDirectory, string projectId)
    {
        ValidateSegment(projectId);
        installedDirectory = Path.GetFullPath(installedDirectory);
        if (!Installed().Any(value => SamePath(value.Directory, installedDirectory))) throw new InvalidDataException("请选择已安装的内容包。");
        string target = Path.Combine(store.Projects, projectId);
        if (System.IO.Directory.Exists(target)) throw new IOException("同名项目已存在。");
        try
        {
            CopyPackage(installedDirectory, target);
            var manifest = NativeModManifest.Read(Path.Combine(target, "mod.json"));
            if (manifest.FormatVersion == 1)
                NativeContentStore.Save(Path.Combine(target, "mod.json"), new NativeModManifest {
                    FormatVersion = 2, Id = manifest.Id, Name = manifest.Id, Version = "1.0.0",
                    Content = new NativeModContent { Code = new NativeModCode {
                        EntryType = manifest.EntryType, Sources = manifest.Sources, ManagedDependencies = manifest.ManagedDependencies
                    } }
                });
            NativeModManager.InspectPackage(target);
            return target;
        }
        catch { if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true); throw; }
    }
    public void SaveProjectManifest(string directory, NativeModManifest manifest)
    {
        directory = ProjectDirectory(directory);
        ValidateSegment(manifest.Id);
        if (manifest.FormatVersion != 2 || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Content is null || manifest.Dependencies is null) throw new InvalidDataException("项目清单需要 v2、名称、版本及 content。");
        NativeContentStore.Save(Path.Combine(directory, "mod.json"), manifest);
    }
    public NativeModArchivePreview InspectZip(string zip)
    {
        var (manifest, files) = InspectArchive(zip);
        var existing = FindInstalled(manifest.Id);
        return new(manifest, files, existing is not null, existing?.Manifest.Version,
            manifest.FormatVersion == 1 || manifest.Content?.Code is not null);
    }
    private static (NativeModManifest Manifest, IReadOnlyList<string> Files) InspectArchive(string zip)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "native-mod-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = Extract(zip, temporary);
            var manifest = NativeModManager.InspectPackage(temporary);
            ValidateSegment(manifest.Id);
            return (manifest, files);
        }
        finally { if (System.IO.Directory.Exists(temporary)) System.IO.Directory.Delete(temporary, true); }
    }
    public NativeModInstallResult Install(string zip, bool replace = false)
    {
        string incoming = Path.Combine(store.Mods, ".incoming-" + Guid.NewGuid().ToString("N"));
        string? backup = null;
        string? target = null;
        bool placed = false;
        try
        {
            Extract(zip, incoming);
            var manifest = NativeModManager.InspectPackage(incoming);
            ValidateSegment(manifest.Id);
            var existing = FindInstalled(manifest.Id);
            if (existing is not null && !replace) throw new IOException("相同 ID 的内容包已安装，请预览并确认替换。");
            target = existing?.Directory ?? Path.Combine(store.Mods, manifest.Id);
            if (existing is null && System.IO.Directory.Exists(target)) throw new IOException("安装目录已被占用。");
            if (existing is not null)
            {
                CheckLinks(store.Mods, target);
                backup = Path.Combine(store.Mods, ".backup-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.Move(target, backup);
            }
            System.IO.Directory.Move(incoming, target); placed = true;
            if (manager?.LoadedMods.Any(value => value.Id == manifest.Id) == true)
            {
                var reloaded = manager.Reload(target);
                if (!reloaded.Success) throw new InvalidDataException("更新加载失败，已保留旧版本：" + reloaded.Diagnostics);
            }
            if (backup is not null)
            {
                try { System.IO.Directory.Delete(backup, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("MOD_BACKUP " + error.Message); }
                backup = null;
            }
            return new(target, manifest, existing is not null);
        }
        catch
        {
            if (placed && target is not null && System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
            if (backup is not null && target is not null) { System.IO.Directory.Move(backup, target); backup = null; }
            throw;
        }
        finally { if (System.IO.Directory.Exists(incoming)) System.IO.Directory.Delete(incoming, true); }
    }
    public void Export(string projectDirectory, string zip, bool replace = false)
    {
        projectDirectory = ProjectDirectory(projectDirectory);
        NativeModManager.InspectPackage(projectDirectory);
        zip = Path.GetFullPath(zip);
        if (SamePath(zip, projectDirectory) || IsWithin(projectDirectory, zip)) throw new InvalidDataException("ZIP 必须导出到项目目录之外。");
        if (File.Exists(zip) && !replace) throw new IOException("目标 ZIP 已存在，请确认覆盖。");
        var files = PackageFiles(projectDirectory);
        string parent = Path.GetDirectoryName(zip)!;
        System.IO.Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, ".native-export-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                foreach (string file in files) archive.CreateEntryFromFile(file, Path.GetRelativePath(projectDirectory, file).Replace('\\', '/'), CompressionLevel.Optimal);
            InspectArchive(temporary);
            File.Move(temporary, zip, replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string ExportDiagnostics() => JsonSerializer.Serialize(new {
        installed = Installed(), projects = Projects(), loaded = manager?.LoadedMods ?? Array.Empty<NativeModInfo>()
    }, NativeContentStore.Json);

    private NativeModProjectInfo? FindInstalled(string id)
    {
        var matches = Installed().Where(value => value.Manifest.Id == id).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("安装库含重复模组 ID：" + id);
        return matches.SingleOrDefault();
    }
    private string ProjectDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!IsWithin(store.Projects, directory) || SamePath(store.Projects, directory)) throw new InvalidDataException("编辑项目必须位于项目库中，请先从安装副本创建项目。");
        CheckLinks(store.Projects, directory);
        return directory;
    }
    private static bool SamePath(string first, string second) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsWithin(string directory, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void ValidateSegment(string value, bool allowHidden = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) || !allowHidden && value.StartsWith('.'))
            throw new InvalidDataException("ID 或项目目录名必须是有效的单一目录名称。");
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new InvalidDataException("ID 或路径使用了保留名称。");
    }
    private static string ArchivePath(string value)
    {
        value = value.Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains(':') || value.Contains('\0')) throw new InvalidDataException("ZIP 包含无效路径。");
        string[] parts = value.TrimEnd('/').Split('/');
        foreach (string part in parts) ValidateSegment(part, true);
        return string.Join('/', parts);
    }
    private static IReadOnlyList<string> Extract(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("ZIP 文件数量超过限制。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string path = ArchivePath(entry.FullName);
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            int kind = entry.ExternalAttributes >> 16 & 0xf000;
            if (kind != 0 && kind != 0x8000 && kind != 0x4000) throw new InvalidDataException("ZIP 不支持符号链接或特殊文件。");
            if (!names.Add(path)) throw new InvalidDataException("ZIP 路径重复或大小写冲突：" + path);
            total = checked(total + entry.Length);
            if (entry.Length > MaximumFileBytes || total > MaximumTotalBytes || directory && entry.Length != 0) throw new InvalidDataException("ZIP 解压大小超过限制。");
            entries.Add((entry, path, directory));
        }
        var manifests = entries.Where(value => !value.Directory && Path.GetFileName(value.Path) == "mod.json").ToArray();
        if (manifests.Length != 1 || manifests[0].Path.Count(character => character == '/') > 1) throw new InvalidDataException("ZIP 需要唯一的根目录或单层包装目录 mod.json。");
        string prefix = manifests[0].Path == "mod.json" ? "" : manifests[0].Path[..(manifests[0].Path.LastIndexOf('/') + 1)];
        foreach (var item in entries)
            if (prefix.Length > 0 && item.Path != prefix.TrimEnd('/') && !item.Path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("ZIP 包含内容包根目录以外的文件。");
        var files = new List<string>();
        System.IO.Directory.CreateDirectory(destination);
        foreach (var item in entries)
        {
            if (item.Path == prefix.TrimEnd('/')) continue;
            string relative = item.Path[prefix.Length..];
            string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            if (item.Directory) { System.IO.Directory.CreateDirectory(target); continue; }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = item.Entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            byte[] buffer = new byte[81920]; long written = 0; int count;
            while ((count = input.Read(buffer)) != 0)
            {
                written += count;
                if (written > item.Entry.Length || written > MaximumFileBytes) throw new InvalidDataException("ZIP 实际解压大小超过声明。");
                output.Write(buffer, 0, count);
            }
            if (written != item.Entry.Length) throw new InvalidDataException("ZIP 文件内容截断。");
            files.Add(relative);
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }
    private static string[] PackageFiles(string root)
    {
        var files = new List<string>();
        void Visit(string directory)
        {
            CheckLinks(root, directory);
            foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                if (name is ".git" or ".godot") continue;
                CheckLinks(root, path);
                ArchivePath(Path.GetRelativePath(root, path));
                if (System.IO.Directory.Exists(path)) Visit(path);
                else files.Add(path);
            }
        }
        Visit(root);
        if (files.Count > MaximumEntries || files.Sum(file => new FileInfo(file).Length) > MaximumTotalBytes || files.Any(file => new FileInfo(file).Length > MaximumFileBytes))
            throw new InvalidDataException("内容包文件数量或大小超过限制。");
        return files.ToArray();
    }
    private static void CheckLinks(string root, string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("内容包不支持符号链接。");
            if (SamePath(current, root)) return;
        }
        throw new InvalidDataException("路径不在内容库内。");
    }
    private static void CopyPackage(string source, string target)
    {
        foreach (string file in PackageFiles(source))
            NativeContentStore.WriteAtomic(Path.Combine(target, Path.GetRelativePath(source, file)), File.ReadAllBytes(file));
    }
}

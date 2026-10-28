namespace SlackOfDescent;

public static class GmPaths
{
    public static string FindData(string executableDirectory,string assemblyDirectory)
    {
        var candidates=new[]{Path.Combine(executableDirectory,"GameData"),Path.Combine(assemblyDirectory,"GameData")};
        return candidates.FirstOrDefault(path=>File.Exists(Path.Combine(path,"manifest.json")))
            ??throw new DirectoryNotFoundException("GameData is missing. Searched: "+string.Join(", ",candidates));
    }

    public static string FindCompiler(string dataDirectory)
    {
        string name=OperatingSystem.IsWindows()?"slack-extract.exe":"slack-extract";
        var root=Directory.GetParent(dataDirectory)??throw new DirectoryNotFoundException(dataDirectory);
        var candidates=new[]{
            Path.Combine(root.FullName,"Tools",name),
            Path.Combine(root.Parent?.FullName??root.FullName,"Tools",name),
            Path.Combine(root.FullName,"Tools","Extract","target","release",name)
        };
        return candidates.FirstOrDefault(File.Exists)
            ??throw new FileNotFoundException("GML compiler unavailable. Searched: "+string.Join(", ",candidates));
    }
}

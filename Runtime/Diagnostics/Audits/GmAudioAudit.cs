using Godot;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public sealed partial class GmAudio
{
    public int AuditResourceSounds()
    {
        var embedded=world.Manifest.GetProperty("sounds").EnumerateArray().Where(s=>s.GetProperty("present").GetBoolean()).Select(s=>Path.Combine(world.Root,s.GetProperty("path").GetString()!));
        var external=Directory.EnumerateFiles(world.SourceRoot,"*",SearchOption.AllDirectories).Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".wav" or ".mp3" or ".ogg");
        var paths=embedded.Concat(external).Distinct().ToArray();
        foreach(string path in paths)
        {
            using var stream=Load(path);
            if(stream is null||stream.GetLength()<=0)throw new InvalidDataException("Packaged sound failed to decode: "+path);
        }
        return paths.Length;
    }
}

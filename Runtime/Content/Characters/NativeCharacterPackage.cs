using System.IO.Compression;
using System.Text.Json;

namespace SlackOfDescent;

public static class NativeCharacterPackage
{
    public static byte[] Encode(NativeCharacterDocument document)
    {
        document.EndEdit(); document.Validate();
        var staged = document.Duplicate();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var part in staged.Definition.Parts) foreach (var frame in part.Frames)
            {
                if (staged.MissingFrames.ContainsKey(frame.Id) && staged.Pixels(frame.Id).All(b => b == 0)) continue;
                frame.Png = staged.Definition.Id + "/" + frame.Id + ".png";
                using var image = zip.CreateEntry(frame.Png, CompressionLevel.Fastest).Open();
                image.Write(NativeCharacterPng.Encode(part.Width, part.Height, staged.Pixels(frame.Id)));
            }
            using var definition = zip.CreateEntry(staged.Definition.Id + ".character.json").Open();
            JsonSerializer.Serialize(definition, staged.Definition, NativeContentStore.Json);
        }
        return output.ToArray();
    }

    public static NativeCharacterDocument Decode(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > 17000 || zip.Entries.Sum(e => e.Length) > 256L * 1024 * 1024) throw new InvalidDataException("角色包过大。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            string name = Normalize(entry.FullName);
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("角色包中存在重复路径。");
        }
        var definitions = entries.Where(e => e.Key.EndsWith(".character.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (definitions.Length != 1) throw new InvalidDataException("角色包必须包含一个角色定义。");
        using var stream = definitions[0].Value.Open();
        var definition = JsonSerializer.Deserialize<CharacterDefinition>(stream, NativeContentStore.Json) ?? throw new InvalidDataException("角色定义无效。");
        new NativeCharacterDocument(definition);
        string directory = definitions[0].Key.Contains('/') ? definitions[0].Key[..(definitions[0].Key.LastIndexOf('/') + 1)] : "";
        var pixels = new Dictionary<string, byte[]>(); var missing = new Dictionary<string, string>();
        foreach (var part in definition.Parts) foreach (var frame in part.Frames)
        {
            string name = Normalize(directory + frame.Png);
            if (!entries.TryGetValue(name, out var entry))
            {
                pixels.Add(frame.Id, new byte[part.Width * part.Height * 4]); missing.Add(frame.Id, frame.Png); continue;
            }
            using var data = new MemoryStream(); using (var image = entry.Open()) image.CopyTo(data);
            var decoded = NativeCharacterPng.Decode(data.ToArray());
            if (decoded.Width != part.Width || decoded.Height != part.Height) throw new InvalidDataException("角色帧尺寸不匹配。");
            pixels.Add(frame.Id, decoded.Rgba);
        }
        var document = new NativeCharacterDocument(definition, pixels);
        foreach (var item in missing) document.MissingFrames.Add(item.Key, item.Value);
        return document;
    }

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(part => part is ".." or ".")) throw new InvalidDataException("角色包包含无效路径。");
        return path;
    }
}

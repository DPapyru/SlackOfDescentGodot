using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public sealed partial class CharacterDefinition
{
    public List<NativeCharacterPart> Parts { get; set; } = new();
    public double AnimationFps { get; set; } = 8;
}

public sealed class NativeCharacterPart
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "部件";
    public string Target { get; set; } = "BODY_TOP";
    public bool ReplaceOriginal { get; set; }
    public int Width { get; set; } = 32;
    public int Height { get; set; } = 32;
    public int AnchorX { get; set; } = 16;
    public int AnchorY { get; set; } = 24;
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int Layer { get; set; }
    public bool Visible { get; set; } = true;
    public List<NativeCharacterFrame> Frames { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class NativeCharacterFrame
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Png { get; set; } = "";
    public int Duration { get; set; } = 1;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public readonly record struct NativePixelRect(int X, int Y, int Width, int Height)
{
    public NativePixelRect Clip(int width, int height)
    {
        int x = Math.Clamp(X, 0, width), y = Math.Clamp(Y, 0, height);
        return new(x, y, Math.Max(0, Math.Min(width, X + Width) - x), Math.Max(0, Math.Min(height, Y + Height) - y));
    }
}

public sealed record NativePixelClipboard(int Width, int Height, byte[] Rgba)
{
    public string Encode() => "slack-rgba-v1:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));
    public static NativePixelClipboard? Decode(string text)
    {
        if (!text.StartsWith("slack-rgba-v1:", StringComparison.Ordinal)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<NativePixelClipboard>(Convert.FromBase64String(text[14..]));
            return value is not null && value.Width > 0 && value.Height > 0 && value.Width <= 1024 && value.Height <= 1024 && value.Rgba is not null && value.Rgba.Length == checked(value.Width * value.Height * 4) ? value : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or OverflowException) { return null; }
    }
}

public sealed class NativeCharacterRepository(NativeContentStore store)
{
    public NativeContentStore Store { get; } = store;
    public string DefinitionPath(string id)
    {
        if (id is "." or ".." || string.IsNullOrWhiteSpace(id) || id.Length > 120 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new InvalidDataException("无效角色 ID。");
        return Path.Combine(Store.Characters, id + ".character.json");
    }
    public string FramePath(NativeCharacterFrame frame, string? packageDirectory = null)
    {
        string root = Path.GetFullPath(packageDirectory ?? Store.Characters) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, frame.Png));
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("角色图像路径超出内容目录。");
        return path;
    }
    public NativeCharacterDocument Load(string id) => LoadDefinition(NativeContentStore.Load<CharacterDefinition>(DefinitionPath(id)));
    public NativeCharacterDocument LoadDefinition(CharacterDefinition definition, string? packageDirectory = null)
    {
        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var missing=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var part in definition.Parts)
            foreach (var frame in part.Frames)
            {
                string path=FramePath(frame, packageDirectory);
                byte[] rgba;
                try
                {
                    var png = NativeCharacterPng.Decode(File.ReadAllBytes(path));
                    if (png.Width != part.Width || png.Height != part.Height) throw new InvalidDataException("PNG 尺寸与角色部件不符。");
                    rgba=png.Rgba;
                }
                catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
                {
                    missing.Add(frame.Id,frame.Png);
                    rgba=new byte[checked(part.Width*part.Height*4)];
                }
                if (!data.TryAdd(frame.Id, rgba)) throw new InvalidDataException("角色帧 ID 重复。");
            }
        var document=new NativeCharacterDocument(definition,data);
        foreach(var frame in missing)document.MissingFrames.Add(frame.Key,frame.Value);
        return document;
    }
    public void Save(NativeCharacterDocument document, ContentRegistry registry, bool overwrite = false)
    {
        document.EndEdit(); document.Validate();
        string path = DefinitionPath(document.Definition.Id);
        if (File.Exists(path) && !overwrite) throw new IOException("同一 ID 的角色已存在，请确认覆盖。");
        var staged = document.Duplicate();
        string revision = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Store.Characters, staged.Definition.Id, revision);
        Directory.CreateDirectory(directory);
        bool committed = false;
        try
        {
            int partIndex = 0;
            foreach (var part in staged.Definition.Parts)
            {
                int frameIndex = 0;
                foreach (var frame in part.Frames)
                {
                    if(staged.MissingFrames.ContainsKey(frame.Id)&&staged.Pixels(frame.Id).All(b=>b==0)){frameIndex++;continue;}
                    frame.Png = Path.Combine(staged.Definition.Id, revision, $"{partIndex:D2}-{frameIndex++:D3}.png").Replace('\\', '/');
                    NativeContentStore.WriteAtomic(FramePath(frame), NativeCharacterPng.Encode(part.Width, part.Height, staged.Pixels(part, part.Frames.IndexOf(frame))));
                    staged.MissingFrames.Remove(frame.Id);
                }
                partIndex++;
            }
            NativeContentStore.Save(path, staged.Definition); committed = true;
            for (int p = 0; p < document.Definition.Parts.Count; p++)
                for (int f = 0; f < document.Definition.Parts[p].Frames.Count; f++) document.Definition.Parts[p].Frames[f].Png = staged.Definition.Parts[p].Frames[f].Png;
            document.MissingFrames.Clear();foreach(var frame in staged.MissingFrames)document.MissingFrames.Add(frame.Key,frame.Value);
            document.MarkSaved(); registry.RegisterCharacter(staged.Definition);
        }
        finally { if (!committed && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    public string SavePackage(NativeCharacterDocument document, string packageDirectory)
    {
        document.EndEdit(); document.Validate();
        if (document.MissingFrames.Count > 0) throw new InvalidDataException("请先补齐缺失的角色帧。");
        var staged = document.Duplicate();
        string folder = "characters/" + staged.Definition.Id + "/" + Guid.NewGuid().ToString("N");
        foreach (var part in staged.Definition.Parts) foreach (var frame in part.Frames)
        {
            frame.Png = folder + "/" + frame.Id + ".png";
            NativeContentStore.WriteAtomic(FramePath(frame, packageDirectory), NativeCharacterPng.Encode(part.Width, part.Height, staged.Pixels(frame.Id)));
        }
        string relative = "characters/" + staged.Definition.Id + ".character.json";
        NativeContentStore.Save(Path.Combine(packageDirectory, relative), staged.Definition);
        return relative;
    }
    public IReadOnlyList<string> Discover(ContentRegistry registry) => Store.DiscoverCharacters(registry);
}

public static class NativeCharacterTargets
{
    public static readonly string[] Names = ["HEAD", "BODY_TOP", "BODY_BOTTOM", "HAND1_ABOVE", "HAND2_ABOVE", "HAND1_BELOW", "HAND2_BELOW", "FOOT1_ABOVE", "FOOT2_ABOVE", "FOOT1_BELOW", "FOOT2_BELOW", "TAIL"];
}

public static class NativeCharacterPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4)) throw new InvalidDataException("无效 RGBA 图像。");
        using var output = new MemoryStream(); output.Write(Signature);
        byte[] header = new byte[13]; Write32(header, 0, (uint)width); Write32(header, 4, (uint)height); header[8] = 8; header[9] = 6; Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            for (int y = 0; y < height; y++) { zlib.WriteByte(0); zlib.Write(rgba, y * width * 4, width * 4); }
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []); return output.ToArray();
    }
    public static NativePixelClipboard Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("无效 PNG 签名。");
        int width = 0, height = 0; bool header = false, end = false;
        using var compressed = new MemoryStream();
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            int length = checked((int)Read32(png, offset));
            if (length < 0 || length > png.Length - offset - 12) throw new InvalidDataException("PNG 数据块截断。");
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var body = png.AsSpan(offset + 8, length);
            if (Crc(png.AsSpan(offset + 4, length + 4)) != Read32(png, offset + 8 + length)) throw new InvalidDataException("PNG 校验失败。");
            if (type == "IHDR")
            {
                if (header || length != 13 || body[8] != 8 || body[9] != 6 || body[10] != 0 || body[11] != 0 || body[12] != 0) throw new InvalidDataException("仅支持非交错 RGBA8 PNG。");
                width = checked((int)Read32(png, offset + 8)); height = checked((int)Read32(png, offset + 12));
                if (width is < 1 or > 256 || height is < 1 or > 256) throw new InvalidDataException("PNG 尺寸必须为 1–256。");
                header = true;
            }
            else if (type == "IDAT") compressed.Write(body);
            else if (type == "IEND") { end = true; break; }
            offset += length + 12;
        }
        if (!header || !end) throw new InvalidDataException("PNG 缺少头部或结束块。");
        compressed.Position = 0; using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        int stride = width * 4; byte[] result = new byte[stride * height]; byte[] row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = zlib.ReadByte(); zlib.ReadExactly(row);
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? result[y * stride + x - 4] : 0, above = y > 0 ? result[(y - 1) * stride + x] : 0, diagonal = y > 0 && x >= 4 ? result[(y - 1) * stride + x - 4] : 0;
                int predictor = filter switch { 0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2, 4 => Paeth(left, above, diagonal), _ => throw new InvalidDataException("PNG 滤波类型无效。") };
                result[y * stride + x] = unchecked((byte)(row[x] + predictor));
            }
        }
        if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG 像素数据长度异常。");
        return new(width, height, result);
    }
    private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static uint Read32(byte[] value, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(offset, 4));
    private static void Write32(byte[] value, int offset, uint number) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(offset, 4), number);
    private static uint Crc(ReadOnlySpan<byte> data) { uint crc = uint.MaxValue; foreach (byte b in data) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ crc >> 1 : crc >> 1; } return ~crc; }
    private static void Chunk(Stream output, string type, byte[] data)
    {
        byte[] chunk = new byte[data.Length + 12]; Write32(chunk, 0, (uint)data.Length); Encoding.ASCII.GetBytes(type, chunk.AsSpan(4, 4)); data.CopyTo(chunk, 8); Write32(chunk, data.Length + 8, Crc(chunk.AsSpan(4, data.Length + 4))); output.Write(chunk);
    }
}

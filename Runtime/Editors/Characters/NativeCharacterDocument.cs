using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackOfDescent;

public sealed class NativeCharacterDocument
{
    public CharacterDefinition Definition { get; private set; }
    public Dictionary<string,string> MissingFrames { get; } = new(StringComparer.Ordinal);
    private Dictionary<string, byte[]> pixels = new(StringComparer.Ordinal);
    private readonly Stack<Snapshot> undo = new();
    private readonly Stack<Snapshot> redo = new();
    private Snapshot? transaction;
    private string saved = "";
    private sealed record Snapshot(CharacterDefinition Definition, Dictionary<string, byte[]> Pixels, Dictionary<string, string> Missing);
    public bool Dirty => Fingerprint() != saved;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int UndoCount => undo.Count;
    public NativeCharacterDocument(CharacterDefinition definition, Dictionary<string, byte[]>? data = null)
    {
        Definition = Clone(definition);
        if (data is not null) pixels = data.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal);
        Validate(); MarkSaved();
    }
    public static NativeCharacterDocument Create()
    {
        var document = new NativeCharacterDocument(new CharacterDefinition());
        document.AddPart("身体", 32, 32, 0);
        document.AddPart("帽子", 32, 32, 20).Target = "HEAD";
        document.AddPart("尾巴", 32, 32, -10).Target = "BODY_BOTTOM";
        document.undo.Clear(); document.saved = "";
        return document;
    }
    public byte[] Pixels(NativeCharacterPart part, int frame) => pixels[part.Frames[frame].Id];
    public byte[] Pixels(string frameId) => pixels[frameId];
    public void BeginEdit() => transaction ??= Capture();
    public void EndEdit()
    {
        if (transaction is null) return;
        if (Fingerprint(transaction) != Fingerprint()) { undo.Push(transaction); redo.Clear(); }
        transaction = null;
    }
    public void Edit(Action change) { BeginEdit(); try { change(); } finally { EndEdit(); } }
    public NativeCharacterPart AddPart(string name, int width = 32, int height = 32, int layer = 0)
    {
        CheckSize(width, height);
        var part = new NativeCharacterPart { Name = name, Width = width, Height = height, AnchorX = width / 2, AnchorY = height * 3 / 4, Layer = layer };
        Edit(() => { Definition.Parts.Add(part); AddFrameRaw(part, null); });
        return part;
    }
    public void ImportFrames(string partId, IReadOnlyList<NativePixelClipboard> images, bool append = false, int? anchorX = null, int? anchorY = null)
    {
        if (images.Count == 0) throw new InvalidDataException("请选择至少一张 PNG。");
        var part = Definition.Parts.Single(p => p.Id == partId);
        foreach (var image in images)
        {
            CheckSize(image.Width, image.Height);
            if (image.Rgba.Length != checked(image.Width * image.Height * 4) || image.Width != images[0].Width || image.Height != images[0].Height) throw new InvalidDataException("动画帧尺寸必须一致。");
        }
        if (append && (part.Width != images[0].Width || part.Height != images[0].Height)) throw new InvalidDataException("新增帧尺寸必须与部位一致。");
        if ((append ? part.Frames.Count : 0) + images.Count > 256) throw new InvalidDataException("部件帧数必须为 1–256。");
        Edit(() =>
        {
            if (!append)
            {
                foreach (var frame in part.Frames) { pixels.Remove(frame.Id); MissingFrames.Remove(frame.Id); }
                part.Frames.Clear(); part.Width = images[0].Width; part.Height = images[0].Height;
                part.AnchorX = anchorX ?? part.Width / 2; part.AnchorY = anchorY ?? part.Height / 2;
            }
            foreach (var image in images)
            {
                var frame = new NativeCharacterFrame(); part.Frames.Add(frame); pixels.Add(frame.Id, (byte[])image.Rgba.Clone());
            }
        });
    }
    public void RemovePart(string partId) => Edit(() =>
    {
        var part = Definition.Parts.Single(p => p.Id == partId);
        foreach (var frame in part.Frames) pixels.Remove(frame.Id);
        Definition.Parts.Remove(part);
    });
    public int AddFrame(string partId, int? duplicate = null)
    {
        var part = Definition.Parts.Single(p => p.Id == partId);
        Edit(() => AddFrameRaw(part, duplicate)); return part.Frames.Count - 1;
    }
    private void AddFrameRaw(NativeCharacterPart part, int? duplicate)
    {
        var frame = new NativeCharacterFrame();
        byte[] data = duplicate is int index ? (byte[])Pixels(part, index).Clone() : new byte[checked(part.Width * part.Height * 4)];
        if (duplicate is int source) frame.Duration = part.Frames[source].Duration;
        part.Frames.Add(frame); pixels.Add(frame.Id, data);
    }
    public void RemoveFrame(string partId, int index)
    {
        var part = Definition.Parts.Single(p => p.Id == partId);
        if (part.Frames.Count == 1) throw new InvalidOperationException("每个部件至少保留一帧。");
        Edit(() => { pixels.Remove(part.Frames[index].Id); part.Frames.RemoveAt(index); });
    }
    public void Paint(string partId, int frame, int x, int y, byte r, byte g, byte b, byte a)
    {
        var part = Definition.Parts.Single(p => p.Id == partId);
        if (x < 0 || y < 0 || x >= part.Width || y >= part.Height) return;
        bool standalone = transaction is null;
        if (standalone) BeginEdit();
        int offset = (y * part.Width + x) * 4;
        var rgba = Pixels(part, frame); rgba[offset] = r; rgba[offset + 1] = g; rgba[offset + 2] = b; rgba[offset + 3] = a;
        if (standalone) EndEdit();
    }
    public NativePixelClipboard Copy(string partId, int frame, NativePixelRect? rectangle = null)
    {
        var part = Definition.Parts.Single(p => p.Id == partId);
        var rect = (rectangle ?? new NativePixelRect(0, 0, part.Width, part.Height)).Clip(part.Width, part.Height);
        if (rect.Width == 0 || rect.Height == 0) throw new InvalidOperationException("选区为空。");
        var result = new byte[rect.Width * rect.Height * 4]; var source = Pixels(part, frame);
        for (int y = 0; y < rect.Height; y++) Buffer.BlockCopy(source, ((rect.Y + y) * part.Width + rect.X) * 4, result, y * rect.Width * 4, rect.Width * 4);
        return new(rect.Width, rect.Height, result);
    }
    public NativePixelRect Paste(string partId, int frame, NativePixelClipboard clipboard, int x, int y)
    {
        if (clipboard.Width <= 0 || clipboard.Height <= 0 || clipboard.Rgba.Length != checked(clipboard.Width * clipboard.Height * 4)) throw new InvalidDataException("剪贴板 RGBA 长度无效。");
        var part = Definition.Parts.Single(p => p.Id == partId);
        var rect = new NativePixelRect(x, y, clipboard.Width, clipboard.Height).Clip(part.Width, part.Height);
        Edit(() =>
        {
            var destination = Pixels(part, frame);
            for (int row = 0; row < rect.Height; row++)
                Buffer.BlockCopy(clipboard.Rgba, ((rect.Y - y + row) * clipboard.Width + rect.X - x) * 4, destination, ((rect.Y + row) * part.Width + rect.X) * 4, rect.Width * 4);
        });
        return rect;
    }
    public bool Undo()
    {
        EndEdit(); if (undo.Count == 0) return false;
        redo.Push(Capture()); Restore(undo.Pop()); return true;
    }
    public bool Redo()
    {
        EndEdit(); if (redo.Count == 0) return false;
        undo.Push(Capture()); Restore(redo.Pop()); return true;
    }
    public void MarkSaved() => saved = Fingerprint();
    public NativeCharacterDocument Duplicate()
    {
        var copy=new NativeCharacterDocument(Definition,pixels);
        foreach(var frame in MissingFrames)copy.MissingFrames.Add(frame.Key,frame.Value);
        return copy;
    }
    public void Validate()
    {
        if (Definition.Id is "." or ".." || string.IsNullOrWhiteSpace(Definition.Id) || Definition.Id.Length > 120 || Definition.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new InvalidDataException("角色 ID 必须由英文字母、数字、点、下划线或短横线组成。");
        if (!double.IsFinite(Definition.AnimationFps) || Definition.AnimationFps is < 1 or > 60) throw new InvalidDataException("动画帧率必须为 1–60。请保留不支持的数据文件并更新编辑器。");
        if (Definition.FormatVersion != 1) throw new InvalidDataException("不支持的角色格式版本：" + Definition.FormatVersion);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in Definition.Parts)
        {
            CheckSize(part.Width, part.Height);
            if (!NativeCharacterTargets.Names.Contains(part.Target, StringComparer.Ordinal)) throw new InvalidDataException("未知角色部位：" + part.Target);
            if (string.IsNullOrWhiteSpace(part.Id) || !ids.Add(part.Id)) throw new InvalidDataException("部件 ID 为空或重复。");
            if (part.Frames.Count is < 1 or > 256) throw new InvalidDataException("部件帧数必须为 1–256。");
            foreach (var frame in part.Frames)
            {
                if (string.IsNullOrWhiteSpace(frame.Id) || !ids.Add(frame.Id)) throw new InvalidDataException("帧 ID 为空或重复。");
                if (frame.Duration is < 1 or > 600) throw new InvalidDataException("帧时长必须为 1–600。");
                if (!pixels.TryGetValue(frame.Id, out var data)) pixels[frame.Id] = new byte[checked(part.Width * part.Height * 4)];
                else if (data.Length != checked(part.Width * part.Height * 4)) throw new InvalidDataException("帧 RGBA 尺寸不符。");
            }
        }
        if (Definition.Parts.Count > 64) throw new InvalidDataException("部件最多为 64 个。");
    }
    private static void CheckSize(int width, int height)
    { if (width is < 1 or > 256 || height is < 1 or > 256) throw new InvalidDataException("画布宽高必须为 1–256。"); }
    private Snapshot Capture() => new(Clone(Definition), pixels.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal), new(MissingFrames, StringComparer.Ordinal));
    private void Restore(Snapshot state) { Definition = state.Definition; pixels = state.Pixels; MissingFrames.Clear(); foreach (var pair in state.Missing) MissingFrames.Add(pair.Key, pair.Value); }
    private static CharacterDefinition Clone(CharacterDefinition source) => JsonSerializer.Deserialize<CharacterDefinition>(JsonSerializer.SerializeToUtf8Bytes(source, NativeContentStore.Json), NativeContentStore.Json)!;
    private string Fingerprint() => Fingerprint(new(Definition, pixels, MissingFrames));
    private static string Fingerprint(Snapshot value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var semanticDefinition = Clone(value.Definition);
        foreach (var part in semanticDefinition.Parts) foreach (var frame in part.Frames) frame.Png = "";
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(semanticDefinition, NativeContentStore.Json));
        foreach (var entry in value.Pixels.OrderBy(p => p.Key, StringComparer.Ordinal)) { hash.AppendData(Encoding.UTF8.GetBytes(entry.Key)); hash.AppendData(entry.Value); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

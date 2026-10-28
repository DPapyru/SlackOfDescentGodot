using Godot;
using System.Text.Json;

namespace SlackOfDescent;

public partial class NativeCharacterEditor
{
    private void GenerateBase()
    {
        canvas.FinishStroke();
        var available = new HashSet<string>(assets.Select(a => a.Name), StringComparer.Ordinal);
        var placements = NativeCharacterBase.Plan(Document.Definition, Document.Definition.BasePreset, available);
        if (placements.Count == 0) { ShowMessage(L("当前基础行为没有可用的原版素材。", "No original assets are available for the current base behavior.")); return; }
        void Apply(bool replace)
        {
            try
            {
                int added = 0;
                canvas.Selection = null;
                Document.BeginEdit();
                foreach (var placeholder in Document.Definition.Parts.Where(part => !HasPixels(part)).ToList()) Document.RemovePart(placeholder.Id);
                foreach (var placement in placements)
                {
                    var asset = assets.First(a => a.Name == placement.Asset);
                    var part = replace && placement.Existing is string id ? Document.Definition.Parts.Single(p => p.Id == id) : AddBasePart(placement, asset);
                    if (part is null) continue;
                    ApplyBaseAssets(part, asset);
                    added++;
                }
                Document.EndEdit();
                selectedPart = Math.Max(0, Document.Definition.Parts.Count - 1); selectedFrame = 0;
                overallMode.ButtonPressed = true; workspace.CurrentTab = 1; Refresh(); OnPixelsEdited();
                status.Text = string.Format(NativeLocalization.Text("已生成基底角色：{0} 个部位。"), added);
            }
            catch (Exception error) { ShowMessage(error.Message); }
        }
        var missing = placements.Count(p => p.Existing is null);
        if (Document.Definition.Parts.Count > 0 && missing == 0) Confirm(L("用原版素材替换现有基底部位？", "Replace the existing base parts with original assets?"), () => Apply(true));
        else Apply(missing != placements.Count);
    }
    public async Task CaptureBaseShotsAsync(string output)
    {
        if (DisplayServer.GetName() == "headless") return;
        Directory.CreateDirectory(output);
        NewDocument(); GenerateBaseForAudit();
        await Frames();
        Save(output, "character-base-overall");
        int eye = Document.Definition.Parts.FindIndex(p => p.Name == "Eye");
        if (eye >= 0) SelectPart(eye);
        overallMode.ButtonPressed = false; canvas.Mode = NativeCharacterCanvasMode.Part;
        canvas.Zoom = 9; SetReferenceForAudit(true, 0.3f);
        await Frames();
        Save(output, "character-base-reference");
    }
    private async Task Frames()
    {
        for (int index = 0; index < 5; index++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private void Save(string output, string name)
    {
        RenderingServer.ForceDraw(true);
        using var image = GetViewport().GetTexture().GetImage();
        string path = Path.Combine(output, name + ".png"); image.SavePng(path);
        GD.Print("CHARACTER_BASE_UI_SHOT=", path);
    }
    public void GenerateBaseForAudit()
    {
        GenerateBase(); overallMode.ButtonPressed = true; workspace.CurrentTab = 1;
        canvas.Mode = NativeCharacterCanvasMode.Overall; autoFit = true; FitCanvas();
        canvas.ShowReference = true; canvas.ReferenceOpacity = 0.4f; canvas.QueueRedraw();
    }
    public void SetReferenceForAudit(bool visible, float opacity)
    {
        canvas.ShowReference = visible; canvas.ReferenceOpacity = opacity; canvas.QueueRedraw();
    }
    private bool HasPixels(NativeCharacterPart part)
    {
        for (int frame = 0; frame < part.Frames.Count; frame++)
        {
            var rgba = Document.Pixels(part, frame);
            for (int index = 3; index < rgba.Length; index += 4) if (rgba[index] != 0) return true;
        }
        return false;
    }
    private NativeCharacterPart? AddBasePart(NativeCharacterBasePlacement placement, (string Name, JsonElement Data) asset)
    {
        var frames = asset.Data.GetProperty("frames");
        if (frames.GetArrayLength() == 0) return null;
        int width = frames[0].GetProperty("width").GetInt32(), height = frames[0].GetProperty("height").GetInt32();
        var part = Document.AddPart(placement.Part, width, height, placement.Slot.Layer);
        part.Target = placement.Slot.Target;
        return part;
    }
    private void ApplyBaseAssets(NativeCharacterPart part, (string Name, JsonElement Data) asset)
    {
        var images = asset.Data.GetProperty("frames").EnumerateArray().Select(f => ReadPng(Path.Combine(world.Root, f.GetProperty("path").GetString()!), true)).ToArray();
        var anchor = BaseAnchor(asset.Name, asset.Data);
        Document.ImportFrames(part.Id, images, anchorX: anchor.X, anchorY: anchor.Y);
    }
    private (int X, int Y) BaseAnchor(string name, JsonElement data)
    {
        int x = data.GetProperty("originX").GetInt32(), y = data.GetProperty("originY").GetInt32();
        if (world.Code.TryGetValue("external/" + Path.ChangeExtension(name, ".txt"), out var metadata))
            foreach (var node in metadata[1].EnumerateArray())
                if (node.GetArrayLength() > 3 && node[0].GetString() == "binary" && node[1].GetString() == "Assign" && node[2][0].GetString() == "id" && node[3][0].GetString() == "num")
                {
                    if (node[2][1].GetString() == "partx") x = (int)node[3][1].GetDouble();
                    if (node[2][1].GetString() == "party") y = (int)node[3][1].GetDouble();
                }
        return (x, y);
    }
    private void RunBaseUiAudit(string output)
    {
        void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException("Character base UI: " + message); }
        NewDocument();
        overallMode.ButtonPressed = true; workspace.CurrentTab = 1;
        var plan = NativeCharacterBase.Resolve(Document.Definition.BasePreset, new HashSet<string>(assets.Select(a => a.Name), StringComparer.Ordinal));
        Expect(plan.Count >= 8, "the current preset resolves a full base, got " + plan.Count);
        GenerateBase();
        var generated = Document.Definition.Parts.ToList();
        Expect(generated.Count == plan.Count, "generating a base replaces the blank document, got " + generated.Count);
        Expect(generated.All(p => p.Frames.Count > 0 && NativeCharacterTargets.Names.Contains(p.Target)), "every generated part binds a real target");
        Expect(generated.Select(p => p.Layer).Distinct().Count() == generated.Count, "generated layers stay unique");
        foreach (var item in plan)
        {
            var part = generated.Single(p => p.Name == item.Part);
            var asset = assets.First(a => a.Name == item.Asset);
            var anchor = BaseAnchor(asset.Name, asset.Data);
            Expect(part.AnchorX == anchor.X && part.AnchorY == anchor.Y, part.Name + " keeps the original part anchor");
            Expect(part.Target == item.Slot.Target, part.Name + " attaches to " + item.Slot.Target);
            Expect(part.Frames.Count == asset.Data.GetProperty("frames").GetArrayLength(), part.Name + " imports every animation frame in order");
        }
        foreach (var part in generated.Where(p => p.Name is "Hair" or "Eye" or "Mouth")) Expect(part.Target == "HEAD", "head layers attach to HEAD");
        var bounds = NativeCharacterLayout.Bounds(Document.Definition);
        Expect(bounds.Width > 20 && bounds.Height > 20, "the generated base composes into a character sized rectangle, got " + bounds);
        canvas.Bind(Document, CurrentPart!.Id, 0);
        Expect(canvas.CustomMinimumSize == new Vector2(bounds.Width * canvas.Zoom, bounds.Height * canvas.Zoom), "the overall canvas is sized to the composed character");
        canvas.ShowReference = true; canvas.ReferenceOpacity = 0.4f;
        string coordinate = null!;
        string? expected = null;
        for (int y = bounds.Y; expected is null && y < bounds.Y + bounds.Height; y++)
            for (int x = bounds.X; x < bounds.X + bounds.Width; x++)
            {
                var hit = NativeCharacterLayout.Hit(Document.Definition, p => Document.Pixels(p, 0), x, y);
                if (hit is null) continue;
                expected = hit; coordinate = x + "," + y; break;
            }
        Expect(expected is not null, "a generated base has opaque pixels to hit");
        var target = Document.Definition.Parts.Single(p => p.Id == expected);
        var before = (byte[])Document.Pixels(target, 0).Clone();
        var others = generated.Where(p => p.Id != target.Id).ToDictionary(p => p.Id, p => (byte[])Document.Pixels(p, 0).Clone());
        canvas.PaintColor = new Color(1f, 0f, 0.5f, 1f); canvas.Tool = NativeCharacterTool.Brush;
        var point = coordinate.Split(',');
        canvas.AuditStroke([new(int.Parse(point[0]) - bounds.X, int.Parse(point[1]) - bounds.Y)]);
        Expect(!Document.Pixels(target, 0).SequenceEqual(before), "an overall stroke lands on the hit part " + target.Name);
        Expect(others.All(pair => Document.Pixels(Document.Definition.Parts.Single(p => p.Id == pair.Key), 0).SequenceEqual(pair.Value)), "an overall stroke leaves the other parts untouched");
        Expect(Document.CanUndo, "base generation and tracing stay undoable");
        string id = Document.Definition.Id;
        repository.Save(Document, registry, true);
        var reloaded = repository.Load(id);
        Expect(reloaded.Definition.Parts.Count == generated.Count, "reloading keeps the generated base part count");
        Expect(reloaded.Definition.Parts.Select(p => p.Target).SequenceEqual(generated.Select(p => p.Target)), "reloading keeps every generated target");
        Expect(reloaded.Definition.Parts.Select(p => p.AnchorX).SequenceEqual(generated.Select(p => p.AnchorX)), "reloading keeps every generated anchor");
        var traced = reloaded.Definition.Parts.Single(p => p.Name == target.Name);
        Expect(reloaded.Pixels(traced, 0).SequenceEqual(Document.Pixels(target, 0)), "reloading keeps the traced pixels");
        var empty = NativeCharacterDocument.Create(); empty.Definition.Parts.Clear();
        canvas.Mode = NativeCharacterCanvasMode.Overall; canvas.Bind(empty, null, 0);
        Expect(NativeCharacterLayout.Bounds(empty.Definition).Width == 0, "an empty document composes into nothing");
        overallMode.ButtonPressed = false; canvas.Mode = NativeCharacterCanvasMode.Part; canvas.ShowReference = false;
        Directory.CreateDirectory(Path.Combine(output, "biometric"));
        NativeContentStore.Save(Path.Combine(output, "biometric", "base-audit.json"), new { id, expected, coordinate, width = bounds.Width, height = bounds.Height, parts = generated.Count });
        GD.Print("CHARACTER_BASE_UI_AUDIT_OK parts=", generated.Count, " hit=", target.Name, " bounds=", bounds.Width + "x" + bounds.Height);
    }
}

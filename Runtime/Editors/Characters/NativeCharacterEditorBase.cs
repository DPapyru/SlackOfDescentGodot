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
}

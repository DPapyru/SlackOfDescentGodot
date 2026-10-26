using Godot;

namespace SlackOfDescent;

public sealed class NativeCharacterRendering
{
    private readonly GmWorld world;
    private readonly GmGraphics graphics;
    private readonly NativeCharacterRepository repository;
    private readonly ContentRegistry registry;
    private readonly Func<double> time;
    private readonly GmNativeBridge? bridge;
    private readonly Dictionary<int, CharacterDefinition> characters;
    private readonly HashSet<int> replaced = new();
    internal readonly List<(string Target, string Path, Vector2 Position, Vector2 Scale, float Angle)> Audit = new();
    public NativeCharacterRendering(GmWorld world, GmGraphics graphics, NativeCharacterRepository repository, ContentRegistry registry, Dictionary<int, CharacterDefinition> characters, Func<double> time, GmNativeBridge? bridge = null)
    {
        this.world = world; this.graphics = graphics; this.repository = repository; this.registry = registry; this.characters = characters; this.time = time; this.bridge = bridge;
        graphics.CharacterRendering = this;
        graphics.TrackNativeTextures(registry);
        world.CharacterSpriteBlur = (args, context) => graphics.IsCharacterMainTarget && Replace(context.Self,
            new((float)args[2].Number, (float)args[3].Number), new((float)args[4].Number, (float)args[5].Number),
            -Mathf.DegToRad((float)args[6].Number), GmGraphics.Colour(args[7].Number, args[8].Number));
        graphics.AdditionalDrawCommands += Decorations;
    }
    private GmInstance? Find(int id) => world.Instances.FirstOrDefault(i => i.Id == id && !i.Destroyed);
    private GmInstance? Bone(GmInstance actor, string target) => Find(actor.Scope.ReadOr(target == "TAIL" ? "NEW_TAIL" : target, -4).Int);
    private NativeCharacterFrame Frame(CharacterDefinition definition, NativeCharacterPart part)
    {
        int total = part.Frames.Sum(f => f.Duration);
        int tick = (int)(time() * definition.AnimationFps) % Math.Max(1, total);
        foreach (var frame in part.Frames) { if (tick < frame.Duration) return frame; tick -= frame.Duration; }
        return part.Frames[0];
    }
    private string Path(CharacterDefinition definition, NativeCharacterPart part) => repository.FramePath(Frame(definition, part), registry.CharacterDirectories.GetValueOrDefault(definition.Id));
    internal bool Replace(GmInstance? drawing, Vector2 position, Vector2 scale, float angle, Color tint)
    {
        if (drawing is null) return false;
        int actorId = drawing.Scope.ReadOr("DrawTarget", drawing.Id).Int;
        if (!characters.TryGetValue(actorId, out var definition) || Find(actorId) is not { } actor) return false;
        foreach (var part in definition.Parts.Where(p => p.Visible && p.ReplaceOriginal && p.Frames.Count > 0))
        {
            var bone = Bone(actor, part.Target); if (bone is null) continue;
            if (drawing != bone && !RendererMatches(drawing, part.Target)) continue;
            var point = new Vector2((float)bone.Scope.ReadOr("x").Number, (float)bone.Scope.ReadOr("y").Number);
            if (part.Target != "TAIL" && !point.IsEqualApprox(position)) continue;
            string path = Path(definition, part); if (!File.Exists(path)) continue;
            if (tint.A > 0 && replaced.Add(bone.Id))
            {
                graphics.DrawExternal(path, new(part.AnchorX - part.OffsetX, part.AnchorY - part.OffsetY), position, scale, angle, tint);
                Audit.Add((part.Target, path, position, scale, angle));
            }
            return true;
        }
        return false;
    }
    private bool RendererMatches(GmInstance drawing, string target)
    {
        string suffix = target.StartsWith("HEAD", StringComparison.Ordinal) ? "Head" : target.StartsWith("BODY", StringComparison.Ordinal) ? "Body" : target.StartsWith("HAND", StringComparison.Ordinal) ? "Hand_" + (target.EndsWith("ABOVE", StringComparison.Ordinal) ? "Above" : "Below") : target.StartsWith("FOOT", StringComparison.Ordinal) ? "Foot_" + (target.EndsWith("ABOVE", StringComparison.Ordinal) ? "Above" : "Below") : "Tail";
        return world.Constants.TryGetValue("objCommon_" + suffix, out var kind) && world.IsObject(drawing.Object, kind.Int);
    }
    internal void Decorations(List<GmGraphics.DepthDraw> draws)
    {
        replaced.Clear(); Audit.Clear();
        foreach (var pair in characters.ToArray())
        {
            var actor = Find(pair.Key);
            var definition = bridge is null ? pair.Value : bridge.BoundCharacter(pair.Key);
            if (actor is null || definition is null) { characters.Remove(pair.Key); continue; }
            characters[pair.Key] = definition;
            if (!actor.Active || !actor.Scope.ReadOr("visible", true).Truth) continue;
            foreach (var part in definition.Parts.Where(p => p.Visible && !p.ReplaceOriginal && p.Frames.Count > 0).OrderBy(p => p.Layer))
            {
                var bone = Bone(actor, part.Target);
                if (bone is null || !bone.Scope.ReadOr("visible", true).Truth) continue;
                string path = Path(definition, part); if (!File.Exists(path)) continue;
                var scope = bone.Scope;
                var position = new Vector2((float)scope.ReadOr("x").Number, (float)scope.ReadOr("y").Number);
                float direction = (float)scope.ReadOr("Direction", actor.Scope.ReadOr("Direction", 1)).Number;
                var scale = new Vector2((float)scope.ReadOr("ScaleX", 1).Number * direction, (float)scope.ReadOr("ScaleY", 1).Number);
                float angle = -Mathf.DegToRad((float)scope.ReadOr("image_angle").Number * direction);
                var origin = new Vector2(part.AnchorX - part.OffsetX, part.AnchorY - part.OffsetY);
                var tint = GmGraphics.Colour(scope.ReadOr("image_blend", 16777215).Number, scope.ReadOr("image_alpha", 1).Number);
                if (part.Target == "TAIL" && Bone(actor, scope.ReadOr("OriginPart").Int switch { 1 => "BODY_TOP", 2 => "BODY_BOTTOM", _ => "HEAD" }) is { } attachment)
                {
                    var parent = attachment.Scope; int picture = parent.ReadOr("PartPicture").Int;
                    double radians = (scope.Read("TailPos", 1, picture).Number + parent.ReadOr("image_angle").Number) * Math.PI / 180;
                    double length = scope.Read("TailPos", 0, picture).Number;
                    direction = (float)parent.ReadOr("Direction", 1).Number;
                    position = new((float)(parent.ReadOr("x").Number + direction * Math.Cos(radians) * length), (float)(parent.ReadOr("y").Number - Math.Sin(radians) * length));
                    float sx = scope.ReadOr("NoFlip").Truth ? (float)parent.ReadOr("ScaleX", 1).Number * direction : Math.Abs(scope.ReadOr("TailAngleSpeed").Number) > 10 ? (scope.ReadOr("TailAngleSpeed").Number > 0 ? 1 : -1) : (float)scope.Read("sprite_xscale", scope.ReadOr("Tail").Int).Number;
                    scale = new(sx, (float)parent.ReadOr("ScaleY", 1).Number); angle = -Mathf.DegToRad((float)scope.ReadOr("TailAngle").Number);
                    tint = GmGraphics.Colour(scope.ReadOr("image_blend", 16777215).Number, parent.ReadOr("image_alpha", 1).Number);
                }
                draws.Add(new(scope.ReadOr("depth").Number - part.Layer - 0.01, () => graphics.DrawExternal(path, origin, position, scale, angle, tint)));
                Audit.Add((part.Target, path, position, scale, angle));
            }
        }
    }
}

public sealed partial class GmGraphics
{
    internal NativeCharacterRendering? CharacterRendering;
    private GmInstance? characterDrawingInstance;
    internal bool IsCharacterMainTarget => target < 0;
    internal void ReleaseCharacterPreview()
    {
        Shutdown(); ReleaseNativeTextures(); main.Clear(); tileData.Clear(); textureData.Clear();
        foreach (var texture in textures.Values.Distinct()) texture.Dispose();
        textures.Clear();
        if (particleShapes is not null) foreach (var texture in particleShapes) texture.Dispose();
        particleShapes = null; white?.Dispose(); white = null;
        CharacterRendering = null; AdditionalDrawCommands = null;
    }
}

using Godot;

namespace SlackOfDescent;

public partial class NativeTextureAudit : Node
{
    public override void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ccgame-render-v5-texture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        GmGraphics? graphics = null;
        try
        {
            var world = new GmWorld(ProjectSettings.GlobalizePath("res://GameData"), ProjectSettings.GlobalizePath("res://GameData/assets"), Path.Combine(temporary, "user"));
            graphics = new(this, world);
            GD.Print("NATIVE_TEXTURE_AUDIT_OK checks=", graphics.AuditNativeTextureCache(temporary));
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("NATIVE_TEXTURE_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { graphics?.ReleaseCharacterPreview(); Directory.Delete(temporary, true); }
    }
}

public sealed partial class GmGraphics
{
    internal int AuditNativeTextureCache(string temporary)
    {
        int checks = 0;
        void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        static byte[] Pixels(Texture2D texture) { using var image = texture.GetImage(); image.Convert(Image.Format.Rgba8); return image.GetData(); }
        static void Png(string path, byte red, byte green, byte blue) => NativeContentStore.WriteAtomic(path, NativeCharacterPng.Encode(1, 1, [red, green, blue, 255]));
        static void Manifest(string directory, string id, string? resource, string? character = null) => NativeContentStore.Save(Path.Combine(directory, "mod.json"), new
        {
            formatVersion = 2, id, name = id, version = "1.0.0", content = new
            {
                resources = resource is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [id + ".image"] = resource },
                characters = character is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [id] = character }
            }
        });
        string first = Path.Combine(temporary, "first"), second = Path.Combine(temporary, "second"), character = Path.Combine(temporary, "character");
        foreach (string directory in new[] { first, second, character }) Directory.CreateDirectory(directory);
        string firstPath = Path.Combine(first, "texture.png"), secondPath = Path.Combine(second, "texture.png");
        string characterPath = Path.Combine(character, "characters", "hero", "frame.png");
        Png(firstPath, 255, 0, 0); Png(secondPath, 0, 0, 255); Png(characterPath, 255, 255, 0);
        Manifest(first, "audit.texture.a", "texture.png"); Manifest(second, "audit.texture.b", "texture.png");
        Manifest(character, "audit.texture.character", null, "hero.json");
        NativeContentStore.Save(Path.Combine(character, "hero.json"), new CharacterDefinition
        {
            Id = "audit.texture.character", Parts = [new NativeCharacterPart { Width = 1, Height = 1, Frames = [new NativeCharacterFrame { Png = "characters/hero/frame.png" }] }]
        });
        var registry = new ContentRegistry(); TrackNativeTextures(registry);
        using var manager = new NativeModManager(registry, Path.Combine(temporary, "references"));
        var result = manager.ReloadMany([first, second, character]); Expect(result.Success, "resource and character packages load: " + result.Diagnostics);
        string originalPath = world.Manifest.GetProperty("sprites").EnumerateArray().SelectMany(sprite => sprite.GetProperty("frames").EnumerateArray()).First().GetProperty("path").GetString()!;
        var original = Texture(originalPath); var other = Texture(secondPath); var old = Texture(firstPath); var oldCharacter = Texture(characterPath);
        TexturePixels(old); TexturePixels(oldCharacter);
        DrawExternal(firstPath, Vector2.Zero, Vector2.Zero, Vector2.One, 0);
        Expect(Pixels(old).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "first texture contains actual red Godot pixels");
        Png(firstPath, 0, 255, 0); result = manager.ReloadMany([first]);
        Expect(result.Success && !textures.ContainsKey(firstPath) && !textureData.ContainsKey(old), "successful same-path commit evicts its texture and pixel cache");
        Expect(GodotObject.IsInstanceValid(old) && Pixels(old).SequenceEqual(new byte[] { 255, 0, 0, 255 }), "queued drawing retains its old texture until its references are released");
        var replacement = Texture(firstPath);
        Expect(!ReferenceEquals(old, replacement) && Pixels(replacement).SequenceEqual(new byte[] { 0, 255, 0, 255 }), "successful same-path reload reads new Godot pixels");
        Expect(ReferenceEquals(other, Texture(secondPath)) && ReferenceEquals(original, Texture(originalPath)) && ReferenceEquals(oldCharacter, Texture(characterPath)), "another Mod, original images and unrelated character frames retain their caches");
        Png(firstPath, 255, 0, 255); Manifest(first, "audit.texture.a", "missing.png"); result = manager.ReloadMany([first]);
        Expect(!result.Success && ReferenceEquals(replacement, Texture(firstPath)) && Pixels(replacement).SequenceEqual(new byte[] { 0, 255, 0, 255 }), "failed candidate preserves old pixels despite changed disk contents");
        Manifest(first, "audit.texture.a", "texture.png");
        Png(characterPath, 0, 255, 255); result = manager.ReloadMany([character]);
        Expect(result.Success && !textures.ContainsKey(characterPath) && !textureData.ContainsKey(oldCharacter), "pure character package commit evicts frame paths outside resources");
        Expect(Pixels(Texture(characterPath)).SequenceEqual(new byte[] { 0, 255, 255, 255 }), "same-path character frame reload reads new Godot pixels");
        for (int cycle = 0; cycle < 4; cycle++)
        {
            Begin(); Png(firstPath, (byte)(cycle + 1), 20, 30); result = manager.ReloadMany([first]); Texture(firstPath);
            Expect(result.Success && textures.Count == 4, "repeated same-path reload has bounded texture caches, cycle " + cycle);
            Expect(manager.Unload("audit.texture.a").Success && !textures.ContainsKey(firstPath) && textures.Count == 3, "owner unload removes only its texture, cycle " + cycle);
        }
        Expect(manager.Unload("audit.texture.character").Success && !textures.ContainsKey(characterPath) && ReferenceEquals(other, Texture(secondPath)), "character unload releases frame cache while another Mod remains");
        Expect(manager.Unload("audit.texture.b").Success && textures.Count == 1 && ReferenceEquals(original, Texture(originalPath)), "final Mod unload preserves original texture cache");
        return checks;
    }
}

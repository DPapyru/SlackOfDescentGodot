using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    internal NativeCharacterEditor? ProbeEditor => characterEditor;
    internal SubViewport? ProbeViewport => previewViewport;
    internal GmCanvas? ProbeCanvas => previewCanvas;
    internal NativeCharacterPreview? ProbePreview => isolatedPreview;
    internal void ProbeOpenCharacterEditor() => OpenCharacterEditor();
    internal void ProbeResetPreview() => isolatedPreview?.Reset();
    internal string ProbeBound() => isolatedPreview?.Player is { } player ? isolatedPreview.Bridge.BoundCharacter(player.Id)?.Id ?? "none" : "no-player";
    internal void ProbeApplyPreview() => characterEditor?.ApplyRuntimePreview();
    internal GmInstance? ProbeGamePlayer() => world.Instances.FirstOrDefault(i => !i.Destroyed && world.IsObject(i.Object, world.Constants["objPlayerDoll"].Int));
    internal GmWorld ProbeWorld => world;
    internal bool ProbeWorldReady => world is not null;
    internal async Task ProbeFrames(int count) { for (int index = 0; index < count; index++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
}

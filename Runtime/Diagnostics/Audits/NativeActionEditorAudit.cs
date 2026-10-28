using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditorAudit : Node
{
    public override async void _Ready()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "native-action-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = ProjectSettings.GlobalizePath("res://GameData");
            string user = System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT") ?? Path.Combine(temporary, "user");
            var store = new NativeContentStore(Path.Combine(temporary, "store"));
            NativeLocalization.Initialize(root, user);
            var world = new GmWorld(root, System.Environment.GetEnvironmentVariable("SLACK_SOURCE") ?? Path.Combine(root, "assets"), user);
            var registry = new ContentRegistry();
            int checks = 0;
            void Expect(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Action editor: " + message); }
            Expect(!registry.Actions.ContainsKey("sample.pulse"), "sample actions stay out of the host registry");
            var editor = new NativeActionEditor(world, registry, store) { Visible = false, ForceNative = true };
            AddChild(editor); editor.PopupCentered();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Expect(Find(editor, "动作列表") is ItemList { ItemCount: >= 3 }, "action list is populated with registered and sample actions");
            Expect(Find(editor, "动作目标") is OptionButton { ItemCount: 2 }, "target selector offers player and monster");
            Expect(Find(editor, "动作预览") is TextureRect { Size.Y: >= 200 }, "the preview surface keeps a usable height");
            Expect(NativeActionEditor.PreviewFrame.X * NativeActionEditor.PreviewViewport.Y == NativeActionEditor.PreviewFrame.Y * NativeActionEditor.PreviewViewport.X, "preview frame keeps the port aspect so parts are not stretched");
            Expect(editor.SelectAction("sample.pulse"), "sample action can be selected by id");
            var amount = (SpinBox)editor.ParameterControls["amount"]; var label = (LineEdit)editor.ParameterControls["label"]; var mode = (OptionButton)editor.ParameterControls["mode"];
            Expect(amount.Name == "动作参数_amount" && label.Name == "动作参数_label" && mode.Name == "动作参数_mode", "schema generates named controls per parameter");
            Expect(Math.Abs(amount.Value - 1) < 0.0001 && label.Text == "pulse" && mode.Selected == 0, "controls start from schema defaults");
            amount.Value = 7.5; label.Text = "tap"; mode.Select(2);
            Expect(editor.SaveConfig() == "", "valid parameters save an action config");
            var saved = NativeActionConfig.Load(NativeActionConfig.Path(editor.ConfigDirectory, "sample.pulse"));
            saved.Validate(NativeSamplePulseDefinition);
            Expect(saved.Parameters["amount"] == "7.5" && saved.Parameters["label"] == "tap" && saved.Parameters["mode"] == "hard", "saved config keeps edited values");
            Expect(editor.SelectAction("sample.gravity") && editor.SelectAction("sample.pulse"), "action selection is repeatable");
            Expect(Math.Abs(((SpinBox)editor.ParameterControls["amount"]).Value - 7.5) < 0.0001 && ((LineEdit)editor.ParameterControls["label"]).Text == "tap", "selection restores the saved parameters");
            Expect(!editor.TryValidate(new Dictionary<string, string> { ["amount"] = "1000" }, out string error) && error.Contains("上限", StringComparison.Ordinal), "out of range value is rejected with a readable error");
            Expect(!editor.TryValidate(new Dictionary<string, string> { ["amount"] = "1", ["unknown"] = "2" }, out string error2) && error2.Contains("未知参数", StringComparison.Ordinal), "unknown parameter is rejected");
            string broken = Path.Combine(temporary, "broken"); Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "mod.json"), "{\"formatVersion\":1,\"id\":\"audit.broken\",\"entryType\":\"Broken.Mod\",\"sources\":[\"BrokenMod.cs\"],\"managedDependencies\":[]}");
            File.WriteAllText(Path.Combine(broken, "BrokenMod.cs"), "namespace Broken;\npublic sealed class Mod : SlackOfDescent.IMod\n{\n    public string Id => \"audit.broken\";\n    public void Load(SlackOfDescent.ModContext context) { }\n    public void Unload() { }\n    this is not valid C#\n}\n");
            string diagnostics = editor.CheckCompile([broken]);
            Expect(diagnostics.Contains("BrokenMod.cs", StringComparison.Ordinal), "compile failure reports source diagnostics");
            Expect(Find(editor, "动作状态") is Label { Text.Length: > 0 }, "compile failure leaves the editor responsive");
            Expect(editor.SelectAction("sample.gravity"), "editor survives a failed compile check");
            var formalRegistry=new ContentRegistry();formalRegistry.RegisterAction("sample.pulse",()=>new NativeSamplePulse());
            using(var bridge=new GmNativeBridge(world,formalRegistry))
            {
                var receiver=world.CreateNativeEntity(0,0);
                bridge.BindCharacter(receiver,new(){Actions={{saved.Slot,saved.Action}},ActionConfigs={{saved.Slot,saved}}});
                bridge.Update(new(FrameContext.FixedDelta,0,1,InputSnapshot.Empty));
                Expect(receiver.Scope.ReadOr("actionPulse").Number==7.5&&receiver.Scope.ReadOr("actionPulseLabel").ToString()=="tap"&&receiver.Scope.ReadOr("actionPulseMode").ToString()=="hard","saved action configuration is consumed by formal character binding");
                bridge.UnbindCharacter(receiver.Id);world.Call("instance_destroy",[],new(receiver));world.Instances.Remove(receiver);
            }
            var spin = (SpinBox)editor.ParameterControls["strength"];
            Expect(Find(editor, "动作状态") is Label, "editor exposes a status label for feedback");
            spin.Value = 3.5;
            Expect(editor.ParameterControls.Count > 0 && editor.IsDirty, "changing a parameter marks the editor dirty");
            Expect(editor.DirtyButtonText.Contains("未应用", StringComparison.Ordinal), "the apply button advertises pending parameter changes");
            editor.Apply();
            await editor.SettleAsync(editor.Preview.Player!);
            Expect(!editor.IsDirty, "applying clears the pending marker");
            Expect(Math.Abs(editor.Preview.Player!.Scope.ReadOr("actionGravity").Number - 3.5) < 0.0001, "applying uses the edited parameter value");
            editor.Undo();
            Expect(Math.Abs(((SpinBox)editor.ParameterControls["strength"]).Value - 0.5) < 0.0001, "undo restores the previous parameter value");
            editor.Redo();
            Expect(Math.Abs(((SpinBox)editor.ParameterControls["strength"]).Value - 3.5) < 0.0001, "redo reapplies the changed parameter value");
            ((SpinBox)editor.ParameterControls["strength"]).Value = 2.5;
            var player = await editor.Bind(NativeActionTargets.Player, editor.DescribeDefinition, editor.Values);
            Expect(player.Object == world.Constants["objPlayerDoll"].Int, "player target binds the real player doll");
            await editor.AdvancePreviewAsync(); await editor.AdvancePreviewAsync();
            Expect(Math.Abs(player.Scope.ReadOr("actionGravity").Number - 2.5) < 0.0001, "player action runs inside the isolated preview");
            Expect(editor.Preview.Bridge.Services.Actors.Bound(new(editor.Preview.World, player), CharacterActionSlot.Jump) is not null, "player binding is registered once");
            editor.Preview.World.Engine.Write("view_enabled", 0);
            editor.Preview.World.Engine.Write("view_wview", 640, 0);
            editor.FrameOn(player);
            Expect(editor.Preview.World.Engine.Read("view_enabled").Truth, "preview framing enables the viewport from a disabled state");
            Expect(editor.Preview.World.Engine.Read("view_wport", [0]).Int == NativeActionEditor.PreviewViewport.X && editor.Preview.World.Engine.Read("view_visible", [0]).Truth, "preview framing keeps the view visible at the surface size");
            Expect(editor.Preview.World.Engine.Read("view_wview", [0]).Int == NativeActionEditor.PreviewFrame.X && editor.Preview.World.Engine.Read("view_hview", [0]).Int == NativeActionEditor.PreviewFrame.Y, "preview framing narrows the view to the action frame");
            Expect(Math.Abs(editor.Preview.World.Engine.Read("view_xview", [0]).Number + NativeActionEditor.PreviewFrame.X / 2 - player.Scope.ReadOr("x").Number) <= 1, "preview framing centres the view on the actor");
            Expect(editor.Preview.World.Engine.Read("room_width").Int != NativeActionEditor.PreviewFrame.X && editor.Preview.World.Engine.Read("room_height").Int != NativeActionEditor.PreviewFrame.Y, "preview framing leaves the room bounds untouched");
            ((SpinBox)editor.ParameterControls["strength"]).Value = 3.5;
            var monster = await editor.Bind(NativeActionTargets.Enemy, editor.DescribeDefinition, editor.Values);
            Expect(monster.Object == editor.Preview.World.Constants["objEnemyDoll"].Int, "monster target binds a real enemy doll");
            await editor.AdvancePreviewAsync(); await editor.AdvancePreviewAsync();
            Expect(Math.Abs(monster.Scope.ReadOr("actionGravity").Number - 3.5) < 0.0001, "monster action runs inside the isolated preview");
            Expect(editor.Preview.Bridge.Services.Actors.Bound(new(editor.Preview.World, monster), CharacterActionSlot.Jump) is not null, "monster binding is registered once");
            Expect(editor.Preview.World.Instances.Count(item => item.Object == editor.Preview.World.Constants["objEnemyDoll"].Int) == 1, "repeated applies reuse one monster");
            int before = editor.Preview.Bridge.ActionUpdates;
            await editor.AdvancePreviewAsync();
            Expect(editor.Preview.Bridge.ActionUpdates == before + 2, "the two applied actions update exactly once per frame");
            string previewDirectory = editor.Preview.TemporaryDirectory;
            int hostInstances = world.Instances.Count;
            bool closed = false; editor.Closed += () => closed = true;
            editor.RequestClose();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Expect(closed && !GodotObject.IsInstanceValid(editor), "closing the editor frees the window");
            Expect(!Directory.Exists(previewDirectory), "closing the editor disposes the isolated preview");
            Expect(world.Instances.Count == hostInstances, "the editor never mutates the host world");
            GD.Print("ACTION_EDITOR_AUDIT_OK checks=", checks);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("ACTION_EDITOR_AUDIT_FAILED " + error); GetTree().Quit(1); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private static Node? Find(Node parent, string name) => parent.FindChild(name, true, false);
    private static NativeActionDefinition NativeSamplePulseDefinition => new NativeSamplePulse().Definition;
}

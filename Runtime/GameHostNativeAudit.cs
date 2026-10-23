using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private async void AuditNativeIntegration()
    {
        try
        {
            for(int f=0;f<180;f++)await StepFrameAsync();
            SyncNativeMenu();
            RegressionExpect(nativeMenu!.Visible,"native main menu entries visible");
            nativeMenu.GetChildren().OfType<Button>().Single(b=>b.Text=="C# 模组").EmitSignal(Button.SignalName.Pressed);
            RegressionExpect(modPanel is not null&&modalDepth==1,"mod menu suspends gameplay");
            modPanel!.InstallExamples();
            modPanel.GetChildren().SelectMany(Flatten).OfType<LineEdit>().Single().Text=Path.Combine(contentStore.Mods,"Movement");
            modPanel.ReloadSelected();
            RegressionExpect(content.Actions.Count==3,"mod panel compiles all three movement actions");
            modPanel.Hide();await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            RegressionExpect(modalDepth==0,"mod panel releases modal input");

            nativeMenu.GetChildren().OfType<Button>().Single(b=>b.Text=="角色编辑器").EmitSignal(Button.SignalName.Pressed);
            RegressionExpect(characterEditor is not null&&modalDepth==1,"character editor opened from main menu");
            string output=Path.Combine(contentStore.Root,"integration-pixels");
            string report=characterEditor!.RunUiAudit(output);
            RegressionExpect(content.Characters.Values.Any(c=>c.Parts.Count>=2),"saved editor character immediately registered");
            characterEditor.ApplyRuntimePreview();
            await AuditWait(()=>previewReady&&!previewBusy?Player():null,"actual character preview initialization");
            var previewActor=Player()!;
            RegressionExpect(nativeBridge.BoundCharacter(previewActor.Id) is not null,"preview bound to real GM actor");
            for(int t=0;t<10;t++)
            {
                AdvanceCharacterPreview(new(false,true,t==2,false));
                await AuditWait(()=>!previewBusy?previewActor:null,"preview frame");
            }
            RegressionExpect(previewActor.Scope.ReadOr("x").Number!=96,"preview controls execute actual movement");
            characterEditor.EmitSignal(Window.SignalName.CloseRequested);
            await AuditWait(()=>characterEditor is null&&modalDepth==0?this:null,"character editor return");
            RegressionExpect(world.CurrentRoom==world.Constants["Title"].Int,"character preview returns to title");

            await PrepareTestingRoom();for(int f=0;f<12;f++)await StepFrameAsync();
            nativeAuditUi=true;controls.Clear(world);
            _Input(new InputEventKey{Keycode=Key.V,Pressed=true});
            var vTask=StepFrameAsync();
            var menu=await AuditWait(()=>GetNodeOrNull<PopupMenu>("CharacterMenu"),"real V custom character menu");
            int custom=Enumerable.Range(0,menu.ItemCount).First(i=>menu.GetItemText(i).StartsWith("编辑器：",StringComparison.Ordinal));
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,menu.GetItemId(custom));
            await AuditFinish(vTask,"V full character binding");
            RegressionExpect(nativeBridge.BoundCharacter(Player()!.Id) is not null,"real V menu applies complete binding");
            controls.Clear(world);nativeAuditUi=false;
            await world.LoadRoomAsync(world.Constants["Title"].Int);SyncNativeMenu();
            nativeMenu.GetChildren().OfType<Button>().Single(b=>b.Text=="地图编辑器").EmitSignal(Button.SignalName.Pressed);
            RegressionExpect(mapEditor is not null&&mapEditor.Document.Instances.Count==1227&&mapEditor.Document.Tiles.Count==1647,"map editor imports complete benchmark");
            var doc=mapEditor!.Document;await PlayMap(doc);
            RegressionExpect(mapPlaytest&&modalDepth==0,"map playtest resumes gameplay");
            ReturnToMapEditor();
            await AuditWait(()=>mapEditor.Visible&&modalDepth==1?mapEditor:null,"return to map editor");
            mapEditor.EmitSignal(Window.SignalName.CloseRequested);
            await AuditWait(()=>mapEditor is null&&modalDepth==0?this:null,"close map editor");
            GD.Print("NATIVE_INTEGRATION_OK pixels=",report," tick=",nativeClock.Tick);
            audio.Shutdown();GetTree().Quit();
        }
        catch(Exception e){Fail(e);audio.Shutdown();GetTree().Quit(1);}
    }
    private static IEnumerable<Node> Flatten(Node node)
    {yield return node;foreach(Node child in node.GetChildren())foreach(Node item in Flatten(child))yield return item;}
}

using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    internal async Task AuditKeyboardAsync(string output)
    {
        await KeyAuditReadyAsync();
        Show();
        await KeyAuditTickAsync();
        SetKeyMode(KeyMode.Default);
        document=new(new MotionGraph{Id="audit.keyboard",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,X=4,Y=-16}]}]}],States=[new(){Id="idle",Clip="idle"}]});
        Refresh();frame.Value=0;ApplyDocumentPose();SelectBone("HEAD");
        await AuditKeyModeAsync(output);
        await AuditCanvasUndoAsync();
        AuditKeyboardHelp();
        Hide();
        GD.Print("MOTION_EDITOR_KEYS_OK modes=PASS focus=PASS undo=PASS save=PASS help=PASS");
    }
    private static void KeyAuditExpect(bool ok,string message){if(!ok)throw new InvalidOperationException("Keyboard: "+message);}
    private async Task KeyAuditReadyAsync()
    {
        long start=System.Diagnostics.Stopwatch.GetTimestamp();
        while(busy){if(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds>90)throw new TimeoutException("Keyboard preview initialization");await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
        KeyAuditExpect(preview?.Player is not null,"preview is ready: "+status.Text);
    }
    private async Task KeyAuditTickAsync(){await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
    private PopupMenu KeyAuditViewMenu()
    {
        var button=Descendants(this).OfType<MenuButton>().FirstOrDefault(item=>item.Text=="视图");
        KeyAuditExpect(button is not null,"the menu bar owns the 视图 menu");
        var popup=button!.GetPopup();popup.EmitSignal(PopupMenu.SignalName.AboutToPopup);return popup;
    }
    private int KeyAuditMenuItem(PopupMenu popup,string label)
    {
        for(int i=0;i<popup.ItemCount;i++)if(popup.GetItemText(i)==label)
        {
            KeyAuditExpect(popup.IsItemCheckable(i),"the 视图 menu renders "+label+" as a check item");
            return i;
        }
        throw new InvalidOperationException("Keyboard: the 视图 menu lists "+label);
    }
    private bool KeyAuditMenuChecked(string label){var popup=KeyAuditViewMenu();return popup.IsItemChecked(KeyAuditMenuItem(popup,label));}
    private void KeyAuditChoose(string label){var popup=KeyAuditViewMenu();int index=KeyAuditMenuItem(popup,label);popup.EmitSignal(PopupMenu.SignalName.IdPressed,popup.GetItemId(index));}
    private void KeyAuditSend(InputEvent input)=>PushInput(input,true);
    private void KeyAuditTap(Key key,bool ctrl=false,bool shift=false)
    {
        KeyAuditSend(new InputEventKey{Keycode=key,Pressed=true,CtrlPressed=ctrl,ShiftPressed=shift});
        KeyAuditSend(new InputEventKey{Keycode=key,Pressed=false,CtrlPressed=ctrl,ShiftPressed=shift});
    }
    private Vector2 KeyAuditSurfacePoint(double x,double y){var (surfaceX,surfaceY)=MotionViewport.ToSurface(x,y,surface.Size.X,surface.Size.Y,PreviewView);return new((float)surfaceX,(float)surfaceY);}
    private Vector2 KeyAuditBonePoint(string name)
    {
        var active=PreviewActor!;
        var node=active.Services.Motions.Skeleton(active).Single(item=>item.Name==name);
        var bounds=active.World.Bounds(active.World.Instances.First(item=>item.Id==node.Instance));
        for(double y=bounds.Top+2;y<=bounds.Bottom-2;y+=2)
            for(double x=bounds.Left+2;x<=bounds.Right-2;x+=2)
            {
                var point=KeyAuditSurfacePoint(x,y);
                if(PickBoneAt(point)==name)return point;
            }
        throw new InvalidOperationException("Keyboard audit: no surface point for "+name);
    }
    private async Task AuditKeyModeAsync(string output)
    {
        KeyAuditExpect(keyMode==KeyMode.Default,"the editor starts in default key mode");
        path=Path.Combine(output,"editor.keyboard.motion.json");
        if(File.Exists(path))File.Delete(path);
        KeyAuditTap(Key.S,true);await KeyAuditTickAsync();
        KeyAuditExpect(File.Exists(path),"ctrl+s saves the open document");
        File.Delete(path);
        var focused=frame.GetLineEdit();focused.GrabFocus();await KeyAuditTickAsync();
        KeyAuditExpect(inputContext==EditorInputContext.Text,"focusing a text field enters the text context");
        KeyAuditTap(Key.S,true);await KeyAuditTickAsync();
        KeyAuditExpect(!File.Exists(path),"ctrl+s is ignored while a text field has focus");
        focused.ReleaseFocus();await KeyAuditTickAsync();
        KeyAuditTap(Key.S,true);await KeyAuditTickAsync();
        KeyAuditExpect(File.Exists(path),"ctrl+s saves again after the text field releases focus");
        KeyAuditExpect(KeyAuditMenuChecked("键位：默认")&&!KeyAuditMenuChecked("键位：vim"),"the 视图 menu checks the default key mode");
        KeyAuditChoose("键位：vim");
        KeyAuditExpect(keyMode==KeyMode.Vim&&vimNormal,"switching to vim enters normal mode");
        KeyAuditExpect(NativeContentStore.Load<string>(KeyModePath)=="vim","the key mode persists to the editor settings");
        KeyAuditExpect(KeyAuditMenuChecked("键位：vim")&&!KeyAuditMenuChecked("键位：默认"),"the 视图 menu checks the vim key mode");
        double startFrame=frame.Value;
        KeyAuditTap(Key.L);await KeyAuditTickAsync();
        KeyAuditExpect(Math.Abs(frame.Value-(startFrame+1))<.001,"vim l advances one frame");
        KeyAuditTap(Key.H);await KeyAuditTickAsync();
        KeyAuditExpect(Math.Abs(frame.Value-startFrame)<.001,"vim h steps back one frame");
        KeyAuditTap(Key.D);await KeyAuditTickAsync();KeyAuditTap(Key.D);await KeyAuditTickAsync();
        KeyAuditExpect(Clip.Tracks.First(track=>track.Bone=="HEAD").Keys.All(key=>key.Frame!=startFrame),"vim dd deletes the current frame key");
        KeyAuditTap(Key.U);await KeyAuditTickAsync();
        KeyAuditExpect(Clip.Tracks.First(track=>track.Bone=="HEAD").Keys.Any(key=>key.Frame==startFrame),"vim u undoes dd");
        KeyAuditChoose("键位：默认");
        KeyAuditExpect(keyMode==KeyMode.Default,"choosing 键位：默认 returns to the default mode");
        KeyAuditExpect(NativeContentStore.Load<string>(KeyModePath)=="default","the default key mode persists to the editor settings");
        KeyAuditExpect(KeyAuditMenuChecked("键位：默认")&&!KeyAuditMenuChecked("键位：vim"),"the 视图 menu checks the default key mode again");
        KeyAuditTap(Key.L);await KeyAuditTickAsync();
        KeyAuditExpect(Math.Abs(frame.Value-startFrame)<.001,"default mode leaves l unbound");
    }
    private async Task AuditCanvasUndoAsync()
    {
        document=new(new MotionGraph{Id="audit.keyboard.drag",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,X=4,Y=-16}]}]}],States=[new(){Id="idle",Clip="idle"}]});
        Refresh();frame.Value=0;ApplyDocumentPose();
        var headPoint=KeyAuditBonePoint("HEAD");
        frame.GetLineEdit().GrabFocus();await KeyAuditTickAsync();
        KeyAuditExpect(inputContext==EditorInputContext.Text,"a text field holds the input context before the canvas click");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        await KeyAuditTickAsync();
        KeyAuditExpect(inputContext==EditorInputContext.Canvas&&GetViewport().GuiGetFocusOwner() is null,"a canvas press takes the input context and drops the text focus");
        KeyAuditExpect(dragging,"the canvas press starts a drag");
        var target=headPoint+new Vector2(24,-8);
        SurfaceInput(new InputEventMouseMotion{Position=target});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=target});
        await KeyAuditTickAsync();
        int depth=document.UndoDepth;
        KeyAuditExpect(depth>0,"the canvas drag writes an undo step");
        KeyAuditTap(Key.Z,true);await KeyAuditTickAsync();
        KeyAuditExpect(document.UndoDepth==depth-1,"the routed ctrl+z undoes the canvas drag");
        var restored=Clip.Sample(0)["HEAD"];
        KeyAuditExpect(Math.Abs(restored.X-4)<.001&&Math.Abs(restored.Y+16)<.001,"undo restores the pre-drag pose: "+restored.X.ToString("F2")+","+restored.Y.ToString("F2"));
    }
    private void AuditKeyboardHelp()
    {
        var help=HelpText();
        foreach(string token in new[]{"F 取景","Esc","Ctrl+Z","Ctrl+Shift+Z","Ctrl+S","Ctrl+Space","Alt","中键","滚轮","h / l","j / k","dd","u 撤销","[[frame]]","index = 0","骨骼.字段","content.motions","content.characters","formatVersion 2","PlayMotion","状态机"})
            KeyAuditExpect(help.Contains(token),"help lists "+token);
    }
}

using Godot;
using System.Diagnostics;

namespace SlackOfDescent;

public partial class GameHost
{
    private async Task<T> AuditWait<T>(Func<T?> value,string operation)where T:class
    {
        long started=Stopwatch.GetTimestamp();
        while(value() is not T result)
        {
            if(Stopwatch.GetElapsedTime(started).TotalSeconds>10)throw new InvalidOperationException("Timed out: "+operation);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        return value()!;
    }
    private async Task AuditFinish(Task task,string operation)
    {
        long started=Stopwatch.GetTimestamp();
        while(!task.IsCompleted)
        {
            if(Stopwatch.GetElapsedTime(started).TotalSeconds>10)throw new InvalidOperationException("Timed out: "+operation);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        await task;
    }
    private async void AuditInteractiveControls()
    {
        try
        {
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            var context=new GmContext(new GmInstance());
            var display=world.Select(world.Constants["objDisplay"].Int,context).Single();
            var player=world.Select(world.Constants["objPlayerDoll"].Int,context).Single();
            int originalObject=player.Object;
            Task Open(bool control)
            {
                controls.Clear(world);
                if(control)_Input(new InputEventKey{Keycode=Key.Ctrl,Pressed=true});
                _Input(new InputEventKey{Keycode=Key.V,Pressed=true});return StepFrameAsync();
            }
            async Task<PopupMenu> Menu()=>await AuditWait(()=>GetNodeOrNull<PopupMenu>("CharacterMenu"),"character menu");
            GD.Print("CONTROLS_UI open=cancel");
            var task=Open(false);var menu=await Menu();
            if(menu.GetItemText(0)!="<Input Custom Character Name>"||menu.GetItemText(1)!="<Empty>"||menu.ItemCount<12)throw new InvalidOperationException("Character menu content differs");
            if(task.IsCompleted)throw new InvalidOperationException("Character menu did not suspend game step");
            menu.Hide();await AuditFinish(task,"V cancellation");
            if(display.Scope.Read("selection").Int!=-1)throw new InvalidOperationException("V cancellation index differs");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GD.Print("CONTROLS_UI open=select");
            task=Open(false);menu=await Menu();int nana=-1;
            for(int i=0;i<menu.ItemCount;i++)if(menu.GetItemText(i)=="Character-Nana")nana=menu.GetItemId(i);
            if(nana<2)throw new InvalidOperationException("Nana candidate is missing");
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,nana);menu.Hide();await AuditFinish(task,"V selection");
            if(display.Scope.Read("chara").ToString()!="Character-Nana"||player.Object!=originalObject)throw new InvalidOperationException("V did not retain original appearance loading behavior");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GD.Print("CONTROLS_UI open=part");
            task=Open(true);menu=await Menu();
            if(menu.GetItemText(0)!="Body"||menu.GetItemText(3)!="Head"||!menu.IsItemSeparator(5))throw new InvalidOperationException("Ctrl+V part menu differs");
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,3);menu.Hide();
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GD.Print("CONTROLS_UI open=part-character");
            menu=await Menu();if(menu.GetItemText(0)!="<Input Custom Character Name>")throw new InvalidOperationException("Ctrl+V character menu missing");
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,nana);menu.Hide();await AuditFinish(task,"Ctrl+V selection");
            if(display.Scope.Read("clothingPart").ToString()!="head")throw new InvalidOperationException("Ctrl+V did not load selected part");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            task=Open(false);menu=await Menu();menu.EmitSignal(PopupMenu.SignalName.IdPressed,0);menu.Hide();
            var entryDialog=await AuditWait(()=>GetNodeOrNull<ConfirmationDialog>("CharacterInput"),"character input");
            var entry=entryDialog.GetChildren().OfType<LineEdit>().Single();entry.Text="Character-Nana";entryDialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            await AuditFinish(task,"V name input");
            if(display.Scope.Read("chara").ToString()!="Character-Nana")throw new InvalidOperationException("V name input not loaded");
            GD.Print("CONTROLS_UI V=pass CtrlV=pass cancel=pass name=pass candidates=",nana);
            foreach(int cursor in new[]{0,1,2})
            {
                controls.Clear(world);
                _Input(new InputEventKey{Keycode=Key.Backspace,Pressed=true});
                task=StepFrameAsync();
                for(int i=0;i<4&&task.IsCompleted;i++){await task;task=StepFrameAsync();}
                var pause=await AuditWait(()=>world.Select(world.Constants["objPause"].Int,context).FirstOrDefault(),"pause object");
                int initial=pause.Scope.Read("Cursor").Int;long pausedFrame=world.Frame;
                await AuditWait(()=>pause.Scope.Read("index").Int>=2?pause:null,"pause input loop");
                if(task.IsCompleted||world.Frame!=pausedFrame||initial!=0)throw new InvalidOperationException("Backspace did not suspend original pause loop");
                for(int i=0;i<cursor;i++)
                {
                    int expected=i+1;
                    _Input(new InputEventKey{Keycode=Key.S,Pressed=true});
                    await AuditWait(()=>pause.Scope.Read("Cursor").Int==expected?pause:null,"pause navigation");
                    _Input(new InputEventKey{Keycode=Key.S,Pressed=false});
                    for(int releaseFrame=0;releaseFrame<4;releaseFrame++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                }
                _Input(new InputEventKey{Keycode=Key.Enter,Pressed=true});await AuditFinish(task,"pause decide");
                controls.Clear(world);
                if(cursor<2&&!pause.Destroyed)throw new InvalidOperationException("Pause did not close");
                if(cursor==2&&world.CurrentRoom!=world.Constants["Title"].Int)throw new InvalidOperationException("Pause did not return to title");
                GD.Print("CONTROLS_UI pause=",cursor," pass frame=",world.Frame);
                for(int i=0;i<12&&cursor<2;i++)await StepFrameAsync();
            }
            GD.Print("CONTROLS_UI_OK");audio.Shutdown();GetTree().Quit();
        }
        catch(Exception e){Fail(e);audio.Shutdown();GetTree().Quit(1);}
    }
}

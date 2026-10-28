using Godot;
using System.Diagnostics;

namespace SlackOfDescent;

public partial class GameHost
{
    private void RegressionExpect(bool passed,string message)
    {
        if(!passed)throw new InvalidOperationException("Regression: "+message);
    }
    private async Task RegressionDrive(Task task,string operation,int choice=1)
    {
        long started=Stopwatch.GetTimestamp();
        while(!task.IsCompleted)
        {
            if(Stopwatch.GetElapsedTime(started).TotalSeconds>30)throw new InvalidOperationException("Timed out: "+operation);
            var dialog=GetChildren().OfType<AcceptDialog>().FirstOrDefault(d=>d.Visible);
            if(dialog is not null)
            {
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                if(choice==0)dialog.EmitSignal(Window.SignalName.CloseRequested);
                else dialog.EmitSignal(AcceptDialog.SignalName.CustomAction,choice.ToString());
            }
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        await task;
    }
    private async Task RegressionFrames(int frames,int choice=1)
    {
        for(int i=0;i<frames;i++)await RegressionDrive(StepFrameAsync(),"game step",choice);
    }
    private int RegressionKey(int binding)
    {
        int offset=world.Globals.Read("controllMode").Int==1?12:0;
        return world.Globals.Read("key",[offset+binding]).Int;
    }
    private async Task RegressionTap(int code,int choice=1)
    {
        _Input(new InputEventKey{Keycode=GodotKey(code),Pressed=true});
        await RegressionFrames(1,choice);
        _Input(new InputEventKey{Keycode=GodotKey(code),Pressed=false});
        await RegressionFrames(4,choice);
    }
    private GmInstance RegressionTitle()=>world.Select(world.Constants["objTitle"].Int,new(new GmInstance())).Single();
    private async Task RegressionSelect(string cursor,int choice=1)
    {
        var title=RegressionTitle();int wanted=title.Scope.Read(cursor).Int;
        for(int i=0;i<12&&title.Scope.Read("Cursor").Int!=wanted;i++)await RegressionTap(RegressionKey(3),choice);
        RegressionExpect(title.Scope.Read("Cursor").Int==wanted,"menu cursor "+cursor);
        await RegressionTap(RegressionKey(10),choice);
    }
    private void RegressionView(string view)=>RegressionExpect(RegressionTitle().Scope.Read("View").Equals(RegressionTitle().Scope.Read(view)),"menu view "+view);
    private async Task RegressionReturnTitle(bool checkGrenade=false)
    {
        controls.Clear(world);_Input(new InputEventKey{Keycode=Key.Backspace,Pressed=true});
        var task=StepFrameAsync();
        for(int i=0;i<5&&task.IsCompleted;i++){await task;task=StepFrameAsync();}
        var context=new GmContext(new GmInstance());
        var pause=await AuditWait(()=>world.Select(world.Constants["objPause"].Int,context).FirstOrDefault(),"regression pause");
        await AuditWait(()=>pause.Scope.Read("index").Int>=2?pause:null,"regression pause input");
        if(checkGrenade)
        {
            int before=world.Created.GetValueOrDefault(world.Constants["objNana_Grenade"].Int);long pausedFrame=world.Frame;
            _Input(new InputEventKey{Keycode=Key.G,Pressed=true});
            for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            RegressionExpect(world.Created.GetValueOrDefault(world.Constants["objNana_Grenade"].Int)==before&&world.Frame==pausedFrame,"grenade blocked during pause");
            _Input(new InputEventKey{Keycode=Key.G,Pressed=false});
        }
        _Input(new InputEventKey{Keycode=Key.Backspace,Pressed=false});
        for(int i=1;i<=2;i++)
        {
            _Input(new InputEventKey{Keycode=GodotKey(RegressionKey(3)),Pressed=true});
            await AuditWait(()=>pause.Scope.Read("Cursor").Int==i?pause:null,"regression pause navigation");
            _Input(new InputEventKey{Keycode=GodotKey(RegressionKey(3)),Pressed=false});
            for(int frame=0;frame<4;frame++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        _Input(new InputEventKey{Keycode=Key.Enter,Pressed=true});
        await RegressionDrive(task,"return title");controls.Clear(world);
        await RegressionFrames(12);
        RegressionExpect(world.CurrentRoom==world.Constants["Title"].Int,"return to title");
    }
    private async Task RegressionEnter(string cursor,string destination,int choice=1)
    {
        await RegressionSelect("GAMESTART");RegressionView("VIEW_GAMESTART");
        await RegressionSelect(cursor,choice);await RegressionFrames(45,choice);
        RegressionExpect(world.CurrentRoom==world.Constants[destination].Int,"destination "+cursor+" got "+world.CurrentRoom);
        RegressionExpect(world.Select(world.Constants["objPlayerDoll"].Int,new(new GmInstance())).Any(),"player in "+destination);
        GD.Print("REGRESSION_MENU cursor=",cursor," destination=",destination," choice=",choice," PASS");
    }
    private async void AuditGameplayRegression()
    {
        try
        {
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await RegressionFrames(180);
            world.Globals.Write("gameData",11,[0]);
            for(int round=0;round<(OS.GetCmdlineUserArgs().Contains("--weapons-only")?0:3);round++)
            {
                await RegressionSelect("GAMESTART");await RegressionSelect("CRACKLECRADLE");RegressionView("VIEW_GAMESTART_CC");
                await RegressionSelect("STAGESELECT");RegressionView("VIEW_STAGESELECT");
                await RegressionTap(RegressionKey(1));await RegressionTap(RegressionKey(0));
                await RegressionSelect("STAGESELECT_BACK");RegressionView("VIEW_GAMESTART_CC");
                await RegressionSelect("GAMESTART_CC_BACK");RegressionView("VIEW_GAMESTART");
                await RegressionTap(RegressionKey(11));RegressionView("VIEW_TITLE");
                GD.Print("REGRESSION_MENU StageSelect=PASS Back=PASS round=",round);
                foreach(var entry in new[]{("SURVIVAL","SurvivalWorld"),("PLAYGROUND","Playground"),("SURVIVALPLAYGROUND","Survival")})
                {
                    int choice=entry.Item1=="PLAYGROUND"?new[]{0,2,3}[round]:1;
                    await RegressionEnter(entry.Item1,entry.Item2,choice);
                    await RegressionReturnTitle();
                }
            }
            if(!OS.GetCmdlineUserArgs().Contains("--weapons-only"))
            {
                await RegressionSelect("GAMESTART");await RegressionSelect("CRACKLECRADLE");await RegressionSelect("STAGESELECT");
                await RegressionSelect("STAGE_N");await RegressionFrames(45);
                RegressionExpect(world.CurrentRoom==world.Constants["Stage1_1"].Int,"stage select starts first unlocked level");
                await RegressionReturnTitle();
            }
            await RegressionEnter("PLAYGROUND","Playground");
            await RegressionGrenades();
            await RegressionReturnTitle(true);
            await RegressionBindings();
            GD.Print("GAMEPLAY_REGRESSION_OK");audio.Shutdown();GetTree().Quit();
        }
        catch(Exception e){Fail(e);audio.Shutdown();GetTree().Quit(1);}
    }
    private async Task RegressionBindings()
    {
        GD.Print("REGRESSION_BINDINGS enter");
        await RegressionSelect("OPTION");await RegressionSelect("KEYCONFIG");RegressionView("VIEW_KEYCONFIG");
        SyncGrenadeBinding();RegressionExpect(grenadeBinding!.Visible,"binding button visible in key settings");
        grenadeBinding.EmitSignal(BaseButton.SignalName.Pressed);
        GD.Print("REGRESSION_BINDINGS opened");
        RegressionExpect(bindingDialog is not null,"binding dialog opened");
        bindingDialog!.EmitSignal(Window.SignalName.WindowInput,new InputEventKey{Keycode=Key.H,Pressed=true});
        RegressionExpect(bindingDialog is null&&controls.SecondaryKey==72,"binding dialog saves key");
        var reloaded=new GmInput();reloaded.LoadBindings(world);
        RegressionExpect(reloaded.SecondaryKey==72,"binding survives reload");
        grenadeBinding.EmitSignal(BaseButton.SignalName.Pressed);
        bindingDialog!.EmitSignal(Window.SignalName.WindowInput,new InputEventKey{Keycode=Key.Escape,Pressed=true});
        RegressionExpect(controls.SecondaryKey==72,"binding cancellation preserves key");
        controls.SaveBinding(world,71);controls.Clear(world);
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        GD.Print("REGRESSION_BINDINGS default=G custom=H reload=PASS cancel=PASS");
    }
    private async Task RegressionGrenades()
    {
        controls.SaveBinding(world,71);controls.Clear(world);
        var context=new GmContext(new GmInstance());
        var player=world.Select(world.Constants["objPlayerDoll"].Int,context).Single();
        world.Globals.Write("TItems",1,[11,11]);world.Globals.Write("TItems",1,[12,11]);
        await RegressionTap(RegressionKey(4));await RegressionFrames(90);
        await RegressionTap(RegressionKey(4));await RegressionFrames(90);
        RegressionExpect(world.Globals.Read("Weapon").Equals(world.Constants["WP_NANA_AR"]),$"equip Nana assault rifle current={world.Globals.Read("CurrentWeapon")} weapon={world.Globals.Read("Weapon")} motion={player.Scope.Read("MotionNum")}");
        var gun=world.Select(world.Constants["objNana_ARGun"].Int,context).Single();
        var controller=world.Select(world.Constants["objController"].Int,context).Single();
        int grenade=world.Constants["objNana_Grenade"].Int;
        int Count()=>world.Created.GetValueOrDefault(grenade);
        async Task Fire(bool middle,bool both=false)
        {
            if(!middle||both)_Input(new InputEventKey{Keycode=Key.G,Pressed=true});
            if(middle)_Input(new InputEventMouseButton{ButtonIndex=MouseButton.Middle,Pressed=true});
            await RegressionFrames(1);
        }
        async Task Release()
        {
            _Input(new InputEventKey{Keycode=Key.G,Pressed=false});
            _Input(new InputEventMouseButton{ButtonIndex=MouseButton.Middle,Pressed=false});
            await RegressionFrames(30);
        }
        async Task Reload()
        {
            await RegressionTap(RegressionKey(6));await RegressionFrames(80);
            RegressionExpect(gun.Scope.Read("Grenade").Truth,"reload restores grenade");
        }
        world.Engine.Write("mouse_x",player.Scope.Read("x").Number-300);world.Engine.Write("mouse_y",player.Scope.Read("y").Number-25);
        await RegressionFrames(2);
        int first=Count();double resource=world.Globals.Read("PlayerWP",[2]).Number;
        await Fire(false);
        RegressionExpect(Count()==first+1,"G creates one grenade");
        RegressionExpect(Math.Abs(world.Globals.Read("PlayerWP",[2]).Number-(resource-world.Globals.Read("PlayerWPMAX").Number*.05))<.0001,"grenade resource cost");
        var projectile=world.Select(grenade,context).Single();
        RegressionExpect(projectile.Scope.Read("damage").Int==750,"grenade damage");
        RegressionExpect(projectile.Scope.Read("hspeed").Number<0,"left aiming grenade velocity");
        await RegressionFrames(30);RegressionExpect(Count()==first+1,"holding G does not repeat");
        await Release();await Fire(false);RegressionExpect(Count()==first+1,"unloaded rifle cannot repeat");await Release();
        await Reload();
        world.Engine.Write("mouse_x",player.Scope.Read("x").Number+300);await RegressionFrames(2);
        await Fire(true,true);RegressionExpect(Count()==first+2,"simultaneous G and middle fires once");
        projectile=world.Select(grenade,context).Last();RegressionExpect(projectile.Scope.Read("hspeed").Number>0,"right aiming grenade velocity");
        var enemy=world.Create(world.Constants["objEnemyDoll"].Int,projectile.Scope.Read("x").Number,projectile.Scope.Read("y").Number,run:false);
        enemy.Scope.Write("sprite_index",world.Constants["sprBodyTop"]);enemy.Scope.Write("HP",10000);enemy.Scope.Write("INV",0);enemy.Scope.Write("WEAK",0);enemy.Scope.Write("MotionNum",0);enemy.Scope.Write("DEAD_MOTION",1);
        world.Evaluator.ResetBudget();await world.EventAsync(projectile,4,10);
        RegressionExpect(projectile.Scope.Read("BOOM").Truth,"collision triggers grenade explosion");
        await world.EventAsync(projectile,3,0);
        RegressionExpect(projectile.Destroyed&&enemy.Scope.Read("HP").Int==9250&&enemy.Active,"explosion applies damage once and restores enemy activation");
        enemy.Destroyed=true;await Release();
        await Reload();await Fire(true);RegressionExpect(Count()==first+3,"middle fires grenade");await Release();
        await Reload();resource=world.Globals.Read("PlayerWP",[2]).Number;world.Globals.Write("PlayerWP",0,[2]);
        await Fire(false);RegressionExpect(Count()==first+3,"insufficient resource blocks grenade");await Release();world.Globals.Write("PlayerWP",resource,[2]);
        controls.SaveBinding(world,72);
        await Fire(false);RegressionExpect(Count()==first+3,"old G binding stops firing");await Release();
        _Input(new InputEventKey{Keycode=Key.H,Pressed=true});await RegressionFrames(1);
        RegressionExpect(Count()==first+4,"custom H binding fires grenade");
        _Input(new InputEventKey{Keycode=Key.H,Pressed=false});await RegressionFrames(30);controls.SaveBinding(world,71);
        controller.Scope.Write("ctrl",1);world.Globals.Write("controllMode",1);
        await Reload();await Fire(false);RegressionExpect(Count()==first+5,"keyboard only G fires grenade");await Release();
        controller.Scope.Write("ctrl",0);world.Globals.Write("controllMode",0);
        await Reload();int bullets=world.Created.GetValueOrDefault(world.Constants["objBullet"].Int);
        _Input(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true});await RegressionFrames(5);
        _Input(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false});await RegressionFrames(5);
        RegressionExpect(world.Created.GetValueOrDefault(world.Constants["objBullet"].Int)>bullets,"primary fire still fires bullets");
        await RegressionTap(RegressionKey(4));await RegressionFrames(30);await Fire(false);
        RegressionExpect(Count()==first+5,"other weapon cannot fire rifle grenade");await Release();
        GD.Print("REGRESSION_GRENADES G=PASS middle=PASS hold=PASS reload=PASS resource=PASS other=PASS custom=PASS keyboard=PASS direction=PASS explosion=PASS primary=PASS");
    }
}

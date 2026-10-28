using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private async void AuditNativeJump()
    {
        try
        {
            await RegressionFrames(180);
            var loaded=mods.Reload(Path.Combine(ExamplesDirectory,"Movement"));
            RegressionExpect(loaded.Success,"movement mod compiles: "+loaded.Diagnostics);
            await PrepareTestingRoom();await RegressionFrames(12);
            nativeAuditUi=true;controls.Clear(world);
            _Input(new InputEventKey{Keycode=Key.V,Pressed=true});
            var selection=StepFrameAsync();
            var menu=await AuditWait(()=>GetNodeOrNull<PopupMenu>("CharacterMenu"),"real V triple jump menu");
            int custom=Enumerable.Range(0,menu.ItemCount).Single(i=>menu.GetItemText(i).Contains("Yuki · 三段跳",StringComparison.Ordinal));
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,menu.GetItemId(custom));
            await AuditFinish(selection,"real V triple jump selection");
            controls.Clear(world);nativeAuditUi=false;
            var actor=Player()!;
            RegressionExpect(nativeBridge.BoundCharacter(actor.Id)?.Id=="example.triple-yuki","real menu binds triple Yuki");
            var controller=NativeActorActions.Controller(new(world,actor))!;
            var jumps=new List<double>();
            world.CharacterJumped+=instance=>{if(instance==actor)jumps.Add(instance.Scope.ReadOr("vspeed").Number);};
            bool joyJump=false;double joyY=0;
            long tick=0;
            async Task Tick()
            {
                nativeClock.BeginTick();controls.Poll(world);var input=controls.TakeNativeInput();
                nativeBridge.Update(new(FrameContext.FixedDelta,nativeClock.GameTime,nativeClock.Tick,new(input.Held,input.Pressed,input.Released)));
                if(nativeClock.LegacyDue(world.Engine.Read("room_speed").Number))await StepFrameAsync();
                tick++;
            }
            async Task Tap(int slot)
            {
                int key=NativeActorActions.Key(new(world,actor),slot);
                bool joy=controller.Scope.ReadOr("ctrl").Equals(world.Constants["CTRL_JOYPAD"]);
                if(joy)joyJump=true;else _Input(new InputEventKey{Keycode=GodotKey(key),Pressed=true});
                long before=world.Frame;
                do{await Tick();}while(world.Frame==before);
                if(joy)joyJump=false;else _Input(new InputEventKey{Keycode=GodotKey(key),Pressed=false});
                before=world.Frame;
                do{await Tick();}while(world.Frame==before);
            }
            async Task Land()
            {
                for(int attempt=0;attempt<240;attempt++)
                {
                    await Tick();
                    if(NativeActorActions.Grounded(new(world,actor))&&controller.Scope.ReadOr("JumpNum").Int==2&&actor.Scope.ReadOr("nativeJumpCount").Int==0
                        &&actor.Scope.ReadOr("ChangeMotion").Number==0
                        &&new[]{"STAND_0","STAND_1","CROUCH"}.Any(motion=>NativeActorActions.MotionMatches(new(world,actor),motion)))return;
                }
                throw new InvalidOperationException("triple Yuki did not reset after natural landing: "+string.Join(' ',new[]{"y","vspeed","MotionNum","ChangeMotion","nativeJumpCount","air"}.Select(field=>$"{field}={actor.Scope.ReadOr(field)}"))
                    +$" JumpNum={controller.Scope.ReadOr("JumpNum")} JumpAble={controller.Scope.ReadOr("JumpAble")} HP={world.Globals.ReadOr("PlayerHP")} room={world.CurrentRoom}");
            }
            async Task CheckJumps(string label,int slot=5)
            {
                jumps.Clear();double startY=actor.Scope.ReadOr("y").Number;
                for(int jump=0;jump<4;jump++)
                {
                    await Tap(slot);
                    RegressionExpect(jumps.Count==Math.Min(jump+1,3),label+" request accepted once/no fourth "+jump);
                    RegressionExpect(controller.Scope.ReadOr("JumpNum").Int==Math.Max(0,1-jump),label+" original remaining count "+jump);
                }
                RegressionExpect(jumps.SequenceEqual(new double[]{-18,-24,-24}),label+" original first/aerial impulses: "+string.Join(',',jumps));
                RegressionExpect(actor.Scope.ReadOr("y").Number<startY,label+" actually moves upward");
                GD.Print("NATIVE_JUMP_CASE_OK ",label);
            }
            GD.Print("JUMP_BASELINE player=",actor.Id," controller=",controller.Id," mode=",world.Globals.ReadOr("controllMode")," position=",actor.Scope.ReadOr("x"),",",actor.Scope.ReadOr("y")," air=",actor.Scope.ReadOr("air"));
            double originalAngle=actor.Scope.ReadOr("Angle").Number;
            for(int jump=0;jump<4;jump++)
            {
                await Tap(5);
                GD.Print("JUMP_BASELINE jump=",jump+1," count=",actor.Scope.ReadOr("nativeJumpCount")," remaining=",controller.Scope.ReadOr("JumpNum")," speed=",actor.Scope.ReadOr("vspeed")," motion=",actor.Scope.ReadOr("MotionNum"));
                RegressionExpect(controller.Scope.ReadOr("JumpNum").Int==Math.Max(0,1-jump),"first two requests keep original jump count "+jump);
                RegressionExpect(jumps.Count==Math.Min(jump+1,3),"original jump request accepted exactly once and no fourth "+jump);
                if(jump<3)RegressionExpect(Math.Abs(actor.Scope.ReadOr("Angle").Number-originalAngle)>1e-6,"all three real jumps start rotation "+jump);
            }
            RegressionExpect(jumps.SequenceEqual(new double[]{-18,-24,-24}),"original first and aerial jump velocities preserved");
            await Land();RegressionExpect(Math.Abs(actor.Scope.ReadOr("Angle").Number-originalAngle)<1e-6,"natural landing restores original angle");
            await Land();await CheckJumps("keymouse up",2);
            await Land();world.Globals.Write("controllMode",world.Constants["CTRL_KEY"]);controller.Scope.Write("ctrl",world.Constants["CTRL_KEY"]);
            await CheckJumps("keyboard up",2);
            await Land();world.Globals.Write("key",74,[17]);await CheckJumps("keyboard remapped J");
            await Land();world.Globals.Write("controllMode",world.Constants["CTRL_JOYPAD"]);controller.Scope.Write("ctrl",world.Constants["CTRL_JOYPAD"]);
            controls.JoystickExists=device=>device==1;controls.JoystickButtons=device=>16;
            controls.JoystickButton=(device,button)=>device==1&&button==world.Globals.Read("key",[25]).Int&&joyJump;
            controls.JoystickAxis=(device,axis)=>axis==1?joyY:0;
            await CheckJumps("joypad mapped button");
            await Land();jumps.Clear();joyY=-1;
            for(int i=0;i<4;i++)await Tick();
            RegressionExpect(jumps.Count==0&&controller.Scope.ReadOr("JumpNum").Int==2,"joypad up keeps original no-jump behavior");
            joyY=0;for(int i=0;i<4;i++)await Tick();
            world.Globals.Write("controllMode",world.Constants["CTRL_KEYMOUSE"]);controller.Scope.Write("ctrl",world.Constants["CTRL_KEYMOUSE"]);
            jumps.Clear();await Tap(5);await Tap(5);actor.Scope.Write("BINDED",1);await Tap(5);
            RegressionExpect(jumps.Count==2&&controller.Scope.ReadOr("JumpNum").Int==0,"binding blocks extra jump without granting count");
            actor.Scope.Write("BINDED",0);actor.Scope.Write("MotionNum",actor.Scope.Read("JUMPUP2"));actor.Scope.Write("ChangeMotion",0);await Tap(5);
            RegressionExpect(jumps.Count==3,"binding did not consume extra jump");
            await Land();jumps.Clear();await Tap(5);await Tap(5);
            actor.Scope.Write("MotionNum",actor.Scope.Read("DAMAGE_0"));actor.Scope.Write("ChangeMotion",0);actor.Scope.Write("KeyFrmNum",0);await Tap(5);
            RegressionExpect(jumps.Count==2&&controller.Scope.ReadOr("JumpNum").Int==0,"damage motion blocks extra jump");
            actor.Scope.Write("MotionNum",actor.Scope.Read("JUMPUP2"));actor.Scope.Write("ChangeMotion",0);await Tap(5);
            RegressionExpect(jumps.Count==3,"damage rejection did not consume extra jump");
            world.Evaluator.ResetBudget();await world.Evaluator.CallAsync("Respawn",[96,448],new(actor));
            for(int i=0;i<30;i++)await Tick();
            RegressionExpect(Player()==actor&&nativeBridge.BoundCharacter(actor.Id)?.Id=="example.triple-yuki","original restart preserves selected actor and binding");
            RegressionExpect(!world.Instances.Contains(controller),"original restart replaces nonpersistent controller");
            controller=NativeActorActions.Controller(new(world,actor))!;
            await Land();await CheckJumps("original restart");
            await Land();nativeBridge.UnbindCharacter(actor.Id);jumps.Clear();
            for(int i=0;i<4;i++)await Tap(5);
            RegressionExpect(jumps.SequenceEqual(new double[]{-18,-24})&&controller.Scope.ReadOr("JumpNum").Int==0,"unbound original keeps two jumps and original impulses");
            await Land();jumps.Clear();for(int i=0;i<4;i++)await Tap(2);
            RegressionExpect(jumps.SequenceEqual(new double[]{-18,-24})&&controller.Scope.ReadOr("JumpNum").Int==0,"unbound original up keeps two jumps and original impulses");
            GD.Print("NATIVE_JUMP_AUDIT_OK ticks=",tick);
            audio.Shutdown();GetTree().Quit();
        }
        catch(Exception error){Fail(error);audio.Shutdown();GetTree().Quit(1);}
    }
}

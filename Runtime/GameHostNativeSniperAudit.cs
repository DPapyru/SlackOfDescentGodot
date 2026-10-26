using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private async void AuditNativeSniper()
    {
        try
        {
            await RegressionFrames(180);
            string sniperProject=Path.Combine(contentStore.Projects,"SniperAudit");Directory.CreateDirectory(sniperProject);
            foreach(string path in Directory.EnumerateFiles(Path.Combine(ExamplesDirectory,"TestMod")))File.Copy(path,Path.Combine(sniperProject,Path.GetFileName(path)),true);
            var loaded=mods.Reload(sniperProject);
            RegressionExpect(loaded.Success,"sniper mod compiles: "+loaded.Diagnostics);
            await RegressionSelect("OPTION");await RegressionSelect("KEYCONFIG");RegressionView("VIEW_KEYCONFIG");
            SyncGrenadeBinding();SyncNativeBindings();
            await AuditNativeBindingsLocalization();
            RegressionExpect(nativeBindingsButton!.Visible,"named operation binding entry appears in original key settings");
            nativeBindingsButton.EmitSignal(BaseButton.SignalName.Pressed);
            var binding=nativeBindingsWindow!.FindChildren("*","Button",true,false).OfType<Button>().SingleOrDefault(button=>button.GetMeta("action_id","").AsString()=="testmod.reload");
            RegressionExpect(binding is not null,"named reload operation creates a real binding row");
            binding!.EmitSignal(BaseButton.SignalName.Pressed);
            nativeBindingsWindow.EmitSignal(Window.SignalName.WindowInput,new InputEventKey{Keycode=Key.T,Pressed=true});
            RegressionExpect(nativeBridge.Services.Input.GetKey("testmod.reload")==84&&new NativeInputService(content,world.UserRoot).GetKey("testmod.reload")==84,"binding UI saves and persisted service reads T");
            _Process(0);RegressionExpect(nativeBridge.Services.Input.Context==NativeInputContext.Menu,"binding window uses menu input context");
            nativeBindingsWindow.EmitSignal(Window.SignalName.CloseRequested);
            await PrepareTestingRoom();await RegressionFrames(12);
            _Process(0);RegressionExpect(nativeBridge.Services.Input.Context==NativeInputContext.Game,"play room restores game input context");
            nativeAuditUi=true;controls.Clear(world);
            _Input(new InputEventKey{Keycode=Key.V,Pressed=true});
            var selection=StepFrameAsync();
            var menu=await AuditWait(()=>GetNodeOrNull<PopupMenu>("CharacterMenu"),"real V sniper menu");
            int custom=Enumerable.Range(0,menu.ItemCount).Single(i=>menu.GetItemText(i).Contains("Nana · 狙击枪",StringComparison.Ordinal));
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,menu.GetItemId(custom));
            await AuditFinish(selection,"real V sniper selection");
            controls.Clear(world);nativeAuditUi=false;
            var actor=Player()!;
            RegressionExpect(nativeBridge.BoundCharacter(actor.Id)?.Id=="testmod.nana-sniper","real V menu binds sniper Nana");
            var controller=NativeActorActions.Controller(new(world,actor))!;
            int bullets=world.Created.GetValueOrDefault(world.Constants["objBullet"].Int);
            long ticks=0;
            int observedShots=0;NativeLine? latestShot=null;double latestShotAngle=0;Exception? shotFailure=null;
            content.EventHandlers.Add("audit.sniper.sound",new("audit","weapon.fire",NativeEventPhase.After,value=>
            {
                try
                {
                    if(value.Actor?.Id!=actor.Id||actor.Scope.ReadOr("sniperShots").Int==observedShots)return;
                    observedShots=actor.Scope.ReadOr("sniperShots").Int;
                    var owner=new ActorContext(world,actor);var hand=owner.Part("HAND3_ABOVE")??owner;
                    latestShotAngle=controller.Scope.ReadOr("m_dir").Number;double radians=latestShotAngle*Math.PI/180;
                    var muzzle=new NativePartPose(hand.X-5*Math.Cos(radians),hand.Y+5*Math.Sin(radians),latestShotAngle,1,Math.Cos(radians)<0?-1:1).Transform(64,-3);
                    latestShot=nativeBridge.Services.Presentation.Lines.Single(line=>line.Owner==actor.Id&&line.Id=="shot");
                    RegressionExpect(Math.Abs(latestShot.X1-muzzle.X)<1e-7&&Math.Abs(latestShot.Y1-muzzle.Y)<1e-7,"real shot line starts at transformed recoiling muzzle");
                    RegressionExpect(owner.Call("sound_exists",world.Constants["sndBullet"]).Truth&&owner.Call("sound_isplaying",world.Constants["sndBullet"]).Truth,"real shot plays decoded original bullet sound in the same callback");
                }
                catch(Exception error){shotFailure=error;}
            }));
            async Task Tick()
            {
                nativeClock.BeginTick();controls.Poll(world);var input=controls.TakeNativeInput();
                controller=NativeActorActions.Controller(new(world,actor))??controller;
                double previousReload=actor.Scope.ReadOr("sniperReload").Number;
                nativeBridge.Update(new(FrameContext.FixedDelta,nativeClock.GameTime,nativeClock.Tick,new(input.Held,input.Pressed,input.Released)));
                if(previousReload<=0&&actor.Scope.ReadOr("sniperReload").Number>0)
                {
                    var owner=new ActorContext(world,actor);
                    RegressionExpect(owner.Call("sound_exists",world.Constants["sndReload"]).Truth&&owner.Call("sound_isplaying",world.Constants["sndReload"]).Truth,"real reload plays decoded original reload sound on its first tick");
                }
                if(nativeBridge.Services.Presentation.Sprites.SingleOrDefault(sprite=>sprite.Owner==actor.Id&&sprite.Id=="sniper") is {} visual)
                {
                    var owner=new ActorContext(world,actor);var hand=owner.Part("HAND3_ABOVE")??owner;double radians=controller.Scope.ReadOr("m_dir").Number*Math.PI/180;
                    double dx=visual.X-hand.X,dy=visual.Y-hand.Y;
                    RegressionExpect(Math.Abs(dx*Math.Sin(radians)+dy*Math.Cos(radians))<1e-7&&dx*dx+dy*dy<=25.000001
                        &&visual.Angle==controller.Scope.ReadOr("m_dir").Number&&visual.ScaleX==1&&visual.ScaleY==(Math.Cos(radians)<0?-1:1)
                        &&visual.OriginX==18&&visual.OriginY==7,"live gun follows hand attachment, aim direction, facing and short recoil");
                }
                if(nativeClock.LegacyDue(world.Engine.Read("room_speed").Number))await StepFrameAsync();
                if(shotFailure is not null)throw shotFailure;
                ticks++;
            }
            async Task Frames(int count){for(int i=0;i<count;i++)await Tick();}
            async Task Fire(bool mouse=true,int hold=3)
            {
                if(mouse)_Input(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true});
                else _Input(new InputEventKey{Keycode=GodotKey(NativeActorActions.Key(new(world,actor),4)),Pressed=true});
                await Frames(hold);
                if(mouse)_Input(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false});
                else _Input(new InputEventKey{Keycode=GodotKey(NativeActorActions.Key(new(world,actor),4)),Pressed=false});
                await Frames(30);
            }
            async Task Ready()
            {
                for(int i=0;i<240;i++)
                {
                    await Tick();
                    if(NativeActorActions.IsLocomotion(new(world,actor))&&actor.Scope.ReadOr("ChangeMotion").Number==0&&actor.Scope.ReadOr("sniperCooldown").Number<=1e-9)return;
                }
                throw new InvalidOperationException("sniper did not return to locomotion: "+string.Join(' ',new[]{"MotionNum","ChangeMotion","ChangeMotionNum","sniperShots","sniperAmmunition"}.Select(f=>$"{f}={actor.Scope.ReadOr(f)}")));
            }
            await Frames(90);
            world.Engine.Write("mouse_x",actor.Scope.ReadOr("x").Number+400);world.Engine.Write("mouse_y",actor.Scope.ReadOr("y").Number-40);
            await Frames(6);
            GD.Print("SNIPER_BASELINE ",string.Join(' ',new[]{"MotionNum","ChangeMotion","BINDED","HALFBINDED","sniperAmmunition"}.Select(f=>$"{f}={actor.Scope.ReadOr(f)}"))," Aim=",controller.Scope.ReadOr("Aim")," Weapon=",world.Globals.ReadOr("CurrentWeapon"));
            await Fire(hold:150);
            GD.Print("SNIPER_HELD shots=",actor.Scope.ReadOr("sniperShots")," ammo=",actor.Scope.ReadOr("sniperAmmunition")," motion=",actor.Scope.ReadOr("MotionNum"));
            RegressionExpect(actor.Scope.ReadOr("sniperShots").Int==1&&actor.Scope.ReadOr("sniperAmmunition").Int==4,"holding mouse through cooldown fires exactly once through original Nana animation");
            RegressionExpect(world.Created.GetValueOrDefault(world.Constants["objBullet"].Int)==bullets,"sniper original fire produces no vanilla bullet");
            RegressionExpect(nativeBridge.Services.Presentation.Hud.Single(item=>item.Owner==actor.Id).Text.Contains("4/5",StringComparison.Ordinal),"sniper HUD shows remaining five-round magazine");
            var gun=world.Instances.Single(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objNana_Gun"].Int));
            RegressionExpect(gun.Scope.ReadOr("Bullet").Int==4,"original Nana gun mirrors custom ammunition");
            await Ready();await Fire();
            RegressionExpect(actor.Scope.ReadOr("sniperShots").Int==2,"second distinct mouse press fires");
            await Fire();await Frames(90);
            RegressionExpect(actor.Scope.ReadOr("sniperShots").Int==2,"cooldown press is rejected without delayed queued fire");
            world.Globals.Write("controllMode",world.Constants["CTRL_KEY"]);controller.Scope.Write("ctrl",world.Constants["CTRL_KEY"]);
            await Ready();await Fire(false);
            RegressionExpect(actor.Scope.ReadOr("sniperShots").Int==3,"keyboard attack uses original Nana animation");
            async Task Reload(int key=84)
            {
                _Input(new InputEventKey{Keycode=GodotKey(key),Pressed=true});await Frames(1);
                _Input(new InputEventKey{Keycode=GodotKey(key),Pressed=false});await Frames(1);
            }
            await Ready();await Reload(82);
            RegressionExpect(actor.Scope.ReadOr("sniperReload").Number==0,"rebound named reload ignores original R");
            await Reload();
            RegressionExpect(actor.Scope.ReadOr("sniperReload").Number>2.4,"rebound T starts a 2.5 second reload");
            var magazine=nativeBridge.Services.Entities.Query("testmod.magazine").Single();double magazineX=magazine.X;
            await Frames(1);
            RegressionExpect(magazine.HorizontalSpeed==0&&magazine.VerticalSpeed==0&&Math.Abs(magazine.X-magazineX)<2,"magazine moves once with private velocity and no legacy speed drift");
            RegressionExpect(nativeBridge.Services.Presentation.Sprites.Count(sprite=>sprite.Owner==magazine.Id)==1
                &&nativeBridge.Services.Presentation.Sprites.Single(sprite=>sprite.Owner==magazine.Id).OriginX==4,"live ejected magazine has exactly one behavior-controlled visual");
            int remaining=(int)Math.Ceiling(actor.Scope.ReadOr("sniperReload").Number/FrameContext.FixedDelta)-1;
            await Frames(remaining);
            RegressionExpect(actor.Scope.ReadOr("sniperAmmunition").Int==2&&actor.Scope.ReadOr("sniperReload").Number>0,"ammunition stays unchanged until final reload tick");
            await Frames(1);
            RegressionExpect(actor.Scope.ReadOr("sniperAmmunition").Int==5&&actor.Scope.ReadOr("sniperReload").Number==0&&gun.Scope.ReadOr("Bullet").Int==5,"final reload tick fills five rounds and original gun state");
            world.Globals.Write("controllMode",world.Constants["CTRL_KEYMOUSE"]);controller.Scope.Write("ctrl",world.Constants["CTRL_KEYMOUSE"]);
            int beforeEmpty=actor.Scope.ReadOr("sniperShots").Int;
            foreach(int angle in new[]{0,180,90,270,0})
            {
                await Ready();double radians=angle*Math.PI/180;
                world.Engine.Write("mouse_x",actor.Scope.ReadOr("x").Number+400*Math.Cos(radians));world.Engine.Write("mouse_y",actor.Scope.ReadOr("y").Number-400*Math.Sin(radians));
                await Frames(6);await Fire();
                RegressionExpect(Math.Abs(Math.IEEERemainder(latestShotAngle-angle,360))<15,"actual original aim and sniper muzzle cover direction "+angle);
            }
            await Frames(180);await Fire(hold:90);
            RegressionExpect(actor.Scope.ReadOr("sniperShots").Int==beforeEmpty+5&&actor.Scope.ReadOr("sniperAmmunition").Int==0
                &&actor.Scope.ReadOr("sniperReload").Number==0&&gun.Scope.ReadOr("Bullet").Int==0
                &&world.Created.GetValueOrDefault(world.Constants["objBullet"].Int)==bullets,"five real shots exhaust the magazine without vanilla automatic reload, fixed refill or extra bullets");
            await Reload();await Frames(150);await Ready();
            RegressionExpect(actor.Scope.ReadOr("sniperAmmunition").Int==5,"explicit reload restores five rounds after an empty magazine");
            world.Globals.Write("controllMode",world.Constants["CTRL_KEY"]);controller.Scope.Write("ctrl",world.Constants["CTRL_KEY"]);
            await Ready();await Fire(false);await Ready();await Reload();
            actor.Scope.Write("MotionNum",actor.Scope.Read("DAMAGE_0"));actor.Scope.Write("ChangeMotion",0);actor.Scope.Write("KeyFrmNum",0);
            await Frames(1);
            RegressionExpect(actor.Scope.ReadOr("sniperReload").Number==0&&actor.Scope.ReadOr("sniperAmmunition").Int==4,"damage motion cancels reload without adding ammunition");
            await Frames(180);await Ready();await Reload();world.Globals.Write("CurrentWeapon",1);await Frames(1);
            RegressionExpect(actor.Scope.ReadOr("sniperReload").Number==0,"switching away from equipped weapon cancels reload");
            world.Globals.Write("CurrentWeapon",0);await Frames(2);
            await Reload();int ammunition=actor.Scope.ReadOr("sniperAmmunition").Int;
            await world.LoadRoomAsync(world.CurrentRoom);await Frames(20);
            RegressionExpect(Player()==actor&&actor.Scope.ReadOr("sniperAmmunition").Int==ammunition&&actor.Scope.ReadOr("sniperReload").Number==0,"ordinary room change preserves magazine and cancels reload");
            controller=NativeActorActions.Controller(new(world,actor))!;
            world.Evaluator.ResetBudget();await world.Evaluator.CallAsync("Respawn",[96,448],new(actor));await Frames(30);
            RegressionExpect(Player()==actor&&actor.Scope.ReadOr("sniperAmmunition").Int==5,"original Respawn restores five rounds on persistent character");
            controller=NativeActorActions.Controller(new(world,actor))!;
            world.Globals.Write("controllMode",world.Constants["CTRL_KEYMOUSE"]);controller.Scope.Write("ctrl",world.Constants["CTRL_KEYMOUSE"]);
            var fixtures=new List<GmInstance>();
            GmInstance Fixture(string type)
            {
                var instance=world.Create(world.Constants[type].Int,0,0,run:false);instance.Active=false;
                instance.Scope.Write("mask_index",world.Constants["sprMask"]);instance.Scope.Write("DAMAGE_ACTIVE",1);
                instance.Scope.Write("HP",1000);instance.Scope.Write("INV",0);instance.Scope.Write("WEAK",0);
                instance.Scope.Write("MotionNum",0);instance.Scope.Write("DEAD_MOTION",1);instance.Scope.Write("BindTarget",-4);
                instance.Scope.Write("qsIs",0);instance.Scope.Write("LastGravity",0);
                fixtures.Add(instance);return instance;
            }
            var nearEnemy=Fixture("objEnemyDoll");var behindEnemy=Fixture("objEnemyDoll");var barrier=Fixture("objBlock");barrier.Scope.Write("solid",1);
            var previousSuppressCode=world.SuppressCode;
            world.SuppressCode=(key,context)=>fixtures.Contains(context.Self)||previousSuppressCode?.Invoke(key,context)==true;
            var geometry=world.Instances.Where(instance=>instance.Active&&!instance.Destroyed&&(instance.Scope.ReadOr("solid").Truth
                ||world.IsObject(instance.Object,world.Constants["objBlock"].Int)||world.IsObject(instance.Object,world.Constants["objEnemyDoll"].Int))).ToArray();
            int scenario=0;
            content.EventHandlers.Add("audit.sniper.geometry-before",new("audit","weapon.fire",NativeEventPhase.Before,_=>
            {
                var owner=new ActorContext(world,actor);var hand=owner.Part("HAND3_ABOVE")??owner;double radians=controller.Scope.ReadOr("m_dir").Number*Math.PI/180;
                foreach(var instance in geometry)instance.Active=false;
                void Place(GmInstance instance,double distance)
                {
                    instance.Scope.Write("x",hand.X+distance*Math.Cos(radians));instance.Scope.Write("y",hand.Y-distance*Math.Sin(radians));instance.Active=true;
                }
                Place(nearEnemy,scenario==0?160:300);Place(behindEnemy,400);Place(barrier,scenario==0?250:scenario==1?160:30);
            }));
            content.EventHandlers.Add("audit.sniper.geometry-after",new("audit","weapon.fire",NativeEventPhase.After,_=>
            {
                foreach(var instance in fixtures)
                {instance.Active=false;instance.Scope.Write("hspeed",0);instance.Scope.Write("vspeed",0);instance.Scope.Write("speed",0);}
                foreach(var instance in geometry)instance.Active=true;
            }));
            for(scenario=0;scenario<3;scenario++)
            {
                await Ready();world.Engine.Write("mouse_x",actor.Scope.ReadOr("x").Number+400);world.Engine.Write("mouse_y",actor.Scope.ReadOr("y").Number-40);await Frames(6);
                nearEnemy.Scope.Write("HP",1000);behindEnemy.Scope.Write("HP",1000);
                int before=actor.Scope.ReadOr("sniperShots").Int;await Fire();
                var stop=world.Bounds(scenario==0?nearEnemy:barrier);
                RegressionExpect(actor.Scope.ReadOr("sniperShots").Int==before+1&&nearEnemy.Scope.ReadOr("HP").Int==(scenario==0?700:1000)
                    &&behindEnemy.Scope.ReadOr("HP").Int==1000&&latestShot is {} line
                    &&line.X2>=stop.Left-1e-7&&line.X2<=stop.Right+1e-7&&line.Y2>=stop.Top-1e-7&&line.Y2<=stop.Bottom+1e-7,
                    scenario==0?"real rifle damages only nearest enemy before wall":scenario==1?"real rifle wall stops damage to both enemies":"wall inside real rifle barrel stops muzzle pre-ray before farther enemies");
            }
            content.EventHandlers.Remove("audit.sniper.geometry-before");content.EventHandlers.Remove("audit.sniper.geometry-after");
            foreach(var instance in fixtures)instance.Destroyed=true;
            world.SuppressCode=previousSuppressCode;
            await Ready();await Fire();await Ready();
            ammunition=actor.Scope.ReadOr("sniperAmmunition").Int;
            string sourceFile=Path.Combine(sniperProject,"SniperMod.cs"),source=File.ReadAllText(sourceFile);
            File.WriteAllText(sourceFile,source.Replace("public sealed class SniperWeapon : ICharacterWeapon\n{","public sealed class SniperWeapon : ICharacterWeapon\n{\n public SniperWeapon(){throw new InvalidOperationException(\"expected constructor failure\");}",StringComparison.Ordinal));
            var rejected=mods.Reload(sniperProject);File.WriteAllText(sourceFile,source);await Frames(1);
            RegressionExpect(!rejected.Success&&actor.Scope.ReadOr("sniperAmmunition").Int==ammunition,"factory construction failure retains live weapon and ammunition");
            Action rejectObserver=()=>throw new InvalidOperationException("expected post-bridge observer failure");content.Changed+=rejectObserver;
            try{rejected=mods.Reload(sniperProject);}finally{content.Changed-=rejectObserver;}
            await Frames(1);
            RegressionExpect(!rejected.Success&&actor.Scope.ReadOr("sniperAmmunition").Int==ammunition,"later Changed observer failure retains old weapon instance and magazine");
            var reloaded=mods.Reload(sniperProject);await Frames(1);
            RegressionExpect(reloaded.Success&&actor.Scope.ReadOr("sniperAmmunition").Int==5,"successful reload replaces weapon with fresh five-round magazine");
            await Ready();await Fire();await Ready();await Reload();
            var unloaded=mods.Unload("example.nana-sniper");await Frames(1);
            RegressionExpect(unloaded.Success&&nativeBridge.BoundCharacter(actor.Id) is null&&nativeBridge.Services.Entities.Query("testmod.magazine").Count==0
                &&!nativeBridge.Services.Presentation.Hud.Any(item=>item.Owner==actor.Id)&&!nativeBridge.Services.Presentation.Sprites.Any(item=>item.Owner==actor.Id)
                &&!content.EventHandlers.Keys.Any(key=>key.StartsWith("testmod.",StringComparison.Ordinal))
                &&!content.Translations.Keys.Any(key=>key.Contains("/testmod.",StringComparison.Ordinal)),"unload removes binding, magazine, HUD, presentation, subscriptions and package translations");
            await Frames(90);int vanilla=world.Created.GetValueOrDefault(world.Constants["objBullet"].Int);await Fire();
            RegressionExpect(world.Created.GetValueOrDefault(world.Constants["objBullet"].Int)>vanilla,"unload restores vanilla gun fire through same mouse path");
            var library=new NativeModLibrary(contentStore,mods);string archive=Path.Combine(contentStore.Root,"SniperAudit.zip");library.Export(sniperProject,archive,true);
            var installed=library.Install(archive,true);var migrated=mods.Reload(installed.Directory);
            RegressionExpect(migrated.Success&&content.Resources["testmod.sniper"].StartsWith(installed.Directory+Path.DirectorySeparatorChar,StringComparison.Ordinal),"ZIP export and installation recompile with migrated resource paths");
            nativeAuditUi=true;controls.Clear(world);_Input(new InputEventKey{Keycode=Key.V,Pressed=true});selection=StepFrameAsync();
            menu=await AuditWait(()=>GetNodeOrNull<PopupMenu>("CharacterMenu"),"migrated sniper V menu");
            custom=Enumerable.Range(0,menu.ItemCount).Single(i=>menu.GetItemText(i).Contains("Nana · 狙击枪",StringComparison.Ordinal));
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,menu.GetItemId(custom));await AuditFinish(selection,"migrated sniper V selection");
            controls.Clear(world);nativeAuditUi=false;actor=Player()!;await Frames(1);
            RegressionExpect(nativeBridge.BoundCharacter(actor.Id)?.Id=="testmod.nana-sniper"&&actor.Scope.ReadOr("sniperAmmunition").Int==5,"ZIP migrated sniper remains selectable through actual V menu");
            mods.Unload("example.nana-sniper");await Frames(1);
            RegressionExpect(!content.Translations.Keys.Any(key=>key.Contains("/testmod.",StringComparison.Ordinal)),"migrated package unload removes its language contributions");
            content.EventHandlers.Remove("audit.sniper.sound");
            RegressionExpect(mods.RetiredContexts.All(context=>NativeModManager.VerifyUnloaded(context)),"successful, rejected and migrated mod contexts are collectible after unload");
            GD.Print("NATIVE_SNIPER_AUDIT_OK ticks=",ticks);
            audio.Shutdown();GetTree().Quit();
        }
        catch(Exception error){Fail(error);audio.Shutdown();GetTree().Quit(1);}
    }
}

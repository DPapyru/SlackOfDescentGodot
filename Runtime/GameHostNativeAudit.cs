using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private bool nativeMenuWindowAudit;
    private int nativeMenuWindowPaints;
    private async void AuditNativeIntegration()
    {
        try
        {
            nativeMenuWindowAudit=OS.GetCmdlineUserArgs().Contains("--native-menu-window");
            if(nativeMenuWindowAudit)
            {
                RegressionExpect(DisplayServer.GetName()!="headless","menu window audit requires a display");
                GetWindow().Title="SlackOfDescent Menu Window Test";canvas.Paint=PaintNativeMenuWindow;
            }
            for(int f=0;f<180;f++)await StepFrameAsync();
            SyncNativeUi();
            if(OS.GetCmdlineUserArgs().Contains("--native-menu-reload"))
            {
                RegressionExpect(nativeMenuWindowAudit&&!NativeLocalization.IsChinese,"fresh process loads saved English language");
                await RegressionSelect("OPTION");RegressionView("VIEW_OPTION");
                await AuditNativeMenuDraw(("语言",14),("English",14),("KeyConfig",46));
                GD.Print("NATIVE_MENU_RELOAD_OK language=en");audio.Shutdown();GetTree().Quit();return;
            }
            await AuditNativeMenuDraw(("GameStart",312),("Option",344),("Mod",376),("Exit",408));
            var logo=graphics.SpriteGeometryAudit!.Single(s=>s.Sprite==world.Constants["sprTitleSurvival"].Int);
            RegressionExpect(logo.Scale==new Vector2(0.75f,0.75f)&&logo.Position.Y+342*logo.Scale.Y<312,"scaled logo leaves a gap above menu");
            RegressionExpect(Mathf.IsEqualApprox(logo.Position.X+(481*logo.Scale.X/2),world.OutputSize().Width/2f),"scaled logo remains centered");
            RegressionExpect(GetNodeOrNull<Control>("NativeMainMenu") is null,"top-left menu entries removed");
            await RegressionSelect("OPTION");RegressionView("VIEW_OPTION");
            await AuditNativeMenuDraw(("语言",14),("简体中文",14),("KeyConfig",46));
            RegressionExpect(RegressionTitle().Scope.Read("Cursor").Equals(RegressionTitle().Scope.Read("LANGUAGE")),"settings opens on language row");
            await RegressionTap(RegressionKey(2));
            RegressionExpect(RegressionTitle().Scope.Read("Cursor").Equals(RegressionTitle().Scope.Read("SAVEANDBACK")),"settings wraps upward from language");
            await RegressionTap(RegressionKey(3));
            RegressionExpect(RegressionTitle().Scope.Read("Cursor").Equals(RegressionTitle().Scope.Read("LANGUAGE")),"settings wraps downward to language");
            await RegressionSelect("KEYCONFIG");RegressionView("VIEW_KEYCONFIG");
            await RegressionTap(RegressionKey(2));await RegressionTap(RegressionKey(10));RegressionView("VIEW_OPTION");
            await RegressionTap(RegressionKey(2));
            RegressionExpect(RegressionTitle().Scope.Read("Cursor").Equals(RegressionTitle().Scope.Read("LANGUAGE")),"language row reached in settings");
            await RegressionTap(RegressionKey(1));
            await AuditNativeMenuDraw(("语言",14),("English",14),("KeyConfig",46));
            RegressionExpect(!NativeLocalization.IsChinese&&NativeLocalization.Text("地图编辑器")=="Map Editor","settings switches interface to English");
            RegressionExpect(NativeContentStore.Load<string>(Path.Combine(world.UserRoot,"Cfg","Language.json"))=="en","language choice persisted");
            await RegressionTap(RegressionKey(10));
            RegressionExpect(NativeLocalization.IsChinese,"settings confirmation switches language back");
            await RegressionTap(RegressionKey(2));
            await AuditNativeMenuDraw(("ToggleBlood",14),("ScreenShake",46),("StartWeather",78),("ArrowCamera",110),("Save and Back",142));
            await RegressionSelect("SAVEANDBACK");RegressionView("VIEW_TITLE");
            await RegressionSelect("MOD");RegressionView("VIEW_MOD");
            await AuditNativeMenuDraw(("地图编辑器",160),("角色编辑器",192),("Mod列表",224),("Back",256));
            NativeLocalization.SetLanguage("en",false);
            await AuditNativeMenuDraw(("地图编辑器",160),("角色编辑器",192),("Mod列表",224),("Back",256));
            NativeLocalization.SetLanguage("zh_CN",false);
            await RegressionTap(RegressionKey(11));RegressionView("VIEW_TITLE");
            RegressionExpect(RegressionTitle().Scope.Read("Cursor").Equals(RegressionTitle().Scope.Read("MOD")),"Mod cancel restores main menu cursor");
            await RegressionSelect("MOD");RegressionView("VIEW_MOD");
            await RegressionSelect("MOD_CSHARP");
            RegressionExpect(modPanel is not null&&modalDepth==1,"mod menu suspends gameplay");
            if(nativeMenuWindowAudit)await AuditNativeMenuWindow(modPanel!,"Mod");
            var modTabs=Flatten(modPanel!).OfType<TabContainer>().Single(t=>t.Name=="ModTabs");
            RegressionExpect(modTabs.GetTabCount()==2&&modTabs.GetTabTitle(0)=="Mod列表"&&modTabs.GetTabTitle(1)=="Mod编辑","fixed Mod list and editor tabs");
            Flatten(modPanel!).OfType<Button>().Single(b=>b.Name=="InstallExamples").EmitSignal(BaseButton.SignalName.Pressed);
            var installedMods=Flatten(modPanel!).OfType<ItemList>().Single(l=>l.Name=="InstalledMods");
            int movement=Enumerable.Range(0,installedMods.ItemCount).Single(i=>installedMods.GetItemTooltip(i).Contains("ID: example.movement",StringComparison.Ordinal));
            installedMods.Select(movement);installedMods.EmitSignal(ItemList.SignalName.ItemSelected,movement);
            Flatten(modPanel!).OfType<Button>().Single(b=>b.Name=="EnableMod").EmitSignal(BaseButton.SignalName.Pressed);
            RegressionExpect(content.Actions.Count==3,"mod panel compiles all three movement actions");
            Flatten(modPanel!).OfType<Button>().Single(b=>b.Name=="CloneMod").EmitSignal(BaseButton.SignalName.Pressed);
            var projectDirectory=Flatten(modPanel!).OfType<LineEdit>().Single(l=>l.Name=="ModProjectDirectory");
            RegressionExpect(modTabs.CurrentTab==1&&!projectDirectory.Editable&&projectDirectory.Text.StartsWith(contentStore.Projects+Path.DirectorySeparatorChar,StringComparison.Ordinal),"editing an installed mod opens its project copy");
            Flatten(modPanel!).OfType<Button>().Single(b=>b.Name=="ReloadModProject").EmitSignal(BaseButton.SignalName.Pressed);
            RegressionExpect(content.Actions.Count==3&&mods.LoadedMods.Single(m=>m.Id=="example.movement").Directory==projectDirectory.Text,"project copy compiles through Mod editor");
            modPanel!.EmitSignal(Window.SignalName.CloseRequested);await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            RegressionExpect(modalDepth==0,"mod panel releases modal input");
            if(nativeMenuWindowAudit)await AuditNativeMenuDraw(("地图编辑器",160),("角色编辑器",192),("Mod列表",224),("Back",256));

            await RegressionSelect("MOD_MAP");
            RegressionExpect(mapEditor is not null&&modalDepth==1&&mapEditor.Document.Instances.Count==1227&&mapEditor.Document.Tiles.Count==1647,"map editor opened from Mod menu");
            if(nativeMenuWindowAudit)await AuditNativeMenuWindow(mapEditor!,"map");
            mapEditor!.EmitSignal(Window.SignalName.CloseRequested);
            await AuditWait(()=>mapEditor is null&&modalDepth==0?this:null,"map editor menu return");
            if(nativeMenuWindowAudit)await AuditNativeMenuDraw(("地图编辑器",160),("角色编辑器",192),("Mod列表",224),("Back",256));
            await RegressionSelect("MOD_CHARACTER");
            RegressionExpect(characterEditor is not null&&modalDepth==1,"character editor opened from Mod menu");
            if(nativeMenuWindowAudit)
            {
                await AuditNativeMenuWindow(characterEditor!,"character");
                var closing=characterEditor!;closing.EmitSignal(Window.SignalName.CloseRequested);
                var discard=closing.GetChildren().OfType<ConfirmationDialog>().FirstOrDefault(d=>d.Visible);
                discard?.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
                await AuditWait(()=>characterEditor is null&&modalDepth==0?this:null,"character editor returns to Mod");
                RegressionView("VIEW_MOD");
                await AuditNativeMenuDraw(("地图编辑器",160),("角色编辑器",192),("Mod列表",224),("Back",256));
                NativeLocalization.SetLanguage("en");
                GD.Print("NATIVE_MENU_WINDOW_OK editors=3 returns=3 language_saved=en");
            }
            GD.Print("NATIVE_MENU_OK settings=visible language=zh_CN,en persistence=PASS mod=map,character,list,editor logo=0.75 menu_y=312");
            if(nativeMenuWindowAudit||OS.GetCmdlineUserArgs().Contains("--native-menu-only"))
            {
                audio.Shutdown();GetTree().Quit();return;
            }
            string output=Path.Combine(contentStore.Root,"integration-pixels");
            string report=characterEditor!.RunUiAudit(output);
            RegressionExpect(content.Characters.Values.Any(c=>c.Parts.Count>=2),"saved editor character immediately registered");
            var rediscovered=new ContentRegistry();new NativeContentStore(world.UserRoot).DiscoverCharacters(rediscovered);
            RegressionExpect(rediscovered.Characters.Values.Any(c=>c.Parts.Count==3&&c.Name=="UI 像素审核"),"saved character discovered by fresh content service");
            int liveRoom=world.CurrentRoom,liveNext=world.NextId;long liveFrame=world.Frame,liveTick=nativeClock.Tick;
            characterEditor.ApplyRuntimePreview();
            await AuditWait(()=>previewReady&&!previewBusy?isolatedPreview?.Player:null,"actual character preview initialization");
            var preview=isolatedPreview!;var previewWorld=preview.World;var previewActor=preview.Player!;
            RegressionExpect(preview.Bridge.BoundCharacter(previewActor.Id) is not null,"preview bound to real GM actor");
            var previewController=NativeActorActions.Controller(new(previewWorld,previewActor))!;
            for(int wait=0;wait<240&&(!NativeActorActions.Grounded(new(previewWorld,previewActor))||previewActor.Scope.ReadOr("ChangeMotion").Truth||!NativeActorActions.MotionMatches(new(previewWorld,previewActor),"STAND_0"));wait++)
            {
                AdvanceCharacterPreview(new(false,false,false,false));
                await AuditWait(()=>!previewBusy?previewActor:null,"preview settles before jump buttons");
            }
            RegressionExpect(NativeActorActions.Grounded(new(previewWorld,previewActor))&&previewController.Scope.ReadOr("JumpNum").Int==2,"preview jump buttons begin grounded with original jump count");
            preview.Input.Clear(previewWorld);
            var previewJumps=new List<double>();
            void RecordPreviewJump(GmInstance instance){if(instance==previewActor)previewJumps.Add(instance.Scope.ReadOr("vspeed").Number);}
            previewWorld.CharacterJumped+=RecordPreviewJump;
            var jumpButton=Flatten(characterEditor).OfType<Button>().Single(button=>button.Text=="跳跃");
            int previewKeyOffset=previewWorld.Globals.ReadOr("controllMode").Equals(previewWorld.Constants["CTRL_KEY"])?12:0;
            int previewJumpKey=previewWorld.Globals.Read("key",[previewKeyOffset+5]).Int;
            for(int click=0;click<2;click++)
            {
                preview.Clock.RoomChanged();
                if(click==1)preview.Clock.LegacyDue(previewWorld.Engine.Read("room_speed").Number);
                jumpButton.EmitSignal(BaseButton.SignalName.Pressed);
                await AuditWait(()=>!previewBusy?previewActor:null,"paused preview jump click");
                RegressionExpect(previewJumps.Count==click+1&&previewJumps[^1]<0,"paused preview jump click applies one original impulse at clock phase "+click);
                var released=preview.Input.TakeNativeInput();
                RegressionExpect(!released.Held.Contains(previewJumpKey)&&previewController.Scope.ReadOr("b2").Int==3,"paused preview jump click releases mapped key "+click);
            }
            previewBusy=true;
            jumpButton.EmitSignal(BaseButton.SignalName.Pressed);
            previewBusy=false;
            var busyInput=preview.Input.TakeNativeInput();
            RegressionExpect(!busyInput.Held.Contains(previewJumpKey)&&!busyInput.Pressed.Contains(previewJumpKey),"busy preview discards jump without holding key");
            jumpButton.EmitSignal(BaseButton.SignalName.Pressed);
            await AuditWait(()=>!previewBusy?previewActor:null,"paused preview jump resumes after busy");
            var resumedInput=preview.Input.TakeNativeInput();
            RegressionExpect(!resumedInput.Held.Contains(previewJumpKey)&&previewController.Scope.ReadOr("b2").Int==3,"paused preview jump releases after busy");
            RegressionExpect(previewJumps.Count==2,"paused preview busy and exhausted clicks do not duplicate jump actions");
            previewWorld.CharacterJumped-=RecordPreviewJump;
            for(int t=0;t<10;t++)
            {
                AdvanceCharacterPreview(new(false,true,t==2,false));
                await AuditWait(()=>!previewBusy?previewActor:null,"preview frame");
            }
            RegressionExpect(previewActor.Scope.ReadOr("x").Number!=96,"preview controls execute actual movement");
            characterEditor.EmitSignal(Window.SignalName.CloseRequested);
            await AuditWait(()=>characterEditor is null&&modalDepth==0?this:null,"character editor return");
            RegressionExpect(isolatedPreview is null&&!Directory.Exists(preview.TemporaryDirectory),"character preview disposes isolated runtime");
            RegressionExpect(world.CurrentRoom==liveRoom&&world.NextId==liveNext&&world.Frame==liveFrame&&nativeClock.Tick==liveTick,"character preview preserves the live room, instances, frame and clock");
            GD.Print("NATIVE_PREVIEW_INPUT_AUDIT_OK paused_clicks=3 impulses=",string.Join(',',previewJumps)," clock_phases=2 busy=PASS release=PASS lifecycle=PASS");
            if(OS.GetCmdlineUserArgs().Contains("--native-preview-input-audit"))
            {
                audio.Shutdown();GetTree().Quit();return;
            }

            await PrepareTestingRoom();for(int f=0;f<12;f++)await StepFrameAsync();
            nativeAuditUi=true;controls.Clear(world);
            _Input(new InputEventKey{Keycode=Key.V,Pressed=true});
            var vTask=StepFrameAsync();
            var menu=await AuditWait(()=>GetNodeOrNull<PopupMenu>("CharacterMenu"),"real V custom character menu");
            int custom=Enumerable.Range(0,menu.ItemCount).First(i=>menu.GetItemText(i)=="编辑器：UI 像素审核");
            menu.EmitSignal(PopupMenu.SignalName.IdPressed,menu.GetItemId(custom));
            await AuditFinish(vTask,"V full character binding");
            RegressionExpect(nativeBridge.BoundCharacter(Player()!.Id)?.Parts.Count==3,"real V menu applies saved layered character binding");
            controls.Clear(world);nativeAuditUi=false;
            await world.LoadRoomAsync(world.Constants["Title"].Int);SyncNativeUi();
            await RegressionFrames(12);await RegressionSelect("MOD");await RegressionSelect("MOD_MAP");
            RegressionExpect(mapEditor is not null&&mapEditor.Document.Instances.Count==1227&&mapEditor.Document.Tiles.Count==1647,"map editor imports complete benchmark");
            var doc=mapEditor!.Document;await PlayMap(doc);
            RegressionExpect(mapPlaytest&&modalDepth==0,"map playtest resumes gameplay");
            for(int f=0;f<12;f++)await StepFrameAsync();
            ReturnToMapEditor();
            await AuditWait(()=>mapEditor.Visible&&modalDepth==1?mapEditor:null,"return to map editor");
            mapEditor.EmitSignal(Window.SignalName.CloseRequested);
            await AuditWait(()=>mapEditor is null&&modalDepth==0?this:null,"close map editor");
            GD.Print("NATIVE_INTEGRATION_OK pixels=",report," tick=",nativeClock.Tick);
            audio.Shutdown();GetTree().Quit();
        }
        catch(Exception e){Fail(e);audio.Shutdown();GetTree().Quit(1);}
    }
    private async Task AuditNativeMenuDraw(params (string Text,float Y)[] expected)
    {
        graphics.TextAudit??=new();graphics.TextAudit.Clear();
        graphics.SpriteGeometryAudit??=new();graphics.SpriteGeometryAudit.Clear();
        graphics.Build();
        foreach(var label in expected)
            RegressionExpect(graphics.TextAudit.Any(t=>t.Text==NativeLocalization.Text(label.Text)&&Mathf.IsEqualApprox(t.Position.Y,label.Y)&&t.Position.X>=0&&t.Position.X<world.OutputSize().Width),"menu draw: "+label.Text+" at "+label.Y);
        if(nativeMenuWindowAudit)await AuditNativeMenuWindow(GetWindow(),"menu");
    }
    private void PaintNativeMenuWindow(GmCanvas target)
    {graphics.Render(target);nativeMenuWindowPaints++;}
    private async Task AuditNativeMenuWindow(Window window,string step)
    {
        RegressionExpect(DisplayServer.GetName()!="headless"&&window.Visible&&window.Size.X>0&&window.Size.Y>0,"visible window for "+step);
        var before=Engine.GetFramesDrawn();int paints=nativeMenuWindowPaints;
        canvas.QueueRedraw();
        for(int frame=0;frame<3;frame++)await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        var drawn=Engine.GetFramesDrawn()-before;
        RegressionExpect(drawn>0,"rendered frames for "+step);
        if(window==GetWindow())RegressionExpect(nativeMenuWindowPaints>paints,"game canvas painted for "+step);
        var handle=DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle,window.GetWindowId());
        RegressionExpect(handle!=0,"native window handle for "+step);
        GD.Print("WINDOW_RENDERED step=",step," display=",DisplayServer.GetName()," frames=",drawn," paints=",nativeMenuWindowPaints-paints," handle=",handle," size=",window.Size);
    }
    private static IEnumerable<Node> Flatten(Node node)
    {yield return node;foreach(Node child in node.GetChildren())foreach(Node item in Flatten(child))yield return item;}
}

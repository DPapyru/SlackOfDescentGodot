using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private readonly GmFixedClock nativeClock = new();
    private readonly ContentRegistry content = new();
    private NativeContentStore contentStore = null!;
    private NativeModManager mods = null!;
    private GmNativeBridge nativeBridge = null!;
    private NativeMapEditor? mapEditor;
    private NativeCharacterEditor? characterEditor;
    private NativeModPanel? modPanel;
    private Button? returnFromTest;
    private bool mapPlaytest;
    private bool previewBusy;
    private bool previewReady;
    private NativeCharacterPreview? isolatedPreview;
    private CharacterDefinition? previewDefinition;
    private bool nativeAuditUi;
    private SubViewport? previewViewport;
    private GmCanvas? previewCanvas;
    private readonly Dictionary<int, CharacterDefinition> characterVisuals = new();
    private NativeCharacterRepository nativeDrawRepository=null!;
    private string ExamplesDirectory => OS.HasFeature("editor") ? ProjectSettings.GlobalizePath("res://Examples/Mods")
        : Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!,"Examples","Mods");
    private void InitializeNative()
    {
        contentStore = new(world.UserRoot);
        nativeDrawRepository=new(contentStore);
        mods = new(content);
        nativeBridge = new(world, content);
        world.SuppressCode = nativeBridge.TrySuppressCode;
        contentStore.DiscoverCharacters(content);
        string enabled = Path.Combine(contentStore.Root,"enabled-mods.json");
        if(File.Exists(enabled))
        {
            var directories=NativeContentStore.Load<string[]>(enabled);
            if(directories.Length>0)
            {
                var result=mods.ReloadMany(directories);
                if(!result.Success)GD.Print("MOD_LOAD_FAILED ",result.Diagnostics);
            }
        }
        mods.Changed += ()=>NativeContentStore.Save(enabled,mods.LoadedMods.Select(m=>m.Directory).ToArray());
        mods.Changed += OnMapModsChanged;
        _ = new NativeCharacterRendering(world,graphics,nativeDrawRepository,content,characterVisuals,()=>nativeClock.GameTime,nativeBridge);
        graphics.AdditionalDrawCommands += NativeSdkDraws;
        GetWindow().FocusExited += ResetNativeInput;
    }
    private void ResetNativeInput()
    { nativeClock.Pause(); controls.Clear(world); elapsed=0; frameMonitor.Reset(); }
    private void SyncNativeUi()
    {
        if(returnFromTest is not null)returnFromTest.Visible=mapPlaytest;
    }
    private void OpenMods()
    {
        if(modPanel is not null)return;
        ResetNativeInput();modalDepth++;
        modPanel=new(mods,contentStore,ExamplesDirectory,content){Visible=false,ForceNative=true};AddChild(modPanel);
        modPanel.VisibilityChanged+=()=>
        {
            if(modPanel is null||modPanel.Visible)return;
            var closed=modPanel;modPanel=null;modalDepth--;ResetNativeInput();closed.QueueFree();
        };
        ShowEditorWindow(modPanel);
    }
    private void OpenMapEditor()
    {
        if(mapEditor is not null)return;
        ResetNativeInput();modalDepth++;
        mapEditor=new(world,content,contentStore){Visible=false,ForceNative=true};AddChild(mapEditor);
        mapEditor.Closed+=()=>
        {
            var closed=mapEditor;mapEditor=null;modalDepth--;ResetNativeInput();closed?.QueueFree();
        };
        mapEditor.PlayRequested+=PlayMap;
        ShowEditorWindow(mapEditor);
    }
    private async Task PrepareTestingRoom()
    {
        var context=new GmContext(world.Instances.FirstOrDefault(i=>!i.Destroyed)??new GmInstance());
        world.Evaluator.ResetBudget();
        world.Globals.Write("StartArea",0);
        await world.Evaluator.CallAsync("DataItemReset",[],context);
        await world.Evaluator.CallAsync("DataItemLoad",[],context);
        await world.LoadRoomAsync(world.Constants["TestRoom_Loading"].Int);
        var started=System.Diagnostics.Stopwatch.StartNew();
        while(world.IsLoadingRoom||world.PendingRoom is not null)
        {
            await world.StepAsync();
            if(started.Elapsed.TotalSeconds>30)throw new InvalidOperationException("测试房间加载超时");
        }
    }
    private async Task PlayMap(NativeMapDocument document)
    {
        if(mapEditor is null)return;
        try
        {
            ResetNativeInput();
            var available=EnabledMaps().ToList();
            string id="editor:"+document.Id;
            available.Add(new(id,"editor",document.Clone()));
            await StartNativeMap(available,id);
            graphics.Build();mapPlaytest=true;modalDepth--;
            if(returnFromTest is null)
            {
                returnFromTest=new Button{Text="返回地图编辑器",Position=new(8,40)};AddChild(returnFromTest);
                returnFromTest.Pressed+=ReturnToMapEditor;
            }
            ResetNativeInput();nativeClock.RoomChanged();
        }
        catch(Exception e){EndMapRuntime();await ShowDialog("试玩失败："+e.Message,["OK"]);mapEditor.ResumeAfterPlaytest();}
    }
    private async void ReturnToMapEditor()
    {
        if(!mapPlaytest||mapEditor is null)return;
        mapPlaytest=false;modalDepth++;ResetNativeInput();
        await world.LoadRoomAsync(world.Constants["Title"].Int);graphics.Build();
        EndMapRuntime();
        mapEditor.ResumeAfterPlaytest();
    }
    private void OpenCharacterEditor()
    {
        if(characterEditor is not null)return;
        ResetNativeInput();modalDepth++;previewReady=false;
        characterEditor=new(world,content,contentStore){Visible=false,ForceNative=true};AddChild(characterEditor);
        previewViewport=new SubViewport{Size=new(640,480),TransparentBg=false,RenderTargetUpdateMode=SubViewport.UpdateMode.Always,Disable3D=true};AddChild(previewViewport);
        previewCanvas=new GmCanvas();previewViewport.AddChild(previewCanvas);previewCanvas.Paint=c=>isolatedPreview?.Graphics.Render(c);
        characterEditor.PreviewSurface.Texture=previewViewport.GetTexture();
        characterEditor.PreviewRequested+=ApplyCharacterPreview;
        characterEditor.PreviewAdvanceRequested+=AdvanceCharacterPreview;
        characterEditor.PreviewResetRequested+=()=>{isolatedPreview?.Reset();previewCanvas?.QueueRedraw();};
        characterEditor.Closed+=CloseCharacterEditor;
        ShowEditorWindow(characterEditor);characterEditor.ApplyRuntimePreview();
    }
    private static void ShowEditorWindow(Window window)
    {
        if(DisplayServer.GetName()=="headless"){window.Position=Vector2I.Zero;window.Show();}
        else window.PopupCentered();
    }
    private async void CloseCharacterEditor()
    {
        if(characterEditor is null)return;
        var closed=characterEditor;characterEditor=null;
        while(previewBusy)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        isolatedPreview?.Dispose();isolatedPreview=null;previewDefinition=null;previewReady=false;
        previewViewport?.QueueFree();previewViewport=null;previewCanvas=null;
        graphics.Build();modalDepth--;ResetNativeInput();closed.QueueFree();
    }
    private GmInstance? Player()=>world.Instances.FirstOrDefault(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int));
    private async void ApplyCharacterPreview(CharacterDefinition definition)
    {
        if(previewBusy)return;
        previewBusy=true;
        try
        {
            if(isolatedPreview is null)
            {
                isolatedPreview=new(previewViewport!,world,content,nativeDrawRepository,mods.LoadedMods.Select(m=>m.Directory));
                await isolatedPreview.InitializeAsync();previewReady=true;
            }
            if(characterEditor is null)return;
            isolatedPreview.Registry.CharacterDirectories[definition.Id]=characterEditor.PreviewDirectory;
            previewDefinition=definition;await isolatedPreview.ApplyAsync(definition);previewCanvas?.QueueRedraw();
            characterEditor?.SetPreviewState(string.Format(NativeLocalization.Text("真实运行时：{0}；动作与碰撞已绑定。"),definition.BasePreset));
        }
        catch(Exception e){characterEditor?.SetPreviewState(NativeLocalization.Text("预览失败：")+NativeLocalization.Text(e.Message));}
        finally{previewBusy=false;}
    }
    private async void AdvanceCharacterPreview(NativeCharacterPreviewInput input)
    {
        if(previewBusy||!previewReady||characterEditor is null||isolatedPreview is null)return;
        previewBusy=true;
        try
        {
            await isolatedPreview.AdvanceAsync(input);previewCanvas?.QueueRedraw();
            var actor=isolatedPreview.Player;if(actor is not null)characterEditor?.SetPreviewState(string.Format(NativeLocalization.Text("真实运行时　动作 {0}　位置 {1:F1}, {2:F1}　竖直速度 {3:F1}"),actor.Scope.ReadOr("MotionNum"),actor.Scope.ReadOr("x").Number,actor.Scope.ReadOr("y").Number,actor.Scope.ReadOr("vspeed").Number));
        }
        catch(Exception e){characterEditor?.SetPreviewState(NativeLocalization.Text("预览失败：")+NativeLocalization.Text(e.Message));}
        finally{previewBusy=false;}
    }
    private async Task BindFullCharacter(CharacterDefinition definition)
    {
        var actor=Player()??throw new InvalidOperationException("没有可控制的角色");
        var missing=definition.Actions.Values.Where(id=>!content.Actions.ContainsKey(id))
            .Concat(definition.Dependencies.Where(id=>!mods.LoadedMods.Any(m=>m.Id==id)&&!content.Resources.ContainsKey(id)))
            .Concat(definition.Parts.SelectMany(p=>p.Frames).Where(f=>!File.Exists(nativeDrawRepository.FramePath(f,content.CharacterDirectories.GetValueOrDefault(definition.Id)))).Select(f=>f.Png)).Distinct().ToArray();
        if(missing.Length>0)
        {
            string message="角色缺少以下资源或扩展，配置会保留：\n"+string.Join('\n',missing);
            if(audit)GD.Print("CONTENT_MISSING ",message);else await ShowDialog(message,["OK"]);
        }
        await NativeCharacterRuntime.BindAsync(world,nativeBridge,definition);characterVisuals[actor.Id]=definition;
    }
    private async Task<int> NativeCharacterMenu(string text,int fallback,GmContext context)
    {
        bool full=text.StartsWith("<Input Custom Character Name>",StringComparison.Ordinal)&&!controls.Call("keyboard_check",[17],world)!.Value.Truth;
        var characters=full?content.Characters.Values.OrderBy(c=>c.Name).ToArray():[];
        string expanded=text+string.Concat(characters.Select(c=>"|编辑器："+c.Name));
        int selected=audit&&!nativeAuditUi&&!OS.GetCmdlineUserArgs().Contains("--controls-ui-audit")?Menu(expanded,fallback):await ShowPopupMenu(text,fallback,characters.Select(c=>NativeLocalization.Text("编辑器：")+NativeContentLocalization.Display(content,c.Id,c.Name)).ToArray());
        int original=text.Split('|').Length;
        if(selected>=original&&selected<original+characters.Length){await BindFullCharacter(characters[selected-original]);return fallback;}
        return selected;
    }
}

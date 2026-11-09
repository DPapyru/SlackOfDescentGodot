using Godot;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    public async Task AuditAsync(string output)
    {
        void Expect(bool ok,string message){if(!ok)throw new InvalidOperationException("Motion editor: "+message);}
        async Task Ready()
        {
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            while(busy){if(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds>90)throw new TimeoutException("Motion preview initialization");await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
            Expect(preview?.Player is not null,"independent preview is ready: "+status.Text);
        }
        async Task Shot(string name)
        {
            string? directory=System.Environment.GetEnvironmentVariable("SLACK_MOTION_SHOTS");
            if(string.IsNullOrEmpty(directory)||DisplayServer.GetName()=="headless")return;
            Directory.CreateDirectory(directory);PopupCentered();
            for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var image=GetTexture().GetImage();string file=Path.Combine(directory,name+".png");image.SavePng(file);GD.Print("MOTION_EDITOR_SHOT ",file);
            Hide();
        }
        async Task Wait(int milliseconds){long start=System.Diagnostics.Stopwatch.GetTimestamp();while(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds<milliseconds)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
        IEnumerable<string> MenuLabels(string label)
        {
            var button=Descendants(this).OfType<MenuButton>().FirstOrDefault(item=>item.Text==label);
            if(button is null)return [];
            var popup=button.GetPopup();popup.EmitSignal(PopupMenu.SignalName.AboutToPopup);
            var labels=new List<string>();for(int i=0;i<popup.ItemCount;i++)labels.Add(popup.GetItemText(i));return labels;
        }
        int Count<T>()=>Descendants(this).Count(node=>node is T);
        bool ButtonText(string text)=>Descendants(this).OfType<Button>().Any(button=>button.Text==text);
        Vector2 SurfacePoint(double x,double y){var (surfaceX,surfaceY)=MotionViewport.ToSurface(x,y,surface.Size.X,surface.Size.Y,PreviewView);return new((float)surfaceX,(float)surfaceY);}
        (double X,double Y) RenderedPoint(double x,double y)
        {
            var view=preview!.World.View(0);
            var (surfaceX,surfaceY)=MotionViewport.ToSurface(x,y,surface.Size.X,surface.Size.Y,new(view.X,view.Y,view.Width,view.Height,(int)view.PortWidth,(int)view.PortHeight));
            return(surfaceX,surfaceY);
        }
        void Layout()
        {
            Expect(tabs is TabBar&&tabs.TabCount==projects.Count,"the character bar lists one tab per project");
            Expect(tabs.TabCount>0&&tabs.CurrentTab==activeProject,"the active tab is the active project");
            Expect(surface is TextureRect&&overlay is not null,"the center stage keeps the single motion surface");
            Expect(Count<TextureRect>()==1&&overlay.GetParent()==surface&&Descendants(this).OfType<SubViewport>().Count(node=>node.GetParent()==this)==1&&Descendants(this).OfType<GmCanvas>().Count(node=>node.GetParent()==viewport)==1,"the studio owns one surface, overlay, viewport and canvas");
            Expect(leftPages is TabContainer&&leftPages.GetTabCount()==1&&leftPages.GetTabTitle(0)=="节点属性修改","the left dock opens on the single pose page");
            Expect(leftPages.GetParent() is VBoxContainer leftDock&&Math.Abs(leftDock.CustomMinimumSize.X-350)<.001,"the left sidebar is 350 pixels wide");
            Expect(leftPages.CurrentTab==0&&sourceView.GetParent()==leftPages.GetTabControl(0),"the left page owns the code view");
            Expect(Descendants(this).OfType<TabContainer>().All(page=>page==leftPages),"the deleted dock pages are gone");
            Expect(sourceView is CodeEdit{Editable:true},"the left page is editable code");
            var settings=Descendants(this).FirstOrDefault(node=>node.Name=="ActionSettings");
            Expect(settings is VBoxContainer{ CustomMinimumSize.X: 280 }&&Descendants(settings).OfType<TabContainer>().Count()==0,"the right dock is a single 280 pixel 动作设置 page");
            Expect(settings is not null&&Descendants(settings).Contains(clipChoice)&&Descendants(settings).Contains(duration)&&Descendants(settings).Contains(loop)&&loop.Text=="循环","动作设置 holds the clip dropdown, 总帧数 and 循环");
            var transport=Descendants(this).FirstOrDefault(node=>node.Name=="TimelineDock");
            Expect(transport is not null&&Descendants(transport).Contains(timelineSlider)&&timelineSlider is HSlider&&frame is SpinBox,"the bottom dock keeps the timeline");
            Expect(transport is not null&&Descendants(transport).OfType<CheckButton>().Any(button=>button.Text is "▶" or "⏸"),"the transport dock owns the play toggle");
            Expect(transport is not null&&Descendants(transport).OfType<Button>().Any(button=>button.Text=="单帧"),"the transport dock owns single step");
            Expect(Descendants(this).OfType<VSplitContainer>().Any(split=>Descendants(split).Contains(transport)),"the bottom dock is resizable through a split container");
            Expect(transport is not null&&Descendants(transport).OfType<OptionButton>().Any(button=>button.ItemCount==3&&Enumerable.Range(0,3).Select(index=>button.GetItemText(index)).SequenceEqual(new[]{"0.5x","1x","2x"})),"the transport dock owns the speed chooser");
            var statusBar=Descendants(this).FirstOrDefault(node=>node.Name=="StatusBar");
            Expect(statusBar is PanelContainer panel&&panel.GetThemeStylebox("panel") is StyleBoxFlat bar&&bar.BgColor==new Color("#007acc")&&status.GetThemeColor("font_color")==Colors.White,"the blue status bar carries white text");
            var fileMenu=MenuLabels("文件").ToList();
            Expect(fileMenu.Contains("导入原版动作…"),"the file menu imports an original motion");
            var editMenu=MenuLabels("编辑").ToList();
            Expect(editMenu.Contains("删除当前帧关键帧")&&editMenu.Contains("IK 绑定所选末端")&&editMenu.Contains("解除所选 IK")&&editMenu.Contains("姿态根偏移"),"the edit menu owns the key and IK entries");
            Expect(MenuLabels("视图").Contains("重载预览")&&MenuLabels("帮助").Contains("说明手册…"),"the view and help menus stay");
            Expect(!fileMenu.Concat(editMenu).Concat(MenuLabels("视图")).Concat(MenuLabels("帮助")).Any(label=>label.Contains("状态")),"no menu label mentions 状态");
            Expect(!Descendants(this).OfType<TabContainer>().SelectMany(page=>Enumerable.Range(0,page.GetTabCount()).Select(page.GetTabTitle)).Any(title=>title.Contains("状态")),"no tab title mentions 状态");
            Expect(!Descendants(this).OfType<ItemList>().Any(),"the deleted list panels are gone");
            Expect(!ButtonText("取景")&&!ButtonText("读取运行帧")&&!ButtonText("预览当前帧"),"the removed toolbar entries are gone");
            Expect(status is Label&&playState is Label&&targetLabel.Text.Length>0,"the status bar reports playback and the target");
            string[] legacy=["动作列表","动作参数","动作预览","动作参数工作区","动作图工作区","工作区"];
            Expect(!Descendants(this).Any(node=>legacy.Contains(node.Name.ToString())),"the old action workspace nodes are gone");
            var legacyMethods=new[]{"ImportAction","ExportAction","CheckCompile","SelectAction","AddMachineState","RefreshStateInspector","AddTransition","AddCondition","RemoveCondition","AddEvent","EditEventCell","RemoveEvent","RenameSelectedState","RemoveSelectedState","SyncIkSelection"};
            Expect(legacyMethods.All(name=>GetType().GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic) is null),"the removed page entry points are gone");
        }
        Layout();
        AuditLeftPages();
        GD.Print("MOTION_EDITOR_LAYOUT_OK");
        await Ready();
        Expect(preview!.Bridge.BoundCharacter(preview.Player!.Id) is not null,"the preview keeps the character binding");
        Expect(Math.Abs(frame.Value)<.001,"the preview opens on frame 0");
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.Q,Pressed=true,Echo=false});
        Expect(!GetViewport().IsInputHandled(),"an unbound key bubbles past the editor");
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.Escape,Pressed=true,Echo=false});
        Expect(!GetViewport().IsInputHandled()&&!dragging,"Escape outside a drag is not swallowed");
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.F,Pressed=true,Echo=false});
        Expect(GetViewport().IsInputHandled(),"the F shortcut marks the key handled");
        preview.Graphics.SpriteGeometryAudit=new();preview.Graphics.Build();
        var loadedDraws=preview.Graphics.SpriteGeometryAudit;double loadedWorst=0;int loadedLimbs=0;
        foreach(var node in PreviewActor!.Services.Motions.Skeleton(PreviewActor))
        {
            Expect(Math.Abs(node.X)>.5||Math.Abs(node.Y)>.5,"the loaded preview applies the document pose instead of leaving "+node.Name+" at the world origin");
            var limb=loadedDraws.Where(draw=>(draw.Position-new Vector2((float)node.X,(float)node.Y)).Length()<=.01f).ToArray();
            Expect(limb.Length>0,"the loaded preview draws the limb for "+node.Name);
            var (overlayX,overlayY)=MotionViewport.ToSurface(node.X,node.Y,surface.Size.X,surface.Size.Y,overlay!.View);
            foreach(var draw in limb)
            {
                var (renderedX,renderedY)=RenderedPoint(draw.Position.X,draw.Position.Y);
                loadedWorst=Math.Max(loadedWorst,Math.Sqrt((overlayX-renderedX)*(overlayX-renderedX)+(overlayY-renderedY)*(overlayY-renderedY)));loadedLimbs++;
                Expect(Math.Abs(overlayX-renderedX)<=.5&&Math.Abs(overlayY-renderedY)<=.5,"the loaded skeleton joint "+node.Name+" lands on its drawn limb: overlay="+overlayX.ToString("F2")+","+overlayY.ToString("F2")+" rendered="+renderedX.ToString("F2")+","+renderedY.ToString("F2"));
            }
        }
        Expect(loadedLimbs>0&&loadedWorst<=.5,"the loaded skeleton lands on the drawn limbs: draws="+loadedLimbs+" worst="+loadedWorst.ToString("F3"));
        PopupCentered();
        for(int i=0;i<3;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        Expect(sourceView is CodeEdit{Editable:true}&&rawView is CodeEdit{Editable:true}&&sourceView.SyntaxHighlighter is TomlSyntaxHighlighter&&rawView.SyntaxHighlighter is TomlSyntaxHighlighter,"the left pages highlight TOML while editing");
        Expect(sourceView.CodeCompletionEnabled&&rawView.CodeCompletionEnabled,"the left pages complete TOML");
        Expect(sourceView.Text.StartsWith("[[frame]]")&&sourceView.Text.Contains("index = "+frame.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),"节点属性修改 renders the current frame block");
        Expect(rawView.Text.Contains("[[clip]]")&&rawView.Text.Contains("[[state]]")&&rawView.Text.Contains("[state.movement]"),"原始数据 renders the whole graph as TOML tables");
        string sourceText=sourceView.Text;
        sourceView.Text="HEA";sourceView.SetCaretLine(0);sourceView.SetCaretColumn(3);
        FillCompletion(sourceView,true);Expect(sourceView.GetCodeCompletionOption(0).Count>0,"节点属性修改 offers TOML completion");
        sourceView.Text=sourceText;
        string rawText=rawView.Text;
        rawView.Text="HEA";rawView.SetCaretLine(0);rawView.SetCaretColumn(3);
        FillCompletion(rawView,false);Expect(rawView.GetCodeCompletionOption(0).Count>0,"原始数据 offers TOML completion");
        rawView.Text=rawText.Replace("duration = 60","duration = 45");
        sourceView.Text="[[frame]]\nindex = 0\nHEAD.x = 2.5\n";
        CommitFrame();
        Expect(Math.Abs(Clip.Sample(0).GetValueOrDefault("HEAD").X-2.5)<1e-9&&sourceView.Text.Contains("HEAD.x = 2.5"),"committing the frame text writes the pose and re-renders the block");
        sourceView.Text="[[frame]]\nindex = 0\nHEAD.y = 1.5\n";
        sourceView.EmitSignal(Control.SignalName.FocusExited);
        Expect(Math.Abs(Clip.Sample(0).GetValueOrDefault("HEAD").Y-1.5)<1e-9,"losing focus commits the frame text");
        int frameUndo=document.UndoDepth;
        sourceView.Text="[[frame]]\nindex = 0\nHEAD.nope = 1\n";
        CommitFrame();
        Expect(sourceView.Text.Contains("HEAD.nope")&&document.UndoDepth==frameUndo,"invalid frame text is rejected without an edit");
        Expect(rawView.Text.Contains("duration = 45"),"an unfocused graph edit survives refreshes from the frame page");
        CommitGraph();
        Expect(Math.Abs(Clip.Duration-45)<1e-9,"committing the graph text applies the whole document");
        rawView.Text=rawView.Text.Replace("loop = true","loop = false");
        rawView.EmitSignal(Control.SignalName.GuiInput,new InputEventKey{Keycode=Key.Enter,CtrlPressed=true,Pressed=true});
        Expect(!Clip.Loop,"Ctrl+Enter commits the graph text");
        frame.Value=100000;
        sourceView.Text="[[frame]]\nindex = 100000\nHEAD.x = 4\n";
        int rangeUndo=document.UndoDepth;
        CommitFrame();
        Expect(document.UndoDepth==rangeUndo&&status.Text.Contains("帧号超出片段长度"),"an out of range frame text is rejected without an edit");
        frame.Value=0;
        int graphUndo=document.UndoDepth;
        rawView.Text="id = \"broken\"\nskeleton = \"Nana\"\nentry = \"idle\"\n[[clip]]\nid = \"idle\"\nduration = 10\n[[state]]\nid = \"idle\"\nclip = \"idle\"\nunknown = 1\n";
        CommitGraph();
        Expect(document.Graph.Id!="broken"&&document.UndoDepth==graphUndo&&rawView.Text.Contains("unknown = 1"),"invalid graph text is rejected without an edit");
        await Shot("motion-left-pages");
        var studio=preview!;var studioWorld=studio.World;
        var skeleton=studio.Bridge.Services.Motions.Skeleton(new(studio.World,studio.Player!));
        Expect(skeleton.Count==15,"preview exposes the 15 part skeleton");
        document=new(new MotionGraph{Id="audit.ik",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_TOP",Keys=[new(){Frame=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=3}]},new(){Bone="HAND1_BELOW",Keys=[new(){Frame=0,X=3}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND1_BELOW"}]});
        Refresh();
        var ikDragged=document.Drag("idle","HAND1_BELOW",0,4,2);
        Expect(ikDragged.Success,"IK drag applies from the editor document");
        Expect(Clip.Tracks.Single(t=>t.Bone=="BODY_TOP").Keys.Single().Angle!=0,"IK drag writes the chain root angle");
        Refresh();Expect(sourceView.Text.Contains("BODY_TOP.angle"),"a canvas drag re-renders the frame text");
        document.Undo();Refresh();Expect(Clip.Tracks.Single(t=>t.Bone=="BODY_TOP").Keys.Single().Angle==0&&!sourceView.Text.Contains("BODY_TOP.angle"),"IK drag undo restores the chain root and the text");
        await Shot("motion-studio");
        await Shot("motion-transport");
        Project.ClipIndex=0;
        document=new(new MotionGraph{Id="audit.live",Clips=[new(){Id="idle",Duration=20}],States=[new(){Id="idle",Clip="idle"}]});
        Refresh();frame.Value=3;
        int liveUndo=document.UndoDepth;
        duration.Value=21;
        Expect(Clip.Duration==21,"the clip duration is written immediately");
        Expect(document.UndoDepth==liveUndo+1,"the first immediate write adds one undo step");
        duration.Value=22;duration.Value=23;
        Expect(document.UndoDepth==liveUndo+1,"changing one control three times keeps a single undo step");
        Expect(Clip.Duration==23,"the control keeps writing the latest value");
        await Wait(600);
        duration.Value=24;
        Expect(document.UndoDepth==liveUndo+2,"edits more than 500ms apart start a new undo step");
        path=Path.Combine(output,"editor.coalesce.motion.json");Save();
        duration.Value=25;
        Expect(document.UndoDepth==liveUndo+3,"saving resets the coalescing window");
        RunAction("undo");Expect(Clip.Duration==24,"undo covers immediate writes");
        RunAction("redo");Expect(Clip.Duration==25,"redo covers immediate writes");
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.Z,CtrlPressed=true,Pressed=true});
        Expect(Clip.Duration==24,"ctrl+z undoes the last edit");
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.Z,CtrlPressed=true,ShiftPressed=true,Pressed=true});
        Expect(Clip.Duration==25,"ctrl+shift+z redoes the last edit");
        duration.Value=26;int echoUndo=document.UndoDepth;double echoDuration=Clip.Duration;
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.Z,CtrlPressed=true,Pressed=true,Echo=true});
        Expect(document.UndoDepth==echoUndo&&Clip.Duration==echoDuration,"a held ctrl+z echo neither undoes nor writes: depth="+document.UndoDepth+" duration="+Clip.Duration);
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.Z,CtrlPressed=true,Pressed=true});
        Expect(document.UndoDepth==echoUndo-1&&Clip.Duration==25,"ctrl+z without echo undoes the last duration edit: depth="+document.UndoDepth+" duration="+Clip.Duration);
        loop.ButtonPressed=false;Expect(!Clip.Loop,"the clip loop flag is written immediately");
        Expect(!ButtonText("写入关键帧")&&!ButtonText("更新片段")&&!ButtonText("写入参数")&&!ButtonText("写入事件"),"the submit buttons are gone");
        var fileMenu=MenuLabels("文件").ToList();
        Expect(fileMenu.Count(label=>label=="保存到…")==1,"the file menu has a single 保存到… entry");
        Expect(!fileMenu.Contains("保存到 Mod 项目")&&!fileMenu.Contains("将动作图保存到角色"),"the two old save entries are gone");
        int applyCount=Descendants(this).OfType<Button>().Count(button=>button.Text=="应用到游戏角色");
        foreach(var menu in Descendants(this).OfType<MenuButton>()){var popup=menu.GetPopup();popup.EmitSignal(PopupMenu.SignalName.AboutToPopup);for(int i=0;i<popup.ItemCount;i++)if(popup.GetItemText(i)=="应用到游戏角色")applyCount++;}
        Expect(applyCount==1,"applying to the game is offered exactly once");
        Project.ClipIndex=0;
        document=new(new MotionGraph{Id="audit.ring",Clips=[new(){Id="idle",Duration=20,Tracks=[new(){Bone="BODY_BOTTOM",Keys=[new(){Frame=0,X=0,Y=0,Angle=0}]},new(){Bone="BODY_TOP",Keys=[new(){Frame=0,X=0,Y=0,Angle=0}]},new(){Bone="HEAD",Keys=[new(){Frame=0,X=4,Y=-16,Angle=0}]},new(){Bone="HAND1_ABOVE",Keys=[new(){Frame=0,X=10,Y=0,Angle=0}]},new(){Bone="HAND1_BELOW",Keys=[new(){Frame=0,X=10,Y=0,Angle=0}]}]}],States=[new(){Id="idle",Clip="idle"}],IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND1_BELOW"}]});
        Refresh();frame.Value=0;
        var ringActor=PreviewActor!;ringActor.Write("Direction",1);ringActor.Write("image_angle",0);
        ApplyDocumentPose();
        void ZeroAncestors()
        {
            var nodes=ringActor.Services.Motions.Skeleton(ringActor);
            var headNode=nodes.FirstOrDefault(node=>node.Name=="HEAD");var chain=new List<string>();
            for(var node=headNode;node is not null;){var parentNode=nodes.FirstOrDefault(item=>item.Instance==node.Parent);if(parentNode is null)break;chain.Add(parentNode.Name);node=parentNode;}
            if(chain.Count==0)return;
            document.Edit(g=>{foreach(string name in chain){var track=g.Clips[0].Tracks.FirstOrDefault(item=>item.Bone==name);if(track is null){track=new(){Bone=name};g.Clips[0].Tracks.Add(track);}track.Keys.RemoveAll(key=>key.Frame==0);track.Keys.Add(new(){Frame=0,X=0,Y=0,Angle=0});}});
            Refresh();ApplyDocumentPose();
        }
        ZeroAncestors();
        SelectBone("HEAD");
        var ringNodes=ringActor.Services.Motions.Skeleton(ringActor);
        var headNode=ringNodes.Single(node=>node.Name=="HEAD");
        Expect(overlay!.RingVisible,"selecting a bone shows the ring");
        Expect(Math.Abs(overlay.RingPixelRadius-72)<.001,"the ring keeps a fixed 72 pixel radius");
        Expect(Math.Abs((overlay.BallAngle??double.NaN)-headNode.Angle)<.001,"the ball angle equals the selected bone world angle");
        var bars=overlay.BarPoints;
        Expect(bars.Count==2&&bars[0]!=Vector2.Zero&&bars[1]!=Vector2.Zero&&overlay.RingHit(bars[0])=="bar:x"&&overlay.RingHit(bars[1])=="bar:y","the ring draws an X bar and a Y bar that are hittable");
        var xBarRect=overlay.BarRect(0);var yBarRect=overlay.BarRect(1);
        Expect(xBarRect is not null&&yBarRect is not null&&xBarRect.Value.GetCenter().DistanceTo(bars[0])<.001f&&yBarRect.Value.GetCenter().DistanceTo(bars[1])<.001f,"the drawn bar rects are centered on the bar points: x="+xBarRect?.GetCenter()+" y="+yBarRect?.GetCenter());
        Expect(xBarRect is not null&&yBarRect is not null&&Math.Abs(xBarRect.Value.Size.X-144)<.001f&&Math.Abs(xBarRect.Value.Size.Y-8)<.001f&&Math.Abs(yBarRect.Value.Size.X-8)<.001f&&Math.Abs(yBarRect.Value.Size.Y-144)<.001f,"the X bar is 144x8 and the Y bar is 8x144");
        Expect(xBarRect is not null&&yBarRect is not null&&overlay.RingHit(xBarRect.Value.GetCenter())=="bar:x"&&overlay.RingHit(yBarRect.Value.GetCenter())=="bar:y","the bar rect centers hit their own bars");
        Expect(overlay.BarAxis(0).DistanceTo(new Vector2(1,0))<.001f&&overlay.BarAxis(1).DistanceTo(new Vector2(0,1))<.001f,"a level parent keeps the X bar horizontal and the Y bar vertical: "+overlay.BarAxis(0)+","+overlay.BarAxis(1));
        int ballUndo=document.UndoDepth;
        var ball=overlay.BallPoint;Expect(ball is not null,"the ring shows a ball");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=ball!.Value});
        Expect(dragging&&dragMode==DragMode.Ball,"pressing the ball starts an absolute angle drag");
        var parentNode=ringNodes.FirstOrDefault(node=>node.Instance==headNode.Parent);
        double parentX=parentNode?.X??ringActor.X,parentY=parentNode?.Y??ringActor.Y,desired=120,radius=120,desiredRadians=desired*Math.PI/180;
        var ballTarget=SurfacePoint(parentX+Math.Cos(desiredRadians)*radius,parentY-Math.Sin(desiredRadians)*radius);
        SurfaceInput(new InputEventMouseMotion{Position=ballTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=ballTarget});
        Expect(Math.Abs(Clip.Sample(0)["HEAD"].Angle-desired)<.1,"dragging the ball writes the absolute angle");
        Expect(document.UndoDepth==ballUndo+1,"the ball drag is one undo step");
        Expect(Math.Abs(ringActor.Services.Motions.Skeleton(ringActor).Single(node=>node.Name=="HEAD").Angle-desired)<.1,"the bone world angle follows the dragged absolute angle");
        SelectBone("HEAD");
        var barBefore=Clip.Sample(0)["HEAD"];int barUndo=document.UndoDepth;
        var xBar=overlay.BarPoints[0];
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=xBar});
        Expect(dragging&&dragMode==DragMode.BarX,"pressing the X bar starts a local X drag");
        Expect(Math.Abs(Clip.Sample(0)["HEAD"].X-barBefore.X)<.001&&Math.Abs(Clip.Sample(0)["HEAD"].Y-barBefore.Y)<.001,"pressing the X bar writes nothing before release");
        Expect(document.UndoDepth==barUndo,"pressing the X bar adds no undo step");
        SurfaceInput(new InputEventMouseMotion{Position=xBar+new Vector2(24,0)});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=xBar+new Vector2(24,0)});
        var barAfter=Clip.Sample(0)["HEAD"];
        Expect(barAfter.X>barBefore.X+.01,"dragging the X bar right increases local X");
        Expect(Math.Abs(barAfter.Y-barBefore.Y)<.01,"the X bar leaves local Y alone");
        Expect(document.UndoDepth==barUndo+1,"the X bar drag is one undo step");
        SelectBone("HEAD");
        var yBefore=Clip.Sample(0)["HEAD"];int yUndo=document.UndoDepth;
        var yBar=overlay.BarPoints[1];
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=yBar});
        Expect(dragging&&dragMode==DragMode.BarY,"pressing the Y bar starts a local Y drag");
        Expect(Math.Abs(Clip.Sample(0)["HEAD"].X-yBefore.X)<.001&&Math.Abs(Clip.Sample(0)["HEAD"].Y-yBefore.Y)<.001,"pressing the Y bar writes nothing before release");
        Expect(document.UndoDepth==yUndo,"pressing the Y bar adds no undo step");
        SurfaceInput(new InputEventMouseMotion{Position=yBar+new Vector2(0,-24)});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=yBar+new Vector2(0,-24)});
        var yAfter=Clip.Sample(0)["HEAD"];
        Expect(yAfter.Y<yBefore.Y-.01,"dragging the Y bar up moves the bone up");
        Expect(document.UndoDepth==yUndo+1,"the Y bar drag is one undo step");
        SelectBone("HEAD");
        var arrowZoom=SurfacePoint(ringActor.X,ringActor.Y);
        bool OldArrowsBlank()=>Enumerable.Range(0,8).All(index=>{var node=ringActor.Services.Motions.Skeleton(ringActor).Single(item=>item.Name=="HEAD");double radians=(90-index*45)*Math.PI/180;var point=SurfacePoint(node.X,node.Y)+new Vector2((float)(Math.Cos(radians)*76),(float)(-Math.Sin(radians)*76));return PickBoneAt(point) is null&&overlay.RingHit(point) is null;});
        for(int guard=0;guard<16&&!OldArrowsBlank();guard++)SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.WheelDown,Pressed=true,Position=arrowZoom});
        Expect(OldArrowsBlank(),"zooming out leaves the old arrow positions on empty canvas");
        foreach(bool shifted in new[]{false,true})
            for(int i=0;i<8;i++)
            {
                SelectBone("HEAD");
                var arrowNode=ringActor.Services.Motions.Skeleton(ringActor).Single(node=>node.Name=="HEAD");
                double arrowRadians=(90-i*45)*Math.PI/180;
                var arrowPoint=SurfacePoint(arrowNode.X,arrowNode.Y)+new Vector2((float)(Math.Cos(arrowRadians)*76),(float)(-Math.Sin(arrowRadians)*76));
                var arrowPose=Clip.Sample(0)["HEAD"];int arrowUndo=document.UndoDepth;
                SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,ShiftPressed=shifted,Position=arrowPoint});
                var pressedNode=ringActor.Services.Motions.Skeleton(ringActor).Single(node=>node.Name=="HEAD");
                Expect(Math.Abs(Clip.Sample(0)["HEAD"].X-arrowPose.X)<.001&&Math.Abs(Clip.Sample(0)["HEAD"].Y-arrowPose.Y)<.001,"pressing the old arrow positions writes no local X/Y");
                Expect(document.UndoDepth==arrowUndo,"pressing the old arrow positions adds no undo step");
                Expect(Math.Abs(pressedNode.X-arrowNode.X)<.001&&Math.Abs(pressedNode.Y-arrowNode.Y)<.001,"pressing the old arrow positions moves no bone");
                var arrowMoved=arrowPoint+new Vector2(24,0);
                SurfaceInput(new InputEventMouseMotion{Position=arrowMoved});
                SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,ShiftPressed=shifted,Position=arrowMoved});
                var releasedPose=Clip.Sample(0)["HEAD"];var releasedNode=ringActor.Services.Motions.Skeleton(ringActor).Single(node=>node.Name=="HEAD");
                Expect(Math.Abs(releasedPose.X-arrowPose.X)<.001&&Math.Abs(releasedPose.Y-arrowPose.Y)<.001,"dragging from an old arrow position writes no local X/Y");
                Expect(document.UndoDepth==arrowUndo,"dragging from an old arrow position adds no undo step");
                Expect(Math.Abs(releasedNode.X-arrowNode.X)<.001&&Math.Abs(releasedNode.Y-arrowNode.Y)<.001,"dragging from an old arrow position moves no bone");
            }
        SelectBone("HEAD");
        string parentName=parentNode?.Name??"BODY_TOP";
        document.Edit(g=>{var track=g.Clips[0].Tracks.FirstOrDefault(item=>item.Bone==parentName);if(track is null){track=new(){Bone=parentName};g.Clips[0].Tracks.Add(track);}track.Keys.RemoveAll(key=>key.Frame==0);track.Keys.Add(new(){Frame=0,Angle=30});});
        Refresh();ApplyDocumentPose();SelectBone("HEAD");
        var rotatedBefore=Clip.Sample(0)["HEAD"];int rotatedUndo=document.UndoDepth;
        var rotatedBar=overlay.BarPoints[0];
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=rotatedBar});
        SurfaceInput(new InputEventMouseMotion{Position=rotatedBar+new Vector2(24,0)});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=rotatedBar+new Vector2(24,0)});
        var rotatedAfter=Clip.Sample(0)["HEAD"];
        Expect(Math.Abs(rotatedAfter.X-rotatedBefore.X)>.01,"the X bar under a rotated parent changes local X");
        Expect(Math.Abs(rotatedAfter.Y-rotatedBefore.Y)<.01,"the X bar under a rotated parent leaves local Y alone");
        Expect(document.UndoDepth==rotatedUndo+1,"the rotated parent X drag is one undo step");
        document.Edit(g=>{var track=g.Clips[0].Tracks.FirstOrDefault(item=>item.Bone==parentName);if(track is null){track=new(){Bone=parentName};g.Clips[0].Tracks.Add(track);}track.Keys.RemoveAll(key=>key.Frame==0);track.Keys.Add(new(){Frame=0,Angle=0});});
        Refresh();ApplyDocumentPose();SelectBone("HEAD");
        ringActor.Write("Direction",-1);ApplyDocumentPose();SelectBone("HEAD");
        var mirrorBefore=Clip.Sample(0)["HEAD"];int mirrorUndo=document.UndoDepth;
        var mirrorBar=overlay.BarPoints[0];
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=mirrorBar});
        SurfaceInput(new InputEventMouseMotion{Position=mirrorBar+new Vector2(24,0)});
        Expect(overlay.BarKnob(0) is { } mirrorKnob&&mirrorKnob.X-mirrorBar.X>20,"the mirrored bar knob follows the pointer to the right: "+overlay.BarKnob(0));
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=mirrorBar+new Vector2(24,0)});
        var mirrorAfter=Clip.Sample(0)["HEAD"];
        Expect(Math.Abs(mirrorAfter.X-mirrorBefore.X)>.01,"the mirrored X bar still changes local X");
        Expect(Math.Abs(mirrorAfter.Y-mirrorBefore.Y)<.01,"the mirrored X bar leaves local Y alone");
        Expect(document.UndoDepth==mirrorUndo+1,"the mirrored X drag is one undo step");
        ringActor.Write("Direction",1);ApplyDocumentPose();SelectBone("HEAD");
        document.Edit(g=>{var track=g.Clips[0].Tracks.FirstOrDefault(item=>item.Bone==parentName);if(track is null){track=new(){Bone=parentName};g.Clips[0].Tracks.Add(track);}track.Keys.RemoveAll(key=>key.Frame==0);track.Keys.Add(new(){Frame=0,Angle=90});});
        Refresh();ApplyDocumentPose();SelectBone("HEAD");
        var uprightXRect=overlay.BarRect(0);var uprightYRect=overlay.BarRect(1);
        Expect(overlay.BarAxis(0).DistanceTo(new Vector2(0,-1))<.001f&&uprightXRect is not null&&Math.Abs(uprightXRect.Value.Size.X-8)<.001f&&Math.Abs(uprightXRect.Value.Size.Y-144)<.001f,"the X bar stands upright under a 90 degree parent: axis="+overlay.BarAxis(0)+" size="+uprightXRect?.Size);
        Expect(uprightYRect is not null&&Math.Abs(uprightYRect.Value.Size.X-144)<.001f&&Math.Abs(uprightYRect.Value.Size.Y-8)<.001f,"the Y bar lies flat under a 90 degree parent: size="+uprightYRect?.Size);
        var uprightBefore=Clip.Sample(0)["HEAD"];int uprightUndo=document.UndoDepth;
        var uprightBar=overlay.BarPoints[0];
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=uprightBar});
        SurfaceInput(new InputEventMouseMotion{Position=uprightBar+new Vector2(24,0)});
        Expect(overlay.BarKnob(0) is { } uprightKnob&&uprightKnob.DistanceTo(uprightBar)<.5f,"a perpendicular drag leaves the upright X bar knob still: "+overlay.BarKnob(0));
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=uprightBar+new Vector2(24,0)});
        var uprightAfter=Clip.Sample(0)["HEAD"];
        Expect(Math.Abs(uprightAfter.X-uprightBefore.X)<.01&&Math.Abs(uprightAfter.Y-uprightBefore.Y)<.01,"a perpendicular drag on the upright X bar writes no local X/Y: "+uprightAfter.X.ToString("F3")+","+uprightAfter.Y.ToString("F3"));
        Expect(document.UndoDepth==uprightUndo,"a perpendicular drag on the upright X bar adds no undo step");
        Expect(status.Text.Contains("未移动"),"a perpendicular drag on the upright X bar reports no movement: "+status.Text);
        SelectBone("HEAD");
        var alongBefore=Clip.Sample(0)["HEAD"];int alongUndo=document.UndoDepth;
        var alongBar=overlay.BarPoints[0];
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=alongBar});
        SurfaceInput(new InputEventMouseMotion{Position=alongBar+new Vector2(0,-24)});
        Expect(overlay.BarKnob(0) is { } alongKnob&&Math.Abs((alongBar.Y-alongKnob.Y)-24)<.5f,"the upright X bar knob follows the pointer up: "+overlay.BarKnob(0));
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=alongBar+new Vector2(0,-24)});
        var alongAfter=Clip.Sample(0)["HEAD"];
        Expect(alongAfter.X-alongBefore.X>.01&&Math.Abs(alongAfter.Y-alongBefore.Y)<.01,"dragging the upright X bar up changes local X only: "+alongAfter.X.ToString("F3")+","+alongAfter.Y.ToString("F3"));
        Expect(document.UndoDepth==alongUndo+1,"the upright X bar drag is one undo step");
        document.Edit(g=>{var track=g.Clips[0].Tracks.FirstOrDefault(item=>item.Bone==parentName);if(track is null){track=new(){Bone=parentName};g.Clips[0].Tracks.Add(track);}track.Keys.RemoveAll(key=>key.Frame==0);track.Keys.Add(new(){Frame=0,Angle=0});});
        Refresh();ApplyDocumentPose();SelectBone("HEAD");
        var flatRect=overlay.BarRect(0);
        Expect(overlay.BarAxis(0).DistanceTo(new Vector2(1,0))<.001f&&flatRect is not null&&Math.Abs(flatRect.Value.Size.X-144)<.001f&&Math.Abs(flatRect.Value.Size.Y-8)<.001f,"a level parent lays the X bar flat again: axis="+overlay.BarAxis(0)+" size="+flatRect?.Size);
        SelectBone("HAND1_ABOVE");
        Expect(overlay.ChainFocusIndex>=0,"selecting an IK chain bone focuses the chain");
        var chainBones=overlay.FocusChainBones;
        Expect(chainBones.Length==3&&chainBones.Contains("BODY_TOP")&&chainBones.Contains("HAND1_ABOVE")&&chainBones.Contains("HAND1_BELOW"),"the overlay highlights all three chain bones");
        Expect(overlay.EndHandleVisible,"the chain end has a handle");
        await Shot("motion-ring");
        SelectBone("HEAD");
        Expect(overlay.RingVisible,"selecting a bone shows the ring again");
        rootTrackMode=true;
        int rootUndo=document.UndoDepth;
        var blank=SurfacePoint(ringActor.X+400,ringActor.Y+400);
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=blank});
        Expect(!overlay.RingVisible&&highlightedBone is null,"clicking empty canvas hides the ring");
        Expect(Clip.Root.Count==0&&document.UndoDepth==rootUndo,"clearing the selection never writes a root key");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=blank});
        rootTrackMode=false;
        SelectBone("HEAD");
        Expect(overlay.ChainFocusIndex<0,"a non-chain bone clears the chain highlight");
        Change(g=>{g.States.Add(new(){Id="next",Clip=g.Clips[0].Id});g.Clips.Add(new(){Id="next",Duration=20});});
        clipChoice.Select(1);RefreshTimeline();
        Expect(ReferenceEquals(preview,studio)&&ReferenceEquals(preview.World,studioWorld),"switching clips keeps the single preview world");
        document.Undo();
        Expect(ReferenceEquals(preview,studio)&&ReferenceEquals(preview.World,studioWorld),"undo keeps the single preview world");
        int projectIndex=activeProject;
        NewProject(new(MotionTargetKind.PlayerPreset,"Ryoko"),"audit.project",false);
        Expect(projects.Count==projectIndex+2&&document.Graph.Skeleton=="Ryoko"&&Project.Target.Kind==MotionTargetKind.PlayerPreset,"new project targets the preset skeleton");
        Expect(tabs.TabCount==projects.Count,"the character bar lists one tab per project");
        int tabCount=tabs.TabCount;
        NewProject(new(MotionTargetKind.PlayerPreset,"Nana"),"audit.duplicate",false);
        Expect(projects.Count==projectIndex+2&&tabs.TabCount==tabCount&&activeProject==0,"a duplicate target switches to its existing tab");
        Project.ClipIndex=0;
        document=new(new MotionGraph{Id="audit.target",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]});
        Change(g=>g.Clips[0].Duration=30);
        bool targetRejected=false;
        try{ChangeTarget(new(MotionTargetKind.PlayerPreset,"Ryoko"));}catch(InvalidDataException){targetRejected=true;}
        Expect(targetRejected&&projects.Count==projectIndex+2&&tabs.TabCount==tabCount&&status.Text.Contains("标签"),"switching to a role that already has a tab is rejected");
        string ryokoFile=Path.Combine(output,"ryoko.motion.json");
        new MotionGraph{Id="audit.ryoko",Skeleton="Ryoko",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]}.Save(ryokoFile);
        bool openRejected=false;
        try{OpenMotionFile(ryokoFile);}catch(InvalidDataException){openRejected=true;}
        Expect(openRejected&&tabs.TabCount==tabCount&&status.Text.Contains("标签"),"opening a motion file for a taken role is rejected");
        SwitchProject(projectIndex+1);
        await Ready();
        Expect(ReferenceEquals(preview,studio)&&ReferenceEquals(preview.World,studioWorld),"switching projects keeps the single preview world");
        Change(g=>g.Clips[0].Tracks.Add(new(){Bone="HEAD",Keys=[new(){Frame=0}]}));
        Expect(Project.Dirty,"editing a project marks it dirty");
        CloseProject(projects.Count-1);
        Expect(pendingConfirmation is not null&&confirmation.DialogText.Contains("未保存"),"closing a dirty project asks for confirmation");
        pendingConfirmation=null;confirmation.Hide();
        SwitchProject(projectIndex,false);
        Expect(activeProject==projectIndex&&document.Graph.Id!="audit.project","switching tabs rebinds the document");
        RemoveProject(projects.Count-1,false);
        Expect(projects.Count==projectIndex+1&&activeProject==projectIndex,"closing the extra project keeps the active one");
        NewProject(new(MotionTargetKind.Entity,"objEnemyDoll"),"audit.entity",false);
        Expect(document.Graph.Skeleton=="legacy:objEnemyDoll"&&targetLabel.Text.Contains("临时骨架"),"entity project uses a labelled legacy skeleton");
        Change(g=>g.Clips[0].Duration=42);
        string projectFile=Path.Combine(output,"editor.project.motion.json");path=projectFile;Save();
        document=new(MotionGraph.Load(projectFile));Refresh();
        Expect(Clip.Duration==42,"the saved clip survives reload");
        Change(g=>g.Id="audit.export");
        string package=Path.Combine(store.Projects,"audit-motion-project");Directory.CreateDirectory(package);
        NativeContentStore.Save(Path.Combine(package,"mod.json"),new NativeModManifest{FormatVersion=2,Id="audit.motion.project",Name="Audit project",Version="1.0.0",Content=new()});
        ExportToProject(package);
        Expect(File.Exists(Path.Combine(package,"motions","audit.export.motion.json")),"motion export writes the project file");
        var exportedManifest=NativeModManifest.Read(Path.Combine(package,"mod.json"));
        Expect(exportedManifest.Content!.Motions.TryGetValue("audit.export",out var exportedPath)&&exportedPath=="motions/audit.export.motion.json","motion export updates the manifest");
        var exportedContent=new ContentRegistry();using(var exportedManager=new NativeModManager(exportedContent)){var loaded=exportedManager.Reload(package);Expect(loaded.Success&&exportedContent.MotionGraphs.ContainsKey("audit.export"),"exported motion registers through an isolated reload");}
        Expect(MotionExport.ValidId(document.Graph.Id)&&!MotionExport.ValidId("bad id"),"motion export validates ids");
        Change(g=>g.Clips[0].Duration=31);
        bool exportFailed=false;
        var writable=UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute;
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(package,writable&~UnixFileMode.UserWrite&~UnixFileMode.GroupWrite&~UnixFileMode.OtherWrite);
        try{ExportToProject(package);}catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException){exportFailed=true;}
        finally{if(!OperatingSystem.IsWindows())File.SetUnixFileMode(package,writable);}
        Expect(exportFailed&&Project.Dirty,"failed export keeps the project dirty");
        int closed=0;
        ConfirmDirtyClose(()=>closed++);
        Expect(pendingConfirmation is not null&&confirmation.DialogText.Contains(projects[0].Document.Graph.Id),"window close confirms the first dirty project");
        var firstConfirm=pendingConfirmation;pendingConfirmation=null;confirmation.Hide();firstConfirm!();
        Expect(pendingConfirmation is not null&&confirmation.DialogText.Contains(projects[1].Document.Graph.Id),"window close confirms each dirty project in turn");
        pendingConfirmation=null;confirmation.Hide();
        Expect(closed==0,"cancelling a dirty confirmation stops the close");
        ConfirmDirtyClose(()=>closed++);
        var confirmFirst=pendingConfirmation;pendingConfirmation=null;confirmation.Hide();confirmFirst!();
        var confirmSecond=pendingConfirmation;pendingConfirmation=null;confirmation.Hide();confirmSecond!();
        Expect(closed==1,"confirming every dirty project closes the window");
        SwitchProject(projectIndex,false);RemoveProject(projects.Count-1,false);
        Expect(projects.Count==projectIndex+1&&activeProject==projectIndex,"project tabs return to the first document");
        InitializePreview();await Ready();
        var previewBefore=preview!;var previewWorld=previewBefore.World;
        document=new(new MotionGraph{Id="audit.follow",Clips=[new(){Id="idle",Duration=20,Root=[new(){Frame=0,X=5,Y=-3}],Tracks=[new(){Bone="HEAD",Keys=[new(){Frame=0,X=2,Y=-4,Angle=10}]}]}],States=[new(){Id="idle",Clip="idle"}]});
        Refresh();var followActor=new ActorContext(preview.World,preview.Player!);followActor.Write("Direction",-1);
        frame.Value=0;long buildsBefore=preview.Graphics.Builds;
        Change(g=>g.Clips[0].Tracks[0].Keys[0].Angle=35);
        Expect(ReferenceEquals(preview,previewBefore)&&ReferenceEquals(preview.World,previewWorld),"editing reuses the preview world");
        Expect(preview.Graphics.Builds>buildsBefore,"editing redraws the canvas");
        var followClip=document.Graph.Clips[0];var followPose=followClip.Sample(frame.Value)["HEAD"];var followRoot=followClip.RootPose(frame.Value);
        double followX=followActor.X+followRoot.X*-1,followY=followActor.Y+followRoot.Y,followRadians=followActor.Read("image_angle")*Math.PI/180;
        double followExpectedX=followX+(followPose.X*Math.Cos(followRadians)+followPose.Y*Math.Sin(followRadians))*-1,followExpectedY=followY-followPose.X*Math.Sin(followRadians)+followPose.Y*Math.Cos(followRadians);
        Expect(Math.Abs(followActor.Part("HEAD")!.X-followExpectedX)<.001&&Math.Abs(followActor.Part("HEAD")!.Y-followExpectedY)<.001,"edited pose matches the canvas with root offset and mirror");
        RunAction("undo");
        Expect(document.Graph.Clips[0].Tracks[0].Keys[0].Angle==10,"undo restores the edited key");
        followPose=document.Graph.Clips[0].Sample(frame.Value)["HEAD"];
        followExpectedX=followX+(followPose.X*Math.Cos(followRadians)+followPose.Y*Math.Sin(followRadians))*-1;followExpectedY=followY-followPose.X*Math.Sin(followRadians)+followPose.Y*Math.Cos(followRadians);
        Expect(Math.Abs(followActor.Part("HEAD")!.X-followExpectedX)<.001&&Math.Abs(followActor.Part("HEAD")!.Y-followExpectedY)<.001,"undo immediately updates the canvas pose");
        RunAction("redo");
        followPose=document.Graph.Clips[0].Sample(frame.Value)["HEAD"];
        followExpectedX=followX+(followPose.X*Math.Cos(followRadians)+followPose.Y*Math.Sin(followRadians))*-1;followExpectedY=followY-followPose.X*Math.Sin(followRadians)+followPose.Y*Math.Cos(followRadians);
        Expect(Math.Abs(followActor.Part("HEAD")!.X-followExpectedX)<.001&&Math.Abs(followActor.Part("HEAD")!.Y-followExpectedY)<.001,"redo immediately updates the canvas pose");
        Expect(ReferenceEquals(preview,previewBefore),"undo and redo do not rebuild the preview");
        await Shot("motion-follow");
        document=new(MotionImport.FromAst(world.Code["external/Chara/Motion/PlayerNana/Stand0_Damage.txt"],"audit.editor","Nana"));Refresh();
        frame.Value=2;
        document.Edit(g=>g.IkChains=[new(){Root="BODY_TOP",Mid="HAND1_ABOVE",End="HAND1_BELOW"}]);
        Refresh();var canvasActor=PreviewActor!;canvasActor.Write("Direction",1);ApplyDocumentPose();
        var canvasNodes=canvasActor.Services.Motions.Skeleton(canvasActor);
        string? Topmost(double x,double y)
        {
            string? best=null;double bestDepth=double.MaxValue;int bestOrder=-1;
            foreach(var node in canvasNodes)
            {
                var part=canvasActor.World.Instances.FirstOrDefault(item=>item.Id==node.Instance);
                if(part is null||part.Destroyed||!part.Active||!part.Scope.ReadOr("visible").Truth)continue;
                var bounds=canvasActor.World.Bounds(part);
                if(bounds.Empty||x<bounds.Left||x>bounds.Right||y<bounds.Top||y>bounds.Bottom)continue;
                double depth=part.Scope.ReadOr("depth").Number;int order=canvasActor.World.Instances.IndexOf(part);
                if(depth<bestDepth||(depth==bestDepth&&order>bestOrder)){best=node.Name;bestDepth=depth;bestOrder=order;}
            }
            return best;
        }
        Vector2 PointFor(string name)
        {
            var node=canvasNodes.Single(item=>item.Name==name);var bounds=canvasActor.World.Bounds(canvasActor.World.Instances.First(item=>item.Id==node.Instance));
            for(double y=bounds.Top+2;y<=bounds.Bottom-2;y+=2)for(double x=bounds.Left+2;x<=bounds.Right-2;x+=2)if(Topmost(x,y)==name&&Topmost(x+1,y)==name&&Topmost(x-1,y)==name&&Topmost(x,y+1)==name&&Topmost(x,y-1)==name)return SurfacePoint(x,y);
            throw new InvalidOperationException("No unoccluded canvas point for "+name);
        }
        int pickable=0;
        foreach(var node in canvasNodes){var expectedName=Topmost(node.X,node.Y);if(expectedName is null)continue;pickable++;Expect(PickBoneAt(SurfacePoint(node.X,node.Y))==expectedName,"part rect hit picks the topmost bone at "+node.Name);}
        Expect(pickable>0,"visible part rects are pickable");
        bool overlaps=false;
        for(int a=0;a<canvasNodes.Count&&!overlaps;a++)for(int b=a+1;b<canvasNodes.Count&&!overlaps;b++)
        {
            var first=canvasActor.World.Bounds(canvasActor.World.Instances.First(item=>item.Id==canvasNodes[a].Instance));
            var second=canvasActor.World.Bounds(canvasActor.World.Instances.First(item=>item.Id==canvasNodes[b].Instance));
            double left=Math.Max(first.Left,second.Left),right=Math.Min(first.Right,second.Right),top=Math.Max(first.Top,second.Top),bottom=Math.Min(first.Bottom,second.Bottom);
            if(right<left||bottom<top)continue;
            double x=(left+right)/2,y=(top+bottom)/2;
            var expectedName=Topmost(x,y);Expect(expectedName is not null&&expectedName==PickBoneAt(SurfacePoint(x,y)),"overlapping part rects pick the topmost bone");
            overlaps=true;
        }
        Expect(overlaps,"character part rects overlap somewhere");
        string Serialized()=>System.Text.Json.JsonSerializer.Serialize(document.Graph,NativeContentStore.Json);
        var emptyBefore=Serialized();var blankPoint=SurfacePoint(canvasActor.X+400,canvasActor.Y+400);
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=blankPoint});
        Expect(!dragging&&highlightedBone is null&&!overlay.RingVisible,"clicking empty canvas clears the selection");
        Expect(Serialized()==emptyBefore,"clicking empty canvas leaves the document untouched");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=blankPoint});
        var headPoint=PointFor("HEAD");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        Expect(dragging&&highlightedBone is not null&&overlay.RingVisible,"clicking a part selects and highlights the bone");
        int clickUndo=document.UndoDepth;
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=headPoint});
        Expect(!dragging&&Serialized()==emptyBefore&&document.UndoDepth==clickUndo,"a click without movement writes no key and no undo step");
        Expect(highlightedBone is not null,"a click without movement keeps the selection");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Right,Pressed=true,Position=headPoint});
        Expect(!dragging&&Serialized()==emptyBefore,"right click cancels the drag without editing");
        var fkBefore=Clip.Sample(frame.Value)["HEAD"];int fkUndo=document.UndoDepth;
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=headPoint});
        Expect(dragging&&Math.Abs(Clip.Sample(frame.Value)["HEAD"].Angle-fkBefore.Angle)<.001,"pressing an FK bone does not jump");
        var fkTarget=SurfacePoint(CanvasPosition(headPoint).X+24,CanvasPosition(headPoint).Y-8);
        SurfaceInput(new InputEventMouseMotion{Position=fkTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=fkTarget});
        var fkPose=Clip.Sample(frame.Value)["HEAD"];double fkLength=Math.Sqrt(fkBefore.X*fkBefore.X+fkBefore.Y*fkBefore.Y);
        Expect(Math.Abs(Math.Sqrt(fkPose.X*fkPose.X+fkPose.Y*fkPose.Y)-fkLength)<.001,"FK drag keeps the bone length");
        Expect(Clip.Tracks.Single(item=>item.Bone=="HEAD").Keys.Any(key=>key.Frame==frame.Value),"FK drag writes the current frame key");
        Expect(document.UndoDepth==fkUndo+1,"FK drag adds exactly one undo step");
        RunAction("undo");Expect(Math.Abs(Clip.Sample(frame.Value)["HEAD"].Angle-fkBefore.Angle)<.001,"FK drag is one undo step");RunAction("redo");
        var shiftBefore=Clip.Sample(frame.Value)["HEAD"];
        var shiftStart=PointFor("HEAD");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,ShiftPressed=true,Position=shiftStart});
        var shiftTarget=SurfacePoint(CanvasPosition(shiftStart).X-12,CanvasPosition(shiftStart).Y+10);
        SurfaceInput(new InputEventMouseMotion{Position=shiftTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,ShiftPressed=true,Position=shiftTarget});
        var shiftPose=Clip.Sample(frame.Value)["HEAD"];
        Expect(Math.Abs(Math.Sqrt(shiftPose.X*shiftPose.X+shiftPose.Y*shiftPose.Y)-fkLength)<.001,"Shift drag keeps the FK bone length");
        Expect(Math.Abs(shiftPose.Angle-shiftBefore.Angle)>.001,"Shift drag rotates instead of translating");
        RunAction("undo");Expect(Math.Abs(Clip.Sample(frame.Value)["HEAD"].Angle-shiftBefore.Angle)<.001,"Shift drag is one undo step");RunAction("redo");
        var chainBefore=Clip.Sample(frame.Value);
        double midLength=Math.Sqrt(chainBefore["HAND1_ABOVE"].X*chainBefore["HAND1_ABOVE"].X+chainBefore["HAND1_ABOVE"].Y*chainBefore["HAND1_ABOVE"].Y);
        double endLength=Math.Sqrt(chainBefore["HAND1_BELOW"].X*chainBefore["HAND1_BELOW"].X+chainBefore["HAND1_BELOW"].Y*chainBefore["HAND1_BELOW"].Y);
        var endPoint=PointFor("HAND1_BELOW");int ikGestureUndo=document.UndoDepth;
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=endPoint});
        Expect(dragging&&dragMode==DragMode.Ik,"dragging an IK chain end uses IK by default");
        var farPoint=SurfacePoint(canvasActor.X+120,canvasActor.Y-60);
        SurfaceInput(new InputEventMouseMotion{Position=farPoint});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=farPoint});
        Expect(document.UndoDepth==ikGestureUndo+1,"the IK gesture is exactly one undo step");
        var chainPose=Clip.Sample(frame.Value);
        Expect(Math.Abs(Math.Sqrt(chainPose["HAND1_ABOVE"].X*chainPose["HAND1_ABOVE"].X+chainPose["HAND1_ABOVE"].Y*chainPose["HAND1_ABOVE"].Y)-midLength)<.001&&Math.Abs(Math.Sqrt(chainPose["HAND1_BELOW"].X*chainPose["HAND1_BELOW"].X+chainPose["HAND1_BELOW"].Y*chainPose["HAND1_BELOW"].Y)-endLength)<.001,"IK drag keeps both segment lengths");
        Expect(status.Text.Contains("夹"),"unreachable IK target is clamped to the chain reach");
        Expect(Math.Abs(chainPose["BODY_TOP"].Angle-chainBefore["BODY_TOP"].Angle)>.001,"IK drag rotates the chain root");
        var altBefore=Clip.Sample(frame.Value)["BODY_TOP"].Angle;var altPoint=PointFor("HAND1_BELOW");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,AltPressed=true,Position=altPoint});
        Expect(dragging&&dragMode==DragMode.Fk,"alt switches an IK end to temporary FK");
        var altTarget=SurfacePoint(CanvasPosition(altPoint).X+14,CanvasPosition(altPoint).Y-14);
        SurfaceInput(new InputEventMouseMotion{Position=altTarget});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,AltPressed=true,Position=altTarget});
        var altPose=Clip.Sample(frame.Value)["HAND1_BELOW"];
        Expect(Math.Abs(Clip.Sample(frame.Value)["BODY_TOP"].Angle-altBefore)<.001,"temporary FK leaves the IK chain root untouched");
        Expect(Math.Abs(Math.Sqrt(altPose.X*altPose.X+altPose.Y*altPose.Y)-endLength)<.001,"temporary FK keeps the end length");
        RunAction("undo");Expect(Math.Abs(Clip.Sample(frame.Value)["HAND1_BELOW"].X-chainPose["HAND1_BELOW"].X)<.001,"FK override is one undo step");RunAction("redo");
        SelectBone("HAND1_ABOVE");
        Expect(highlightedBone=="HAND1_ABOVE"&&overlay.ChainFocusIndex>=0&&overlay.EndHandleVisible,"bone selection highlights the canvas and its IK chain");
        var cancelBefore=Serialized();var cancelStart=PointFor("HEAD");
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=cancelStart});
        SurfaceInput(new InputEventMouseMotion{Position=SurfacePoint(CanvasPosition(cancelStart).X+30,CanvasPosition(cancelStart).Y-30)});
        SurfaceInput(new InputEventKey{Keycode=Key.Escape,Pressed=true});
        Expect(!dragging&&Serialized()==cancelBefore,"Escape cancels the drag without editing");
        await Shot("motion-drag");
        Change(g=>{g.States.Add(new(){Id="next",Clip=Clip.Id});g.States[0].Transitions.Add(new(){Target="next",Conditions=[new(){Kind=MotionConditionKind.Timer,Comparison=MotionComparison.GreaterOrEqual,Value=4}]});});
        Expect(document.Graph.States[0].Transitions.Count==1,"the transition stays in the document");
        Change(_=>Clip.Events.Add(new(){Start=2,Kind=MotionEventKind.SdkEvent,Value="editor.frame"}));
        path=Path.Combine(output,"editor.motion.json");Save();Expect(File.Exists(path),"motion file saved");
        document=new(MotionGraph.Load(path));Refresh();Expect(Clip.Events.Single().Value=="editor.frame","event configuration survives reload");
        InitializePreview();await Ready();
        for(int i=0;i<12;i++){Step();await Ready();}
        Expect(preview!.Bridge.Services.Motions.State(new(preview.World,preview.Player!))?.State=="next","saved state graph runs in preview");
        string cleanFile=Path.Combine(output,"editor.clean.motion.json");document.Save(cleanFile);
        document=new(new MotionGraph{Id="audit.clean",Clips=[new(){Id="idle"}],States=[new(){Id="idle",Clip="idle"}]});
        path=cleanFile;Refresh();InitializePreview();await Ready();
        Expect(!document.Dirty&&document.Graph.IkChains is null&&document.Graph.Clips[0].Tracks.Count==0,"preview load neither dirties nor writes the document");
        Expect(IsIkEnd("HAND1_BELOW"),"default IK chains stay implicit");
        var fallbackActor=PreviewActor!;
        var fallbackBone=fallbackActor.Services.Motions.Skeleton(fallbackActor).First(node=>node.Parent==fallbackActor.Id);
        var fallbackPart=fallbackActor.World.Instances.First(item=>item.Id==fallbackBone.Instance);
        double fallbackDirection=fallbackActor.Instance.Scope.ReadOr("Direction",1).Number,fallbackRadians=fallbackActor.Read("image_angle")*Math.PI/180;
        double fallbackDir=fallbackPart.Scope.ReadOr("PartPosDir").Number*Math.PI/180,fallbackDis=fallbackPart.Scope.ReadOr("PartPosDis").Number;
        double fallbackLocalX=Math.Cos(fallbackDir)*fallbackDis,fallbackLocalY=-Math.Sin(fallbackDir)*fallbackDis;
        frame.Value=0;ApplyDocumentPose();
        double fallbackExpectedX=fallbackActor.X+(fallbackLocalX*Math.Cos(fallbackRadians)+fallbackLocalY*Math.Sin(fallbackRadians))*fallbackDirection;
        double fallbackExpectedY=fallbackActor.Y-fallbackLocalX*Math.Sin(fallbackRadians)+fallbackLocalY*Math.Cos(fallbackRadians);
        Expect(Math.Abs(fallbackPart.Scope.ReadOr("x").Number-fallbackExpectedX)<.001&&Math.Abs(fallbackPart.Scope.ReadOr("y").Number-fallbackExpectedY)<.001,"missing keys fall back to the live local pose");
        Expect(document.Graph.Clips[0].Tracks.All(track=>track.Bone!=fallbackBone.Name),"missing keys stay implicit");
        document.Edit(g=>g.Clips[0].Tracks.Add(new(){Bone=fallbackBone.Name}));Refresh();ApplyDocumentPose();
        Expect(!document.Graph.Clips[0].Sample(0).ContainsKey(fallbackBone.Name),"empty tracks are not sampled");
        Expect(Math.Abs(fallbackPart.Scope.ReadOr("x").Number-fallbackExpectedX)<.001&&Math.Abs(fallbackPart.Scope.ReadOr("y").Number-fallbackExpectedY)<.001,"empty tracks fall back to the live local pose");
        document.Edit(g=>{var track=g.Clips[0].Tracks.Single(item=>item.Bone==fallbackBone.Name);track.Keys.Add(new(){Frame=0,X=0,Y=0});track.Keys.Add(new(){Frame=10,X=12,Y=-8});});
        Refresh();frame.Value=10;
        var movedPose=Clip.Sample(10)[fallbackBone.Name];var movedRoot=Clip.RootPose(10);
        double movedRadians=fallbackActor.Read("image_angle")*Math.PI/180,movedDirection=fallbackActor.Instance.Scope.ReadOr("Direction",1).Number;
        double movedX=fallbackActor.X+movedRoot.X*movedDirection+(movedPose.X*Math.Cos(movedRadians)+movedPose.Y*Math.Sin(movedRadians))*movedDirection;
        double movedY=fallbackActor.Y+movedRoot.Y-movedPose.X*Math.Sin(movedRadians)+movedPose.Y*Math.Cos(movedRadians);
        Expect(Math.Abs(fallbackPart.Scope.ReadOr("x").Number-movedX)<.001&&Math.Abs(fallbackPart.Scope.ReadOr("y").Number-movedY)<.001,"changing the timeline frame resamples the preview pose");
        string beforeGenerate=Serialized();
        RunAction("referencePose");
        var descriptor=MotionSkeletonDescriptor.FromActor(fallbackActor.World,fallbackActor.Instance,document.Graph.Skeleton);
        Expect(document.Dirty,"generating the reference pose dirties the document");
        Expect(descriptor.Nodes.All(node=>document.Graph.Clips[0].Tracks.Any(track=>track.Bone==node.Name&&track.Keys.Any(key=>key.Frame==0&&Math.Abs(key.X-node.LocalX)<.001&&Math.Abs(key.Y-node.LocalY)<.001))),"generated reference pose writes every part");
        RunAction("undo");
        Expect(Serialized()==beforeGenerate,"generating the reference pose is one undo step");
        string openFile=Path.Combine(output,"editor.open.motion.json");document.Save(openFile);
        document=new(MotionGraph.Load(openFile));Refresh();viewWidth=1280;ApplyView();InitializePreview();await Ready();
        Expect(!document.Dirty,"reopening a saved project leaves the document clean");
        var viewActor=PreviewActor!;
        Expect(viewWidth<1280&&Math.Abs(viewCenterX-viewActor.X)<120,"preview reload auto-fits the target");
        RunAction("fitView");double fittedWidth=viewWidth;var fittedView=PreviewView;
        Expect(viewActor.Services.Motions.Skeleton(viewActor).All(node=>{
            var part=viewActor.World.Instances.First(item=>item.Id==node.Instance);var bounds=viewActor.World.Bounds(part);
            return bounds.Empty||(bounds.Left>=fittedView.ViewX&&bounds.Right<=fittedView.ViewX+fittedView.ViewWidth&&bounds.Top>=fittedView.ViewY&&bounds.Bottom<=fittedView.ViewY+fittedView.ViewHeight);
        }),"fit frames every part");
        var focusedSpin=frame.GetLineEdit();focusedSpin.GrabFocus();
        viewWidth=4096;viewCenterX=0;ApplyView();
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.F,Pressed=true});
        Expect(Math.Abs(viewWidth-4096)<.001,"the F key does not refit while a spin box is focused");
        focusedSpin.ReleaseFocus();
        _UnhandledKeyInput(new InputEventKey{Keycode=Key.F,Pressed=true});
        Expect(viewWidth<4096&&Math.Abs(viewCenterX-viewActor.X)<120,"the F key refits the viewport when no field is focused");
        var cursor=new Vector2(surface.Size.X/2,surface.Size.Y/2);var beforeZoom=CanvasPosition(cursor);
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.WheelUp,Pressed=true,Position=cursor});
        var afterZoom=CanvasPosition(cursor);
        Expect(viewWidth<fittedWidth,"wheel up zooms in");
        Expect(Math.Abs(afterZoom.X-beforeZoom.X)<.01&&Math.Abs(afterZoom.Y-beforeZoom.Y)<.01,"zoom keeps the cursor point fixed");
        double centerBeforePan=viewCenterX;
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Middle,Pressed=true,Position=cursor});
        SurfaceInput(new InputEventMouseMotion{Position=cursor+new Vector2(40,0)});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Middle,Pressed=false,Position=cursor+new Vector2(40,0)});
        Expect(viewCenterX<centerBeforePan,"middle drag pans the viewport");
        double centerBeforeBlankPan=viewCenterY;
        var blankAt=SurfacePoint(viewActor.X+400,viewActor.Y+400);
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=true,Position=blankAt});
        Expect(highlightedBone is null,"dragging empty canvas clears the selection");
        SurfaceInput(new InputEventMouseMotion{Position=blankAt+new Vector2(0,30)});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Left,Pressed=false,Position=blankAt+new Vector2(0,30)});
        Expect(viewCenterY<centerBeforeBlankPan,"dragging empty canvas pans the viewport");
        await Shot("motion-viewport");
        document=new(new MotionGraph{Id="audit.playback",Clips=[new(){Id="idle",Duration=100,Loop=true}],States=[new(){Id="idle",Clip="idle"}]});
        Refresh();var playActor=PreviewActor!;playActor.World.Engine.Write("room_speed",60);
        frame.Value=0;ApplyDocumentPose();UpdatePlaybackOverlay();
        Expect(overlay.Legend.Contains("左")&&overlay.Legend.Contains("右")&&overlay.Legend.Contains("跳")&&overlay.Legend.Contains("冲刺"),"key legend names the four inputs");
        playActor.World.Globals.Write("key",65,[0]);playActor.World.Globals.Write("key",68,[1]);playActor.World.Globals.Write("key",87,[5]);
        UpdatePlaybackOverlay();
        Expect(overlay.Legend.Contains("A")&&overlay.Legend.Contains("D")&&overlay.Legend.Contains("W"),"key legend reads the game key settings");
        playActor.World.Globals.Write("controllMode",playActor.World.Constants["CTRL_KEY"]);
        playActor.World.Globals.Write("key",66,[12]);playActor.World.Globals.Write("key",67,[13]);playActor.World.Globals.Write("key",88,[17]);
        UpdatePlaybackOverlay();
        Expect(overlay.Legend.Contains("B")&&overlay.Legend.Contains("C")&&overlay.Legend.Contains("X"),"key legend follows the controllMode slot offset");
        playActor.World.Globals.Write("controllMode",0);
        playing.ButtonPressed=true;frame.Value=0;RestartPlayback();
        double playBefore=playActor.Services.Motions.State(playActor)!.Frame;
        TickPlayback(FrameContext.FixedDelta);await Ready();
        Expect(playActor.Services.Motions.State(playActor)!.Frame==playBefore+1,"play advances one fixed frame "+playBefore+"->"+playActor.Services.Motions.State(playActor)!.Frame);
        Expect(Math.Abs(timelineSlider.Value-(playBefore+1))<.001,"the progress bar follows playback");
        playing.ButtonPressed=false;
        double pausedFrame=playActor.Services.Motions.State(playActor)!.Frame;
        TickPlayback(FrameContext.FixedDelta);await Ready();
        Expect(playActor.Services.Motions.State(playActor)!.Frame==pausedFrame,"pause holds the state frame");
        Change(g=>g.Clips[0].Duration=99);
        frame.Value=0;Step();await Ready();
        Expect(playActor.Services.Motions.State(playActor)!.Frame==1,"a paused edit syncs before a single frame step");
        playbackSpeed=2;playing.ButtonPressed=true;frame.Value=0;RestartPlayback();
        TickPlayback(FrameContext.FixedDelta);await Ready();
        Expect(playActor.Services.Motions.State(playActor)!.Frame==2,"double speed advances two frames per tick");
        playbackSpeed=1;frame.Value=0;RestartPlayback();
        TickPlayback(.25);await Ready();
        Expect(playActor.Services.Motions.State(playActor)!.Frame==15,"a long frame catches up without dropping steps");
        Change(g=>g.Clips[0].Events.Add(new(){Id="audit.beat",Start=1,Kind=MotionEventKind.SdkEvent,Value="audit.beat"}));
        frame.Value=0;RestartPlayback();playing.ButtonPressed=true;
        TickPlayback(FrameContext.FixedDelta);await Ready();
        Expect(overlay.Playback.Contains("audit.beat"),"playback overlay lists triggered events");
        Change(g=>g.Clips[0].Events.Add(new(){Id="audit.beat2",Start=10,Kind=MotionEventKind.SdkEvent,Value="audit.beat2"}));
        frame.Value=0;RestartPlayback();playing.ButtonPressed=true;
        TickPlayback(FrameContext.FixedDelta*10);await Ready();
        Expect(overlay.Playback.Contains("audit.beat")&&overlay.Playback.Contains("audit.beat2"),"batched steps accumulate every triggered event");
        var playbackEngine=preview!.World.View(0);SyncOverlay();
        Expect(overlay.View.ViewX==playbackEngine.X&&overlay.View.ViewY==playbackEngine.Y&&overlay.View.ViewWidth==playbackEngine.Width&&overlay.View.ViewHeight==playbackEngine.Height,"the overlay camera mirrors the engine view after playback");
        Expect(Math.Abs(overlay.View.ViewX-(viewCenterX-viewWidth/2))>.5,"playback moved the engine camera away from the editor framing");
        preview.Graphics.SpriteGeometryAudit=new();preview.Graphics.Build();
        var renderedDraws=preview.Graphics.SpriteGeometryAudit;int limbDraws=0;double worstLimb=0;
        foreach(var node in PreviewActor!.Services.Motions.Skeleton(PreviewActor))
        {
            var limb=renderedDraws.Where(draw=>(draw.Position-new Vector2((float)node.X,(float)node.Y)).Length()<=.01f).ToArray();
            Expect(limb.Length>0,"the renderer draws the limb for "+node.Name);
            var (overlayX,overlayY)=MotionViewport.ToSurface(node.X,node.Y,surface.Size.X,surface.Size.Y,overlay.View);
            foreach(var draw in limb)
            {
                var (renderedX,renderedY)=RenderedPoint(draw.Position.X,draw.Position.Y);
                worstLimb=Math.Max(worstLimb,Math.Sqrt((overlayX-renderedX)*(overlayX-renderedX)+(overlayY-renderedY)*(overlayY-renderedY)));limbDraws++;
                Expect(Math.Abs(overlayX-renderedX)<=.5&&Math.Abs(overlayY-renderedY)<=.5,"the skeleton joint "+node.Name+" lands on its drawn limb: overlay="+overlayX.ToString("F2")+","+overlayY.ToString("F2")+" rendered="+renderedX.ToString("F2")+","+renderedY.ToString("F2"));
            }
        }
        Expect(limbDraws>0&&worstLimb<=.5,"every skeleton joint lands on its drawn limb: draws="+limbDraws+" worst="+worstLimb.ToString("F3"));
        var panBefore=PreviewView;
        double panPerPixel=MotionViewport.WorldPerPixel(surface.Size.X,surface.Size.Y,panBefore);
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Middle,Pressed=true,Position=new(200,200)});
        SurfaceInput(new InputEventMouseMotion{Position=new(240,200)});
        SurfaceInput(new InputEventMouseButton{ButtonIndex=MouseButton.Middle,Pressed=false,Position=new(240,200)});
        var panAfter=PreviewView;
        Expect(Math.Abs((panBefore.ViewX-panAfter.ViewX)-40*panPerPixel)<1,"panning after playback continues from the renderer camera: "+panBefore.ViewX.ToString("F2")+"->"+panAfter.ViewX.ToString("F2"));
        Expect(Math.Abs(panAfter.ViewY-panBefore.ViewY)<1,"a horizontal pan keeps the vertical camera: "+panBefore.ViewY.ToString("F2")+"->"+panAfter.ViewY.ToString("F2"));
        Change(g=>{g.States.Add(new(){Id="next",Clip="idle"});g.States[0].Transitions.Add(new(){Target="next",Conditions=[new(){Kind=MotionConditionKind.Timer,Comparison=MotionComparison.GreaterOrEqual,Value=2}]});});
        frame.Value=0;RestartPlayback();playing.ButtonPressed=true;
        TickPlayback(FrameContext.FixedDelta*3);await Ready();
        Expect(overlay.Playback.Contains("转换"),"playback overlay shows the active transition");
        frame.Value=7;RestartPlayback();
        Change(g=>g.Clips[0].Duration=101);
        var resumed=playActor.Services.Motions.State(playActor);
        Expect(resumed is not null&&Math.Abs(resumed.Frame-7)<.001,"editing while playing resumes from the current frame");
        Expect(overlay.Playback.Contains("运行")&&overlay.Playback.Contains("帧"),"playback overlay shows the clip and frame without 状态");
        playing.ButtonPressed=false;
        await Shot("motion-playback");
        var help=HelpText();
        Expect(help.Contains("MotionChange")&&help.Contains("PlayerMotionLoad"),"help text names the original motion hooks");
        var registered=MotionImport.Registered(world.Code["external/Chara/Nana/motion_list.txt"]);
        Expect(registered.Count>0&&help.Contains(registered[0].Name+" → "+registered[0].Resource),"help lists registered original motions");
        ApplyToGame();var player=world.Instances.First(i=>!i.Destroyed&&world.IsObject(i.Object,world.Constants["objPlayerDoll"].Int));
        Expect(world.NativeServices!.Motions.State(new(world,player)) is not null,"edited graph applies to the game actor");
        GD.Print("MOTION_EDITOR_AUDIT_OK import=PASS edit=PASS undo=PASS save=PASS reload=PASS preview=PASS game=PASS studio=PASS immediate=PASS state=PASS events=PASS ring=PASS");
    }

    private static void AuditSourceExpect(bool ok,string message){if(!ok)throw new InvalidOperationException("Motion editor: "+message);}

    private void AuditLeftPages()
    {
        AuditSourceExpect(leftPages is TabContainer&&leftPages.GetTabCount()==1,"the left dock opens on the single pose page");
        AuditSourceExpect(leftPages.GetTabTitle(0)=="节点属性修改"&&sourceView is CodeEdit{Editable:true},"the pose page is the editable code view");
        AuditSourceExpect(rawView is null&&showRawData is CheckButton{ButtonPressed:false},"the raw data page is hidden by default");
        showRawData.ButtonPressed=true;
        AuditSourceExpect(leftPages.GetTabCount()==2&&leftPages.GetTabTitle(1)=="原始数据"&&rawView is CodeEdit{Editable:true},"the settings checkbox restores the raw data page");
        string settingsFile=Path.Combine(store.Root,"Editor","action-editor.json");
        AuditSourceExpect(File.Exists(settingsFile)&&File.ReadAllText(settingsFile).Contains("\"ShowRawData\": true",StringComparison.Ordinal),"the raw data toggle persists to the editor settings file");
    }
}

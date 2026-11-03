using Godot;

namespace SlackOfDescent;

public partial class NativeMotionEditor
{
    private void RefreshTabs()
    {
        if(tabs is null)return;
        refreshingTabs=true;tabs.ClearTabs();
        foreach(var project in projects)tabs.AddTab((project.Dirty?"* ":"")+project.Document.Graph.Id);
        tabs.CurrentTab=activeProject;refreshingTabs=false;
    }
    private string TargetLabel()=>Project.Target.Kind switch
    {
        MotionTargetKind.PlayerPreset=>"目标：玩家预设 "+Project.Target.Id,
        MotionTargetKind.PlayerCharacter=>"目标：玩家角色 "+Project.Target.Id,
        _=>"目标：实体 "+Project.Target.Id+"（临时骨架）"
    };
    private void CaptureProjectState()
    {
        Project.ClipIndex=(int)Math.Max(0,clipChoice.Selected);
        Project.BoneIndex=(int)Math.Max(0,bone.Selected);
        Project.Frame=frame.Value;
    }
    private void SwitchProject(int index,bool initializePreview=true)
    {
        if(index<0||index>=projects.Count||index==activeProject)return;
        CaptureProjectState();activeProject=index;ActivateProject();if(initializePreview)InitializePreview();
    }
    private void ActivateProject(){Refresh();frame.Value=Project.Frame;}
    internal void ConfirmDirtyClose(Action done)
    {
        var dirty=projects.Where(project=>project.Dirty).ToList();
        void Step(int position)
        {
            if(position>=dirty.Count){done();return;}
            var project=dirty[position];
            Confirm("项目 "+project.Document.Graph.Id+" 有未保存的修改，关闭后丢失。确定关闭？",()=>Step(position+1));
        }
        Step(0);
    }
    private void CloseProject(int index)
    {
        if(index<0||index>=projects.Count)return;
        if(projects.Count<=1){status.Text="至少保留一个项目。";return;}
        var project=projects[index];
        if(project.Dirty){Confirm("项目 "+project.Document.Graph.Id+" 有未保存的修改，关闭后丢失。确定关闭？",()=>RemoveProject(index));return;}
        RemoveProject(index);
    }
    internal void RemoveProject(int index,bool initializePreview=true)
    {
        if(index<0||index>=projects.Count||projects.Count<=1)return;
        projects.RemoveAt(index);
        if(index<activeProject)activeProject--;
        activeProject=Math.Clamp(activeProject,0,projects.Count-1);
        ActivateProject();if(initializePreview)InitializePreview();
    }
    private List<MotionProjectTarget> TargetChoices()
    {
        var targets=new List<MotionProjectTarget>();
        foreach(string preset in new[]{"Nana","Ryoko","Yuki"})targets.Add(new(MotionTargetKind.PlayerPreset,preset));
        foreach(var character in registry.Characters.Values.OrderBy(value=>value.Id,StringComparer.Ordinal))targets.Add(new(MotionTargetKind.PlayerCharacter,character.Id));
        foreach(var pair in world.Objects.OrderBy(pair=>pair.Value.GetProperty("name").GetString(),StringComparer.Ordinal))
        {
            string name=pair.Value.GetProperty("name").GetString()??"";
            if(name.Contains("objEnemy",StringComparison.Ordinal)||name.Contains("objBlock",StringComparison.Ordinal))targets.Add(new(MotionTargetKind.Entity,name));
        }
        return targets;
    }
    private static string TargetChoiceLabel(MotionProjectTarget target)=>target.Kind switch
    {
        MotionTargetKind.PlayerPreset=>"玩家预设："+target.Id,
        MotionTargetKind.PlayerCharacter=>"玩家角色："+target.Id,
        _=>"实体："+target.Id
    };
    private void NewProjectDialog()
    {
        var targets=TargetChoices();
        var dialog=new ConfirmationDialog{Title="新建项目",OkButtonText="创建",CancelButtonText="取消",Size=new(560,200)};AddChild(dialog);
        var body=Column(dialog);
        var idRow=Row(body);idRow.AddChild(new Label{Text="动作图标识"});var id=new LineEdit{Text="motion",PlaceholderText="motion",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};idRow.AddChild(id);
        var targetRow=Row(body);targetRow.AddChild(new Label{Text="目标对象"});var choice=new OptionButton{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};targetRow.AddChild(choice);
        foreach(var target in targets)choice.AddItem(TargetChoiceLabel(target));
        dialog.Confirmed+=()=>{try{NewProject(targets[Math.Max(0,choice.Selected)],id.Text);}catch(Exception error){ShowMessage(error.GetBaseException().Message);}finally{dialog.QueueFree();}};
        dialog.Canceled+=dialog.QueueFree;dialog.PopupCentered();
    }
    internal void NewProject(MotionProjectTarget target,string id,bool initializePreview=true)
    {
        if(!MotionExport.ValidId(id))throw new InvalidDataException("动作图标识只能包含字母、数字、点、下划线、连字符。");
        if(projects.Any(project=>project.Document.Graph.Id==id))throw new InvalidDataException("项目标识已存在："+id);
        var graph=new MotionGraph{Id=id,Skeleton=target.Skeleton(CharacterPreset(target)),Clips=[new()],States=[new(){Movement=new(){InputSpeed=4,Gravity=.6,JumpSpeed=10}}]};
        CaptureProjectState();projects.Add(new MotionProject(target,new(graph)));activeProject=projects.Count-1;
        ActivateProject();if(initializePreview)InitializePreview();
    }
    private void ChangeTargetDialog()
    {
        Guard(()=>
        {
            if(document.Graph.Clips.Any(clip=>clip.Tracks.Count>0||clip.Root.Count>0))throw new InvalidDataException("当前项目已有轨道，请新建项目或清空轨道后再切换目标。");
            var targets=TargetChoices();
            var dialog=new ConfirmationDialog{Title="切换目标",OkButtonText="切换",CancelButtonText="取消",Size=new(520,170)};AddChild(dialog);
            var body=Column(dialog);
            var row=Row(body);row.AddChild(new Label{Text="目标对象"});var choice=new OptionButton{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(choice);
            foreach(var target in targets)choice.AddItem(TargetChoiceLabel(target));
            dialog.Confirmed+=()=>{try{ChangeTarget(targets[Math.Max(0,choice.Selected)]);}catch(Exception error){ShowMessage(error.GetBaseException().Message);}finally{dialog.QueueFree();}};
            dialog.Canceled+=dialog.QueueFree;dialog.PopupCentered();
        });
    }
    internal void ChangeTarget(MotionProjectTarget target)
    {
        if(document.Graph.Clips.Any(clip=>clip.Tracks.Count>0||clip.Root.Count>0))throw new InvalidDataException("当前项目已有轨道，请新建项目或清空轨道后再切换目标。");
        Project.Target=target;
        Change(graph=>graph.Skeleton=target.Skeleton(CharacterPreset(target)));
        InitializePreview();
    }
    private void ExportToProjectDialog()
    {
        Guard(()=>
        {
            var available=new NativeModLibrary(store).Projects();
            if(available.Count==0)throw new InvalidOperationException("请先在 Mod 窗口创建项目。");
            var dialog=new ConfirmationDialog{Title="保存到 Mod 项目",OkButtonText="导出",CancelButtonText="取消",Size=new(520,170)};AddChild(dialog);
            var body=Column(dialog);
            var row=Row(body);row.AddChild(new Label{Text="项目"});var choice=new OptionButton{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddChild(choice);
            foreach(var project in available)choice.AddItem(project.Manifest.Name+" ["+project.Manifest.Id+"]");
            dialog.Confirmed+=()=>{try{ExportToProject(available[Math.Max(0,choice.Selected)].Directory);}catch(Exception error){ShowMessage(error.GetBaseException().Message);}finally{dialog.QueueFree();}};
            dialog.Canceled+=dialog.QueueFree;dialog.PopupCentered();
        });
    }
    internal void ExportToProject(string directory)
    {
        bool wasDirty=document.Dirty;
        string id=document.Graph.Id;
        if(!MotionExport.ValidId(id))throw new InvalidDataException("动作图标识只能包含字母、数字、点、下划线、连字符。");
        var library=new NativeModLibrary(store);
        var manifest=library.OpenProject(directory);manifest.Content??=new();
        string relative=MotionExport.MotionPath(id);
        string target=Path.Combine(directory,relative.Replace('/',Path.DirectorySeparatorChar));
        bool existed=manifest.Content.Motions.TryGetValue(id,out var previous);
        string backup=target+".export-backup";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if(File.Exists(target))File.Copy(target,backup,true);
        try
        {
            document.Save(target);
            manifest.Content.Motions[id]=relative;
            library.SaveProjectManifest(directory,manifest);
            using var manager=new NativeModManager(new ContentRegistry());
            var reload=manager.Reload(directory);
            if(!reload.Success)throw new InvalidDataException(reload.Diagnostics);
        }
        catch
        {
            if(existed)manifest.Content.Motions[id]=previous!;else manifest.Content.Motions.Remove(id);
            try{library.SaveProjectManifest(directory,manifest);}catch(Exception error){status.Text=error.GetBaseException().Message;}
            if(File.Exists(backup))File.Move(backup,target,true);else if(File.Exists(target))File.Delete(target);
            document.RestoreDirty(wasDirty);
            throw;
        }
        if(File.Exists(backup))File.Delete(backup);
        Refresh();status.Text="已导出到项目："+manifest.Name+" ("+relative+")";
    }
}

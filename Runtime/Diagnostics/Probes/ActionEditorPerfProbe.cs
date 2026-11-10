using Godot;
using System.Diagnostics;

namespace SlackOfDescent;

public partial class ActionEditorPerfProbe : Node
{
    private const double OpenBudgetMs=1200,MemoryBudgetMB=450;

    public override async void _Ready()
    {
        string? temporary=null;
        try
        {
            string project=ProjectSettings.GlobalizePath("res://");
            temporary=Path.Combine(Path.GetTempPath(),"slack-editor-perf-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string root=Path.Combine(project,"GameData");
            var world=new GmWorld(root,Path.Combine(root,"assets"),Path.Combine(temporary,"live"));
            var store=new NativeContentStore(Path.Combine(temporary,"content"));
            var registry=new ContentRegistry();
            var samples=new List<(double OpenMs,double OpenMb,double PreviewMs,double PreviewMb,double ReleasedMb,bool PreviewOnOpen)>();
            for(int run=1;run<=3;run++)
            {
                Collect();await Frames(1);
                long baseline=Memory();
                long started=Stopwatch.GetTimestamp();
                var editor=new NativeActionEditor(world,registry,store,[]){Visible=false};
                AddChild(editor);editor.Show();
                await Frames(1);
                bool previewOnOpen=editor.PreviewReady;
                double openMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long opened=Memory();
                double previewMs=0;
                if(!previewOnOpen)
                {
                    Collect();long triggered=Stopwatch.GetTimestamp();
                    editor.InitializePreview();
                    await Settle(editor);
                    previewMs=Stopwatch.GetElapsedTime(triggered).TotalMilliseconds;
                }
                long loaded=Memory();
                GD.Print("ACTION_EDITOR_PERF run=",run,"/3 openMs=",openMs.ToString("F0")," openMemMB=",MB(opened-baseline).ToString("F1")," previewOnOpen=",previewOnOpen," previewMs=",previewMs.ToString("F0")," previewMemMB=",MB(loaded-opened).ToString("F1")," display=",DisplayServer.GetName());
                editor.RequestClose();
                for(int frame=0;frame<4;frame++)await Frames(1);
                if(IsInstanceValid(editor)&&editor.IsInsideTree()){RemoveChild(editor);editor.QueueFree();await Frames(2);}
                Collect();await Frames(2);
                long closed=Memory();
                if(closed>=loaded)throw new InvalidOperationException("Closing the editor did not release memory: loaded="+MB(loaded).ToString("F1")+"MB closed="+MB(closed).ToString("F1")+"MB");
                samples.Add((openMs,MB(opened-baseline),previewMs,MB(loaded-opened),MB(loaded-closed),previewOnOpen));
            }
            var open=samples.Select(sample=>sample.OpenMs).Order().ToArray();
            var memory=samples.Select(sample=>sample.OpenMb).Order().ToArray();
            var preview=samples.Select(sample=>sample.PreviewMs).Order().ToArray();
            var previewMemory=samples.Select(sample=>sample.PreviewMb).Order().ToArray();
            var released=samples.Select(sample=>sample.ReleasedMb).Order().ToArray();
            double medianOpen=open[1],medianMemory=memory[1];
            GD.Print("ACTION_EDITOR_PERF_MEDIAN openMs=",medianOpen.ToString("F0")," openMemMB=",medianMemory.ToString("F1")," previewMs=",preview[1].ToString("F0")," previewMemMB=",previewMemory[1].ToString("F1")," releasedMB=",released[1].ToString("F1")," display=",DisplayServer.GetName());
            bool lazy=samples.All(sample=>!sample.PreviewOnOpen);
            bool withinBudget=medianOpen<=OpenBudgetMs&&medianMemory<=MemoryBudgetMB;
            GD.Print("ACTION_EDITOR_PERF_VERDICT ",withinBudget?"PASS":"OVER_BUDGET"," openMs=",medianOpen.ToString("F0"),"/",OpenBudgetMs," memMB=",medianMemory.ToString("F1"),"/",MemoryBudgetMB," lazy=",lazy," releasedMB=",released[1].ToString("F1"));
            if(!lazy)throw new InvalidOperationException("Opening the editor still creates the preview world.");
            if(!withinBudget)throw new InvalidOperationException("Editor open exceeds budget: openMs="+medianOpen.ToString("F0")+" memMB="+medianMemory.ToString("F1"));
            GD.Print("ACTION_EDITOR_PERF_OK");GetTree().Quit();
        }
        catch(Exception error){GD.PushError("ACTION_EDITOR_PERF_FAILED "+error);GetTree().Quit(1);}
        finally{try{if(temporary is not null&&Directory.Exists(temporary))Directory.Delete(temporary,true);}catch(Exception error){GD.PushError("ACTION_EDITOR_PERF_CLEANUP "+error);}}
    }

    private async Task Settle(NativeActionEditor editor)
    {
        long started=Stopwatch.GetTimestamp();
        while(!editor.PreviewReady)
        {
            if(Stopwatch.GetElapsedTime(started).TotalSeconds>120)throw new TimeoutException("Preview initialization");
            await Frames(1);
        }
    }
    private async Task Frames(int count){for(int index=0;index<count;index++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
    private static void Collect(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
    private static double MB(long bytes)=>bytes/1048576.0;
    private static long Memory()
    {
        foreach(string line in File.ReadLines("/proc/self/status"))
            if(line.StartsWith("VmRSS:",StringComparison.Ordinal))
                return long.Parse(line[(line.IndexOf(':')+1)..].Replace("kB","",StringComparison.Ordinal).Trim())*1024;
        throw new InvalidOperationException("VmRSS is missing from /proc/self/status.");
    }
}

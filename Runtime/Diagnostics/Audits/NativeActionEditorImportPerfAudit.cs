using Godot;
using System.Diagnostics;

namespace SlackOfDescent;

public partial class NativeActionEditor
{
    internal sealed record ImportPerfSample(int Index,int Total,int Live,int Destroyed,int Viewports,int TempDirectories,long Nodes,long Resources,long ManagedBytes,int Gc0,int Gc1,int Gc2,double ImportMs,double IdleCpuMs);

    internal async Task ImportForensicsAsync(string output,int imports=8)
    {
        long started=Stopwatch.GetTimestamp();
        while(busy||preview?.Player is null)
        {
            if(Stopwatch.GetElapsedTime(started).TotalSeconds>90)throw new TimeoutException("Motion preview initialization");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        }
        var samples=new List<ImportPerfSample>{Sample(0,0,await IdleCpuAsync())};
        int failed=0;
        for(int i=1;i<=imports;i++)
        {
            if(original.ItemCount==0)throw new InvalidOperationException("No original motion is available.");
            original.Select((i-1)%original.ItemCount);
            string expected=original.GetItemText((int)original.Selected).Replace('/','.');
            var previous=document;
            long began=Stopwatch.GetTimestamp();
            ImportOriginal();
            while(busy)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            for(int frame=0;frame<2;frame++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            if(ReferenceEquals(document,previous)||document.Graph.Id!=expected)failed++;
            samples.Add(Sample(i,Stopwatch.GetElapsedTime(began).TotalMilliseconds,0));
        }
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        for(int frame=0;frame<2;frame++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        samples.Add(Sample(-1,0,await IdleCpuAsync()));
        var baseline=samples[0];var loaded=samples[^2];var collected=samples[^1];
        if(loaded.Total!=baseline.Total||loaded.Viewports!=baseline.Viewports)
            throw new InvalidOperationException("Motion editor import accumulates: instances="+loaded.Total+" baseline="+baseline.Total+" viewports="+loaded.Viewports+" baseline="+baseline.Viewports+" failed="+failed);
        double perImport=(loaded.Total-baseline.Total)/(double)imports,heapPerImport=(loaded.ManagedBytes-baseline.ManagedBytes)/(double)imports;
        var table=new System.Text.StringBuilder("IMPORT_PERF_TABLE\n idx total live dead viewports tempdirs nodes resources heapKB gc0 gc1 gc2 importMs idleCpuMs\n");
        foreach(var sample in samples)
            table.Append("IMPORT_PERF_ROW ").Append(sample.Index).Append(' ').Append(sample.Total).Append(' ').Append(sample.Live).Append(' ').Append(sample.Destroyed).Append(' ').Append(sample.Viewports).Append(' ').Append(sample.TempDirectories).Append(' ').Append(sample.Nodes).Append(' ').Append(sample.Resources).Append(' ').Append(sample.ManagedBytes/1024).Append(' ').Append(sample.Gc0).Append(' ').Append(sample.Gc1).Append(' ').Append(sample.Gc2).Append(' ').Append(sample.ImportMs.ToString("F0")).Append(' ').Append(sample.IdleCpuMs.ToString("F0")).Append('\n');
        NativeContentStore.Save(output,samples);
        GD.Print("IMPORT_PERF ",System.Text.Json.JsonSerializer.Serialize(samples));
        GD.Print(table.ToString().TrimEnd('\n'));
        GD.Print("IMPORT_PERF_VERDICT imports=",imports," failed=",failed," instancesPerImport=",perImport.ToString("F2")," heapKBPerImport=",(heapPerImport/1024).ToString("F1"),
            " instancesAfterGc=",collected.Total," retainedAfterGc=",(collected.Total-baseline.Total)," heapKBAfterGc=",(collected.ManagedBytes/1024),
            " idleCpuMsBefore=",baseline.IdleCpuMs.ToString("F0")," idleCpuMsAfter=",collected.IdleCpuMs.ToString("F0"));
        if(failed>0)throw new InvalidOperationException("Motion import did not apply: "+failed+"/"+imports);
        GD.Print("IMPORT_PERF_OK");
    }

    private ImportPerfSample Sample(int index,double importMs,double idleCpuMs)
    {
        var instances=preview?.World.Instances ?? [];
        int total=instances.Count,destroyed=instances.Count(instance=>instance.Destroyed);
        return new(index,total,total-destroyed,destroyed,Descendants(this).Count(node=>node is SubViewport),Directory.GetDirectories(Path.GetTempPath(),"slack-character-preview-*").Length,
            (long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),(long)Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),GC.GetTotalMemory(false),
            GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2),importMs,idleCpuMs);
    }

    private async Task<double> IdleCpuAsync(int frames=120)
    {
        var before=Process.GetCurrentProcess().TotalProcessorTime;
        for(int frame=0;frame<frames;frame++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        return (Process.GetCurrentProcess().TotalProcessorTime-before).TotalMilliseconds;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach(var child in node.GetChildren()){yield return child;foreach(var item in Descendants(child))yield return item;}
    }
}

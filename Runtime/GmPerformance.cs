using System.Diagnostics;

namespace SlackOfDescent;

public sealed class GmPerformance
{
    public readonly bool Enabled=Environment.GetEnvironmentVariable("SLACK_PERF")=="1";
    public readonly bool ScriptsEnabled=Environment.GetEnvironmentVariable("SLACK_PERF_SCRIPTS")=="1";
    private readonly Dictionary<string,(double Total,double Max,int Count)> samples=new();
    private readonly Dictionary<string,(double Total,int Count)> scripts=new();
    public void Script(string key,long start)
    {
        if(!ScriptsEnabled)return;
        var previous=scripts.GetValueOrDefault(key);
        scripts[key]=(previous.Total+Stopwatch.GetElapsedTime(start).TotalMilliseconds,previous.Count+1);
    }
    public long Start()=>Enabled?Stopwatch.GetTimestamp():0;
    public void End(string name,long start)
    {
        if(!Enabled)return;
        double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var previous=samples.GetValueOrDefault(name);
        samples[name]=(previous.Total+ms,Math.Max(previous.Max,ms),previous.Count+1);
    }
    public void Report(long frame,int room)
    {
        if(!Enabled||frame%60!=0)return;
        Console.WriteLine("PERF frame="+frame+" room="+room+" "+string.Join(" ",samples.Select(p=>$"{p.Key}={p.Value.Total/p.Value.Count:F2}ms/max{p.Value.Max:F2}/n{p.Value.Count}")));
        if(ScriptsEnabled)Console.WriteLine("PERF_SCRIPTS "+string.Join(" ",scripts.OrderByDescending(p=>p.Value.Total).Take(12).Select(p=>$"{p.Key}={p.Value.Total:F1}ms/n{p.Value.Count}")));
        samples.Clear();
        scripts.Clear();
    }
}

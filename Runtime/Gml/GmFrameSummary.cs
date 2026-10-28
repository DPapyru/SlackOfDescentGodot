using System.Text.Json.Serialization;

namespace SlackOfDescent;

public sealed class GmFrameSummary
{
    [JsonPropertyName("count")]public int Count{get;init;}
    [JsonPropertyName("meanMs")]public double MeanMs{get;init;}
    [JsonPropertyName("p50Ms")]public double P50Ms{get;init;}
    [JsonPropertyName("p95Ms")]public double P95Ms{get;init;}
    [JsonPropertyName("p99Ms")]public double P99Ms{get;init;}
    [JsonPropertyName("maxMs")]public double MaxMs{get;init;}

    public static double Percentile(List<double> sorted,double p)
    {
        int count=sorted.Count;
        if(count==0)return 0;
        int index=(int)Math.Ceiling(count*p)-1;
        return sorted[Math.Clamp(index,0,count-1)];
    }
    public static GmFrameSummary From(List<double> values)
    {
        values.Sort();
        int count=values.Count;
        return new GmFrameSummary
        {
            Count=count,
            MeanMs=count==0?0:values.Average(),
            P50Ms=Percentile(values,0.5),
            P95Ms=Percentile(values,0.95),
            P99Ms=Percentile(values,0.99),
            MaxMs=count==0?0:values[^1]
        };
    }
}

public sealed class GmCountSummary
{
    [JsonPropertyName("count")]public int Count{get;init;}
    [JsonPropertyName("mean")]public double Mean{get;init;}
    [JsonPropertyName("p50")]public double P50{get;init;}
    [JsonPropertyName("p95")]public double P95{get;init;}
    [JsonPropertyName("p99")]public double P99{get;init;}
    [JsonPropertyName("max")]public double Max{get;init;}

    public static GmCountSummary From(List<double> values)
    {
        values.Sort();
        int count=values.Count;
        return new GmCountSummary
        {
            Count=count,
            Mean=count==0?0:values.Average(),
            P50=GmFrameSummary.Percentile(values,0.5),
            P95=GmFrameSummary.Percentile(values,0.95),
            P99=GmFrameSummary.Percentile(values,0.99),
            Max=count==0?0:values[^1]
        };
    }
}

public sealed class GmFrameCounter
{
    private long lastCount;
    private double lastTotal;
    public long LastCount=>lastCount;
    public long AdvanceCount(long current){long delta=current-lastCount;lastCount=current;return delta;}
    public double AdvanceTotal(double current){double delta=current-lastTotal;lastTotal=current;return delta;}
    public void Reset(){lastCount=0;lastTotal=0;}
}

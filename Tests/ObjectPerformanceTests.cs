using System.Text.Json;
using SlackOfDescent;

public static class ObjectPerformanceTests
{
    public static int Run(GmWorld world)
    {
        var profiler=new GmObjectPerformance{Enabled=true};
        int mud=world.Constants["objMud"].Int,player=world.Constants["objPlayerDoll"].Int;
        long baseline=GC.GetAllocatedBytesForCurrentThread();
        using(profiler.Begin(mud,3))
        {
            GC.KeepAlive(new byte[64000]);
            using(profiler.Begin(player,8))GC.KeepAlive(new byte[128000]);
        }
        long allocated=GC.GetAllocatedBytesForCurrentThread()-baseline;
        var skipped=profiler.Begin(mud,3);skipped.Skip();
        var data=JsonSerializer.SerializeToElement(profiler.Snapshot(world,1,1,allocated,1000));
        var objects=data.GetProperty("objects").EnumerateArray().ToArray();
        long Bytes(string name)=>objects.Single(o=>o.GetProperty("type").GetString()==name).GetProperty("total").GetProperty("allocatedBytes").GetInt64();
        if(Bytes("objMud") is <64000 or >68096)throw new Exception("Nested allocations were charged to the parent");
        if(Bytes("objPlayerDoll") is <128000 or >132096)throw new Exception("Child allocation attribution failed");
        double share=objects.Sum(o=>o.GetProperty("total").GetProperty("drawBuildPercentOfObjects").GetDouble());
        if(Math.Abs(share-100)>0.001)throw new Exception("Draw percentages do not sum to 100");
        if(data.GetProperty("skippedAsyncScopes").GetInt64()!=1)throw new Exception("Suspended scopes must be excluded");
        var empty=JsonSerializer.SerializeToElement(profiler.Snapshot(world,1,1,0,1));
        if(empty.GetProperty("objects").GetArrayLength()!=0)throw new Exception("Samples leaked across cadence windows");
        return 5;
    }
}

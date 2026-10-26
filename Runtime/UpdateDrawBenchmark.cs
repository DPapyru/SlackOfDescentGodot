using Godot;
using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public sealed partial class GameHost
{
    private void AuditUpdateDraw(int frames,int stepsPerFrame)
    {
        int warmup=Math.Max(1,frames/10);
        stepsPerFrame=Math.Max(1,stepsPerFrame);
        var inline=MeasureUpdateDraw(frames,warmup,stepsPerFrame,separated:false);
        var separated=MeasureUpdateDraw(frames,warmup,stepsPerFrame,separated:true);
        var report=new
        {
            kind="update-draw-benchmark",
            room=world.CurrentRoom,
            roomName=world.Rooms.TryGetValue(world.CurrentRoom,out var room)?room.GetProperty("name").GetString():null,
            frames,
            warmupFrames=warmup,
            stepsPerFrame,
            inline,
            separated,
            buildCost=MeasureBuild(),
            environment=new
            {
                engine=Engine.GetVersionInfo()["string"].AsString(),
                os=OS.GetName(),
                display=DisplayServer.GetName(),
                headless=DisplayServer.GetName()=="headless",
                renderer=ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString(),
                cpuCount=OS.GetProcessorCount(),
                maxFps=Engine.MaxFps,
                roomSpeed=world.Engine.Read("room_speed").Number,
                instances=world.Instances.Count,
                tiles=world.Tiles.Count
            }
        };
        string json=JsonSerializer.Serialize(report);
        GD.Print("UPDATE_DRAW_BENCHMARK ",json);
        if(System.Environment.GetEnvironmentVariable("SLACK_BENCH_OUTPUT") is string path)File.WriteAllText(path,json);
    }

    private object MeasureUpdateDraw(int frames,int warmup,int stepsPerFrame,bool separated)
    {
        world.FramePlan=separated?framePlan:null;
        framePlan.Reset(world.CurrentRoom);
        var legacyRedraw=world.Redraw;
        long legacyBuilds=0;
        if(!separated)world.Redraw=()=>{legacyRedraw?.Invoke();legacyBuilds++;};
        var stepSamples=new List<double>(frames*stepsPerFrame);
        var renderSamples=new List<double>(frames);
        var frameSamples=new List<double>(frames);
        long logicSteps=0,builds=0;
        for(int frame=-warmup;frame<frames;frame++)
        {
            long frameStarted=Stopwatch.GetTimestamp();
            for(int step=0;step<stepsPerFrame;step++)
            {
                long started=Stopwatch.GetTimestamp();
                PositionMudWindow();controls.Poll(world);world.Step();controls.EndStep();SyncViewport();
                double stepMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                logicSteps++;
                if(frame>=0)stepSamples.Add(stepMs);
            }
            long renderStarted=Stopwatch.GetTimestamp();
            bool built=!separated||framePlan.Consume() is not null;
            if(built){graphics.Build();canvas.QueueRedraw();builds++;}
            double renderMs=Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
            if(frame<0)continue;
            renderSamples.Add(renderMs);frameSamples.Add(Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds);
        }
        world.Redraw=legacyRedraw;
        var plan=framePlan.Stats;
        world.FramePlan=framePlan;
        return new
        {
            logicSteps,
            builds=separated?builds:legacyBuilds,
            buildsPerLogicStep=separated?(logicSteps==0?0:(double)builds/logicSteps):(logicSteps==0?0:(double)legacyBuilds/logicSteps),
            step=new{meanMs=Mean(stepSamples),p50Ms=Percentile(stepSamples,.5),p95Ms=Percentile(stepSamples,.95),maxMs=Max(stepSamples)},
            render=new{meanMs=Mean(renderSamples),p50Ms=Percentile(renderSamples,.5),p95Ms=Percentile(renderSamples,.95),maxMs=Max(renderSamples)},
            frame=new{meanMs=Mean(frameSamples),p50Ms=Percentile(frameSamples,.5),p95Ms=Percentile(frameSamples,.95),maxMs=Max(frameSamples)},
            published=plan.Published,rendered=plan.Rendered,coalesced=plan.Coalesced,skipped=plan.Skipped
        };
    }

    private object MeasureBuild()
    {
        var samples=new List<double>(64);
        for(int i=0;i<64;i++){long started=Stopwatch.GetTimestamp();graphics.Build();samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);}
        return new{meanMs=Mean(samples),p50Ms=Percentile(samples,.5),p95Ms=Percentile(samples,.95),maxMs=Max(samples)};
    }

    private static double Mean(List<double> values)=>values.Count==0?0:values.Average();
    private static double Max(List<double> values)=>values.Count==0?0:values.Max();
    private static double Percentile(List<double> values,double fraction)
    {
        if(values.Count==0)return 0;
        var sorted=values.Order().ToArray();
        return sorted[Math.Clamp((int)((sorted.Length-1)*fraction),0,sorted.Length-1)];
    }
}

using Godot;
using System.Diagnostics;
using System.Text.Json;

namespace SlackOfDescent;

public partial class GameHost
{
    private void ContentAudit(string[] args)
    {
        int visit=Array.IndexOf(args,"--visit-room");
        if(visit>=0){world.LoadRoom(int.Parse(args[visit+1]));for(int i=0;i<30;i++)StepFrame();}
        int settle=Array.IndexOf(args,"--content-frames");
        if(settle>=0)for(int i=0;i<int.Parse(args[settle+1]);i++)StepFrame();
        int option=Array.IndexOf(args,"--enemy-audit");
        if(option>=0)
        {
            string name=args[option+1];
            var player=world.Select(world.Constants["objPlayerDoll"].Int,new(new GmInstance())).Single();
            var context=new GmContext(player);
            int index=world.Globals.Read("EnemyNum").Int;
            world.Evaluator.ResetBudget();world.Call("createEnemy",[index,name],context);
            var enemy=world.Create(world.Globals.Read("EnemyObject",[index]).Int,player.Scope.Read("x").Number+200,player.Scope.Read("y").Number);
            for(int i=0;i<16;i++)enemy.Scope.Write("ARGUMENT",world.Constants["null"],[i]);
            for(int frame=0;frame<180;frame++)StepFrame();
            GD.Print("CONTENT_ENEMY name=",name," motion=",enemy.Scope.ReadOr("MotionNum")," PASS");
        }
        if(args.Contains("--mud-residue-audit"))
        {
            graphics.SpriteAudit=new();
            var context=new GmContext(new GmInstance());
            var player=world.Select(world.Constants["objPlayerDoll"].Int,context).Single();
            var mud=world.Instances.Where(i=>!i.Destroyed&&i.Object==world.Constants["objMud"].Int).OrderBy(i=>i.Scope.Read("x").Number).First();
            world.Write(player,"x",mud.Scope.Read("x").Number+48);world.Write(player,"y",mud.Scope.Read("y").Number-48);
            for(int frame=0;frame<120;frame++)StepFrame();
            var parts=world.Select(world.Constants["objPlayerParts"].Int,context).ToArray();
            var clothes=world.Select(world.Constants["objExtParts"].Int,context).Where(p=>p.Scope.ReadOr("DrawTarget").Int==player.Id).ToArray();
            var residues=parts.Select(p=>(Part:p,Index:-1)).Concat(clothes.SelectMany(p=>Enumerable.Range(0,p.Scope.Read("SpriteNum").Int).Select(i=>(Part:p,Index:i)))).ToArray();
            GD.Print("CONTENT_MUD_ORIGINAL_GUARD clothing_blocked=",clothes.Count(p=>(p.Scope.Read("x").Int&p.Scope.Read("y").Int&p.Scope.Read("visible").Int)==0));
            foreach(var clothing in clothes){world.Write(clothing,"x",1);world.Write(clothing,"y",1);}
            double Amount((GmInstance Part,int Index) p)=>p.Part.Scope.Read("qKOF",p.Index<0?null:[p.Index]).Number;
            double Residue()=>residues.Sum(Amount);
            double[] Totals()=>[residues.Where(p=>p.Index<0).Sum(Amount),residues.Where(p=>p.Index>=0).Sum(Amount)];
            (int Draws,int Pixels) Coverage()
            {
                var ids=residues.SelectMany(p=>new[]{p.Part.Scope.Read("SPR_MUD",p.Index<0?[0]:[p.Index,0]).Int,p.Part.Scope.Read("SPR_MUD",p.Index<0?[1]:[p.Index,1]).Int}).ToHashSet();
                var draws=graphics.SpriteAudit.Where(d=>ids.Contains(d.Sprite)&&d.Alpha>0).ToArray();
                return(draws.Length,draws.Sum(d=>graphics.SpriteOpaquePixels(d.Sprite,d.Frame)));
            }
            double before=Residue();var beforeGroups=Totals();
            void DryFrame(){world.Write(player,"x",97);world.Write(player,"y",1599);world.Write(player,"speed",0);StepFrame();}
            DryFrame();double after=Residue();var afterGroups=Totals();var firstCoverage=Coverage();
            for(int frame=0;frame<120;frame++)DryFrame();
            var laterCoverage=Coverage();
            GD.Print("CONTENT_MUD before=",before," after_one=",after," after_120=",Residue()," first_pixels=",firstCoverage.Pixels," later_pixels=",laterCoverage.Pixels);
            RegressionExpect(before>0&&after>0,"mud remains after leaving");
            RegressionExpect(Residue()>0&&Residue()<after,"mud gradually decreases");
            RegressionExpect(firstCoverage.Pixels>0&&laterCoverage.Pixels>0,"mud overlay remains visible after leaving");
            var laterGroups=Totals();
            GD.Print("CONTENT_MUD_GROUPS before=",JsonSerializer.Serialize(beforeGroups)," after_one=",JsonSerializer.Serialize(afterGroups)," after_120=",JsonSerializer.Serialize(laterGroups));
            foreach(int group in new[]{0,1})RegressionExpect(beforeGroups[group]>0&&afterGroups[group]>0&&laterGroups[group]>0&&laterGroups[group]<afterGroups[group],"mud remains and decays on "+(group==0?"body":"clothing"));
            double washBefore=Residue();
            var water=world.Create(world.Constants["objWaterBase1"].Int,player.Scope.Read("x").Number-64,player.Scope.Read("y").Number-256,run:false);
            world.Write(water,"image_xscale",128);world.Write(water,"image_yscale",128);world.Write(water,"visible",0);
            world.Evaluator.ResetBudget();
            RegressionExpect(world.Call("place_meeting",[player.Scope.Read("x"),player.Scope.Read("y"),water.Id],new(player)).Truth,"water audit overlaps player");
            DryFrame();double washFirst=Residue();
            RegressionExpect(washFirst>0&&washFirst<washBefore,"cleaning begins gradually for body and clothing");
            var washedGroups=Totals();
            foreach(int group in new[]{0,1})RegressionExpect(washedGroups[group]>0&&washedGroups[group]<laterGroups[group],"water cleaning begins on "+(group==0?"body":"clothing"));
            RegressionExpect(residues.Any(p=>p.Part.Scope.Read("qIsS",p.Index<0?null:[p.Index]).Number<0),"water cleaning is triggered by actual collision");
            for(int frame=0;frame<120;frame++)DryFrame();
            GD.Print("CONTENT_MUD_WASH before=",washBefore," first=",washFirst," later=",Residue()," body_parts=",parts.Length," clothing_parts=",clothes.Length);
            water.Destroyed=true;
            graphics.SpriteAudit=null;
        }
        if(args.Contains("--route-audit"))
        {
            foreach(var _ in AuditRoute(false)){}
        }
        int binding=Array.IndexOf(args,"--bind-audit");
        if(binding>=0)
        {
            foreach(var _ in AuditBinding(args[binding+1],false)){}
        }
    }

    private IEnumerable<int> AuditBinding(string name,bool rendering)
    {
        var context=new GmContext(new GmInstance());
        var player=world.Select(world.Constants["objPlayerDoll"].Int,context).Single();
        var enemy=world.Instances.FirstOrDefault(i=>!i.Destroyed&&i.Scope.Values.ContainsKey("PARTS_MAX")&&i.Scope.ReadOr("index",-1).Int>=0&&(world.IsObject(i.Object,world.Constants["objEnemyDoll"].Int)||world.IsObject(i.Object,world.Constants["objEnemyDollNoDeactivate"].Int))&&world.Globals.Values.GetValueOrDefault(GmScope.Key("EnemyName",[i.Scope.Read("index").Int])).ToString()==name);
        if(enemy is null)
        {
            int index=world.Globals.Read("EnemyNum").Int;
            world.Evaluator.ResetBudget();world.Call("createEnemy",[index,name],new(player));
            enemy=world.Create(world.Globals.Read("EnemyObject",[index]).Int,name=="BossRobot0"?1200:96,1600);
            for(int i=0;i<16;i++)enemy.Scope.Write("ARGUMENT",world.Constants["null"],[i]);
        }
        world.Write(player,"x",enemy.Scope.Read("x"));world.Write(player,"y",enemy.Scope.Read("y"));
        var args=OS.GetCmdlineUserArgs();int scenarioOption=Array.IndexOf(args,"--bind-scenario");
        if(scenarioOption<0)throw new ArgumentException("--bind-scenario required");
        using var document=JsonDocument.Parse(args[scenarioOption+1]);var scenario=document.RootElement;
        var enemyContext=new GmContext(enemy,player);
        string? setup=scenario.TryGetProperty("setup",out var source)?world.Compile(source.GetString()!):null;
        bool collision=scenario.TryGetProperty("collision",out var acquire)&&acquire.GetBoolean();
        double startX=enemy.Scope.Read("x").Number,startY=enemy.Scope.Read("y").Number;
        int boundFrames=0;
        for(int cycle=0;cycle<3;cycle++)
        {
            world.Evaluator.ResetBudget();
            world.Globals.Write("PlayerHP",100);world.Globals.Write("GameOver",0);
            world.Write(enemy,"x",startX);world.Write(enemy,"y",startY);world.Write(enemy,"speed",0);
            world.Write(player,"x",startX);world.Write(player,"y",startY);world.Write(player,"speed",0);
            world.Write(player,"INV",0);
            world.Write(enemy,"col",player.Id);
            if(name=="Saw")world.Write(enemy,"HitFlag",1);
            if(collision)
            {
                world.Call("MotionChange",[enemy.Scope.Read("STAND"),0,1],enemyContext);
                for(int frame=0;frame<30&&!player.Scope.Read("BINDED").Truth;frame++)
                {
                    world.Write(player,"x",enemy.Scope.Read("x"));world.Write(player,"y",enemy.Scope.Read("y").Number-60);world.Write(player,"vspeed",3);
                    StepFrame();if(rendering)yield return 0;
                }
            }
            else if(setup is not null)world.Evaluator.Run(setup,enemyContext);
            else
            {
                var bindArguments=scenario.GetProperty("arguments").EnumerateArray().Select((n,i)=>i==0?new GmValue(player.Id):world.Evaluator.Eval(n,enemyContext)).ToArray();
                world.Call("startBind",bindArguments,enemyContext);
                int motion=world.Evaluator.Eval(scenario.GetProperty("motion"),enemyContext).Int;
                world.Call("MotionChange",[motion,0,1],enemyContext);
            }
            RegressionExpect(player.Scope.Read("BINDED").Truth&&enemy.Scope.Read("BindTarget").Int==player.Id&&(collision||enemy.Scope.Read("BIND").Truth),"bind initialized "+name);
            var samples=new List<double>();int frames=0;
            for(int frame=0;frame<180;frame++)
            {
                long start=Stopwatch.GetTimestamp();StepFrame();samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if(player.Scope.Read("BINDED").Truth&&enemy.Scope.Read("BindTarget").Int==player.Id)frames++;
                if(rendering)yield return 0;
            }
            boundFrames+=frames;samples.Sort();
            RegressionExpect(frames>0,"binding actually advanced "+name);
            GD.Print("CONTENT_BIND name=",name," cycle=",cycle," bound_frames=",frames," motion=",enemy.Scope.Read("MotionNum")," mean_ms=",samples.Average().ToString("F3")," p95_ms=",samples[(int)(samples.Count*0.95)].ToString("F3")," resources=",JsonSerializer.Serialize(graphics.Statistics()));
            world.Evaluator.ResetBudget();world.Call("endBind",[],enemyContext);
            world.Call("CharaStatusResetRest",[],new(player));
            foreach(string standing in new[]{"STAND","IDLE","SMALL_STOP"})if(enemy.Scope.Values.ContainsKey(standing)){world.Call("MotionChange",[enemy.Scope.Read(standing),0,1],enemyContext);break;}
            RegressionExpect(!player.Scope.Read("BINDED").Truth&&!enemy.Scope.Read("BIND").Truth,"bind release "+name);
            world.Write(player,"x",enemy.Scope.Read("x").Number+512);
            for(int frame=0;frame<60;frame++){StepFrame();if(rendering)yield return 0;}
        }
        GD.Print("CONTENT_BIND_OK name=",name," total_bound_frames=",boundFrames);
    }

    private IEnumerable<int> AuditRoute(bool rendering)
    {
        var context=new GmContext(new GmInstance());
        var player=world.Select(world.Constants["objPlayerDoll"].Int,context).Single();
        foreach(int round in Enumerable.Range(0,3))foreach(double x in new[]{96.0,2000,4000,6000,8000,6000,4000,2000,96})
        {
            world.Write(player,"x",x);world.Write(player,"y",1600);world.Globals.Write("PlayerHP",100);world.Globals.Write("GameOver",0);
            var samples=new List<double>();var renderTimes=new List<double>();
            for(int frame=0;frame<90;frame++)
            {
                long start=Stopwatch.GetTimestamp();StepFrame();samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if(rendering){yield return 0;renderTimes.Add(GetProcessDeltaTime()*1000);}
            }
            samples.Sort();
            GD.Print("CONTENT_ROUTE round=",round," x=",x," mean_ms=",samples.Average().ToString("F3")," p95_ms=",samples[84].ToString("F3")," render_fps=",rendering?(1000/renderTimes.Average()).ToString("F2"):"headless"," instances=",world.Instances.Count," resources=",JsonSerializer.Serialize(graphics.Statistics()));
        }
    }
}

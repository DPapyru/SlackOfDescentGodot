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
            double Residue()=>residues.Sum(p=>p.Part.Scope.Read("qKOF",p.Index<0?null:[p.Index]).Number);
            (int Draws,int Pixels) Coverage()
            {
                var ids=residues.SelectMany(p=>new[]{p.Part.Scope.Read("SPR_MUD",p.Index<0?[0]:[p.Index,0]).Int,p.Part.Scope.Read("SPR_MUD",p.Index<0?[1]:[p.Index,1]).Int}).ToHashSet();
                var draws=graphics.SpriteAudit.Where(d=>ids.Contains(d.Sprite)&&d.Alpha>0).ToArray();
                return(draws.Length,draws.Sum(d=>graphics.SpriteOpaquePixels(d.Sprite,d.Frame)));
            }
            double before=Residue();
            world.Write(player,"x",96);world.Write(player,"y",1600);
            StepFrame();double after=Residue();var firstCoverage=Coverage();
            for(int frame=0;frame<120;frame++)StepFrame();
            var laterCoverage=Coverage();
            GD.Print("CONTENT_MUD before=",before," after_one=",after," after_120=",Residue()," first_pixels=",firstCoverage.Pixels," later_pixels=",laterCoverage.Pixels);
            RegressionExpect(before>0&&after>0,"mud remains after leaving");
            RegressionExpect(Residue()>0&&Residue()<after,"mud gradually decreases");
            RegressionExpect(firstCoverage.Pixels>0&&laterCoverage.Pixels>0,"mud overlay remains visible after leaving");
            double washBefore=Residue();
            var water=world.Create(world.Constants["objWaterBase1"].Int,player.Scope.Read("x").Number-512,player.Scope.Read("y").Number-512,run:false);
            world.Write(water,"image_xscale",128);world.Write(water,"image_yscale",128);world.Write(water,"visible",0);
            StepFrame();double washFirst=Residue();
            RegressionExpect(washFirst>0&&washFirst<washBefore,"cleaning begins gradually for body and clothing");
            for(int frame=0;frame<120;frame++)StepFrame();
            GD.Print("CONTENT_MUD_WASH before=",washBefore," first=",washFirst," later=",Residue()," body_parts=",parts.Length," clothing_parts=",clothes.Length);
            water.Destroyed=true;
            graphics.SpriteAudit=null;
        }
        if(args.Contains("--route-audit"))
        {
            foreach(var _ in AuditRoute(false)){}
        }
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

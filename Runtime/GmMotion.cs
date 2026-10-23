namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private static readonly (string Field,GmValue Value)[] MotionPartDefaults =
    [
        ("Left",1),("Right",-1),("Direction",1),("Angle",0),("ScaleX",1),("ScaleY",1),
        ("PartPosDir",0),("PartPosDis",0),("PartPicture",0),("chgAngle",0),
        ("chgScaleX",0),("chgScaleY",0),("chgPartPosDir",0),("chgPartPosDis",0)
    ];
    private static readonly (string Field,GmValue Value)[] MotionPartRows =
    [
        ("motPartPosDir",0),("motPartPosDis",0),("motPartAngle",0),
        ("motPartPicture",0),("motPartScaleX",1),("motPartScaleY",1)
    ];
    private static int MotionLoopCount(GmValue maximum) => maximum.Number>0?checked((int)Math.Ceiling(maximum.Number)):0;
    private GmInstance[] MotionParts(GmScope scope,int count,GmContext context)
    {
        var parts=new List<GmInstance>();
        for(int p=0;p<count;p++)parts.AddRange(Select(scope.Read("Process",[p]).Int,context));
        return parts.ToArray();
    }
    private GmValue LoadPlayerMotion(GmValue[] args,GmContext caller)
    {
        var context=new GmContext(caller.Self,caller.Other,args);
        var scope=context.Self.Scope;int frames=MotionLoopCount(scope.Read("KEYFRM_MAX")),parts=MotionLoopCount(scope.Read("PARTS_MAX"));
        scope.Write("data_motLong",Constants["null"]);
        scope.FillArrayPrefix("data_motStep",0,frames,Constants["null"]);
        scope.FillArrayPrefix("data_motPosX",0,frames,0);
        scope.FillArrayPrefix("data_motPosY",0,frames,0);
        if(frames>0)foreach(var part in MotionParts(scope,parts,context))
            foreach(var field in MotionPartRows)part.Scope.FillArrayPrefix("data_"+field.Field,0,frames,field.Value);
        scope.Write("KeyFrm",Math.Max(0,frames));if(frames>0)scope.Write("i",Math.Max(0,parts));
        foreach(var node in Code[Scripts["setMotionForPlayer"]][1].EnumerateArray())
            if(node[0].GetString()=="if")Evaluator.Eval(node,context);
        frames=MotionLoopCount(scope.Read("KEYFRM_MAX"));parts=MotionLoopCount(scope.Read("PARTS_MAX"));
        int motion=args[0].Int;
        scope.Write("motString","Chara/Motion/"+args[1]+".txt",[motion]);
        scope.Write("motLong",scope.Read("data_motLong"),[motion]);
        foreach(string field in new[]{"motStep","motPosX","motPosY"})scope.CopyArrayPrefix("data_"+field,field,motion,frames);
        if(frames>0)foreach(var part in MotionParts(scope,parts,context))
            foreach(var field in MotionPartRows)part.Scope.CopyArrayPrefix("data_"+field.Field,field.Field,motion,frames);
        scope.Write("KeyFrm",Math.Max(0,frames));if(frames>0)scope.Write("i",Math.Max(0,parts));return 0;
    }

    private GmValue InitializeMotion(string name,GmValue[] args,GmContext caller)
    {
        var context=new GmContext(caller.Self,caller.Other,args);
        var scope=context.Self.Scope;
        foreach(var node in Code[Scripts[name]][1].EnumerateArray())
        {
            var tag=node[0].GetString();
            if(tag=="for"||tag=="call"&&node[1].GetString()=="DoubleMotionInitialize")continue;
            Evaluator.Eval(node,context);
        }
        if(name=="DoubleMotionInitialize")
        {
            int motions=MotionLoopCount(scope.Read("DoubleMOTION_MAX")),frames=MotionLoopCount(scope.Read("DoubleKEYFRM_MAX")),parts=MotionLoopCount(scope.Read("PARTS_MAX"));
            scope.Write("DoubleMotion",Math.Max(0,motions));
            if(motions>0)scope.Write("DoubleKeyFrm",Math.Max(0,frames));
            if(motions>0&&frames>0)
            {
                scope.Write("i",Math.Max(0,parts));
                foreach(var part in MotionParts(scope,parts,context))
                    foreach(var field in new[]{"DoublechgAngle","DoublechgScaleX","DoublechgScaleY","DoublechgPartPosDir","DoublechgPartPosDis"})part.Scope.Write(field,0);
            }
            return 0;
        }
        int bindMax=MotionLoopCount(scope.Read("BIND_MAX")),bindFrames=MotionLoopCount(scope.Read("BINDKEYFRM_MAX"));
        if(bindFrames>0)scope.FillArrayPrefix("bndLong",0,bindMax,Constants["null"]);
        for(int bind=0;bind<bindMax;bind++)
        {
            scope.FillArrayPrefix("bndStep",bind,bindFrames,Constants["null"]);
            scope.FillArrayPrefix("bndPosX",bind,bindFrames,0);
            scope.FillArrayPrefix("bndPosY",bind,bindFrames,0);
            scope.FillArrayPrefix("bndParts",bind,bindFrames,Constants["null"]);
        }
        scope.Write("Bind",Math.Max(0,bindMax));if(bindMax>0)scope.Write("BindKeyFrm",Math.Max(0,bindFrames));
        int motionMax=MotionLoopCount(scope.Read("MOTION_MAX")),frameMax=MotionLoopCount(scope.Read("KEYFRM_MAX")),partMax=MotionLoopCount(scope.Read("PARTS_MAX"));
        if(frameMax>0)scope.FillArrayPrefix("motLong",0,motionMax,Constants["null"]);
        var motionParts=motionMax>0&&frameMax>0?MotionParts(scope,partMax,context):[];
        foreach(var part in motionParts)foreach(var field in MotionPartDefaults)part.Scope.Write(field.Field,field.Value);
        for(int motion=0;motion<motionMax;motion++)
        {
            scope.FillArrayPrefix("motStep",motion,frameMax,Constants["null"]);
            scope.FillArrayPrefix("motPosX",motion,frameMax,0);
            scope.FillArrayPrefix("motPosY",motion,frameMax,0);
            foreach(var part in motionParts)foreach(var field in MotionPartRows)
                part.Scope.FillArrayPrefix(field.Field,motion,frameMax,field.Value);
        }
        scope.Write("Motion",Math.Max(0,motionMax));if(motionMax>0)scope.Write("KeyFrm",Math.Max(0,frameMax));if(motionMax>0&&frameMax>0)scope.Write("i",Math.Max(0,partMax));
        return InitializeMotion("DoubleMotionInitialize",args.Take(3).ToArray(),context);
    }
}

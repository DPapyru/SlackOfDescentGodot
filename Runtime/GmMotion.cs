namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private GmValue LoadPlayerMotion(GmValue[] args,GmContext caller)
    {
        var context=new GmContext(caller.Self,caller.Other,args);
        var scope=context.Self.Scope;int frames=scope.Read("KEYFRM_MAX").Int,parts=scope.Read("PARTS_MAX").Int;
        scope.Write("data_motLong",Constants["null"]);
        for(int frame=0;frame<frames;frame++)
        {
            foreach(var field in new[]{"data_motStep","data_motPosX","data_motPosY"})scope.Write(field,field=="data_motStep"?Constants["null"]:0,[frame]);
            for(int p=0;p<parts;p++)foreach(var part in Select(scope.Read("Process",[p]).Int,context).ToArray())
                foreach(var field in new[]{"data_motPartPosDir","data_motPartPosDis","data_motPartAngle","data_motPartPicture","data_motPartScaleX","data_motPartScaleY"})part.Scope.Write(field,field is "data_motPartScaleX" or "data_motPartScaleY"?1:0,[frame]);
        }
        scope.Write("KeyFrm",Math.Max(0,frames));if(frames>0)scope.Write("i",Math.Max(0,parts));
        var nodes=Code[Scripts["setMotionForPlayer"]][1].EnumerateArray().ToArray();
        foreach(var node in nodes)if(node[0].GetString()=="if")Evaluator.Eval(node,context);
        scope.Write("motString","Chara/Motion/"+args[1]+".txt",[args[0].Int]);
        scope.Write("motLong",scope.Read("data_motLong"),[args[0].Int]);
        for(int frame=0;frame<frames;frame++)
        {
            foreach(var field in new[]{"motStep","motPosX","motPosY"})scope.Write(field,scope.Read("data_"+field,[frame]),[args[0].Int,frame]);
            for(int p=0;p<parts;p++)foreach(var part in Select(scope.Read("Process",[p]).Int,context).ToArray())
                foreach(var field in new[]{"motPartPosDir","motPartPosDis","motPartAngle","motPartPicture","motPartScaleX","motPartScaleY"})part.Scope.Write(field,part.Scope.Read("data_"+field,[frame]),[args[0].Int,frame]);
        }
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
            int motions=scope.Read("DoubleMOTION_MAX").Int,frames=scope.Read("DoubleKEYFRM_MAX").Int,parts=scope.Read("PARTS_MAX").Int;
            scope.Write("DoubleMotion",Math.Max(0,motions));
            if(motions>0)scope.Write("DoubleKeyFrm",Math.Max(0,frames));
            if(motions>0&&frames>0)
            {
                scope.Write("i",Math.Max(0,parts));
                for(int p=0;p<parts;p++)foreach(var part in Select(scope.Read("Process",[p]).Int,context).ToArray())
                    foreach(var field in new[]{"DoublechgAngle","DoublechgScaleX","DoublechgScaleY","DoublechgPartPosDir","DoublechgPartPosDis"})part.Scope.Write(field,0);
            }
            return 0;
        }
        int bindMax=scope.Read("BIND_MAX").Int,bindFrames=scope.Read("BINDKEYFRM_MAX").Int;
        for(int bind=0;bind<bindMax;bind++)for(int frame=0;frame<bindFrames;frame++)
        {
            scope.Write("bndLong",Constants["null"],[bind]);
            foreach(var field in new[]{"bndStep","bndPosX","bndPosY","bndParts"})scope.Write(field,field is "bndStep" or "bndParts"?Constants["null"]:0,[bind,frame]);
        }
        scope.Write("Bind",Math.Max(0,bindMax));if(bindMax>0)scope.Write("BindKeyFrm",Math.Max(0,bindFrames));
        int motionMax=scope.Read("MOTION_MAX").Int,frameMax=scope.Read("KEYFRM_MAX").Int,partMax=scope.Read("PARTS_MAX").Int;
        for(int motion=0;motion<motionMax;motion++)for(int frame=0;frame<frameMax;frame++)
        {
            scope.Write("motLong",Constants["null"],[motion]);
            foreach(var field in new[]{"motStep","motPosX","motPosY"})scope.Write(field,field=="motStep"?Constants["null"]:0,[motion,frame]);
            for(int p=0;p<partMax;p++)foreach(var part in Select(scope.Read("Process",[p]).Int,context).ToArray())
            {
                foreach(var pair in new Dictionary<string,GmValue>{{"Left",1},{"Right",-1},{"Direction",1},{"Angle",0},{"ScaleX",1},{"ScaleY",1},{"PartPosDir",0},{"PartPosDis",0},{"PartPicture",0},{"chgAngle",0},{"chgScaleX",0},{"chgScaleY",0},{"chgPartPosDir",0},{"chgPartPosDis",0}})part.Scope.Write(pair.Key,pair.Value);
                foreach(var field in new[]{"motPartPosDir","motPartPosDis","motPartAngle","motPartPicture","motPartScaleX","motPartScaleY"})part.Scope.Write(field,field is "motPartScaleX" or "motPartScaleY"?1:0,[motion,frame]);
            }
        }
        scope.Write("Motion",Math.Max(0,motionMax));if(motionMax>0)scope.Write("KeyFrm",Math.Max(0,frameMax));if(motionMax>0&&frameMax>0)scope.Write("i",Math.Max(0,partMax));
        return InitializeMotion("DoubleMotionInitialize",args.Take(3).ToArray(),context);
    }
}

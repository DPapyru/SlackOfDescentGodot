using System.Text.Json;

namespace SlackOfDescent;

public readonly record struct MotionRegistered(string Name,string Resource);

public static class MotionImport
{
    public static IReadOnlyList<MotionRegistered> Registered(JsonElement ast)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var list=new List<MotionRegistered>();
        Walk(ast);
        return list;

        void Walk(JsonElement node)
        {
            if(node.ValueKind!=JsonValueKind.Array||node.GetArrayLength()<2)return;
            switch(node[0].GetString())
            {
                case "group":
                    if(node[1].ValueKind==JsonValueKind.Array)foreach(var child in node[1].EnumerateArray())Walk(child);
                    break;
                case "call":
                    if(node[1].GetString()!="PlayerMotionLoad"||node[2].ValueKind!=JsonValueKind.Array||node[2].GetArrayLength()<3)return;
                    string name=Text(node[2][1]),resource=Text(node[2][2]);
                    if(name.Length==0||resource.Length==0||!seen.Add(name))return;
                    list.Add(new(name,resource));
                    break;
            }
        }

        static string Text(JsonElement argument)=>argument.ValueKind==JsonValueKind.Array&&argument.GetArrayLength()>=2&&argument[0].GetString()=="str"?argument[1].GetString()??"":"";
    }

    public static IReadOnlyList<MotionRegistered> Registered(IReadOnlyDictionary<string,JsonElement> code,string preset)
    {
        if(code.TryGetValue("external/Chara/"+preset+"/motion_list.txt",out var list))return Registered(list);
        return code.TryGetValue("external/Chara/Nana/motion_list.txt",out var fallback)?Registered(fallback):[];
    }

    public static bool TryFirstRegistered(IReadOnlyDictionary<string,JsonElement> code,string preset,string id,string skeleton,out MotionGraph? graph,out string source,out string error)
    {
        graph=null;source="";error="";
        MotionRegistered first;
        try
        {
            var registered=Registered(code,preset);
            if(registered.Count==0){error="没有可用的原版动作清单："+preset;return false;}
            first=registered[0];
        }
        catch(Exception exception){error="读取原版动作清单失败："+exception.GetBaseException().Message;return false;}
        string path="external/Chara/Motion/"+first.Resource+".txt";
        if(!code.TryGetValue(path,out var ast)){error="找不到原版动作："+path;return false;}
        try{graph=FromAst(ast,id,skeleton);}
        catch(Exception exception){error="导入原版动作 "+first.Name+" 失败："+exception.GetBaseException().Message;return false;}
        source=first.Name+" → "+first.Resource;
        return true;
    }

    public static MotionGraph FromAst(JsonElement ast,string id,string skeleton,double sourceHz=30)
    {
        if(!double.IsFinite(sourceHz)||sourceHz<=0)throw new InvalidDataException("Invalid source clock.");
        var scalar=new Dictionary<string,double>(StringComparer.Ordinal);
        var values=new Dictionary<(int Part,string Field,int Index),double>();
        int part=-1;
        double Number(JsonElement n)
        {
            double value=n[0].GetString() switch
            {
                "num"=>n[1].GetDouble(),
                "id"=>scalar!.TryGetValue(n[1].GetString()!,out var number)?number:throw new InvalidDataException("Undefined motion constant: "+n[1]),
                "unary"=>n[1].GetString() switch{"Subtract"=>-Number(n[2]),"Add"=>Number(n[2]),_=>throw new InvalidDataException("Unsupported motion unary: "+n[1])},
                "binary"=>n[1].GetString() switch{"Add"=>Number(n[2])+Number(n[3]),"Subtract"=>Number(n[2])-Number(n[3]),"Multiply"=>Number(n[2])*Number(n[3]),"Divide"=>Number(n[2])/Number(n[3]),_=>throw new InvalidDataException("Unsupported motion expression: "+n[1])},
                "call" => n[1].GetString()! switch{"sin"=>Math.Sin(Number(n[2][0])),"degtorad"=>Number(n[2][0])*Math.PI/180,_=>throw new InvalidDataException("Unsupported motion call: "+n[1])},
                _=>throw new InvalidDataException("Unsupported motion expression: "+n[0])
            };
            if(!double.IsFinite(value))throw new InvalidDataException("Non finite motion value.");return value;
        }
        int Index(JsonElement n)
        {
            double v=Number(n);if(v<0||v>4096||v!=Math.Truncate(v))throw new InvalidDataException("Invalid motion index.");return (int)v;
        }
        void Walk(JsonElement n)
        {
            switch(n[0].GetString())
            {
                case "group":foreach(var child in n[1].EnumerateArray())Walk(child);break;
                case "with":
                    var target=n[1];
                    if(target[0].GetString()!="binary"||target[1].GetString()!="Index"||target[2][1].GetString()!="Process")throw new InvalidDataException("Unsupported motion part selection.");
                    int saved=part;part=Index(target[3][1][0]);Walk(n[2]);part=saved;break;
                case "binary":
                    if(n[1].GetString()!="Assign")throw new InvalidDataException("Unsupported motion assignment.");
                    var left=n[2];double v=Number(n[3]);
                    if(left[0].GetString()=="id")
                    {
                        string name=left[1].GetString()!;
                        if(name!="data_motLong"&&!name.StartsWith("tmp_",StringComparison.Ordinal))throw new InvalidDataException("Unsupported motion field: "+name);
                        scalar[name]=v;
                    }
                    else if(left[0].GetString()=="binary"&&left[1].GetString()=="Index")
                    {
                        string field=left[2][1].GetString()!;
                        if(!new[]{"data_motStep","data_motPosX","data_motPosY","data_motPartPosDir","data_motPartPosDis","data_motPartAngle","data_motPartPicture","data_motPartScaleX","data_motPartScaleY"}.Contains(field))throw new InvalidDataException("Unsupported motion field: "+field);
                        values[(part,field,Index(left[3][1][0]))]=v;
                    }
                    else throw new InvalidDataException("Unsupported motion assignment target.");
                    break;
                default:throw new InvalidDataException("Unsupported motion statement: "+n[0]);
            }
        }
        Walk(ast);
        double factor=60/sourceHz;
        var steps=values.Where(p=>p.Key.Part==-1&&p.Key.Field=="data_motStep").OrderBy(p=>p.Key.Index).ToArray();
        if(steps.Length==0)throw new InvalidDataException("Motion has no frame times.");
        if(steps.Any(s=>s.Value<0)||steps.Zip(steps.Skip(1)).Any(p=>p.First.Value>=p.Second.Value))throw new InvalidDataException("Motion frame times are not strictly increasing.");
        double Get(int bone,string field,int index,double fallback=0)=>values.GetValueOrDefault((bone,field,index),fallback);
        var clip=new MotionClip{Id="imported",Duration=Math.Max(1,Math.Max(scalar.GetValueOrDefault("data_motLong",steps[^1].Value),steps[^1].Value)*factor)};
        foreach(var step in steps)clip.Root.Add(new(){Frame=step.Value*factor,X=Get(-1,"data_motPosX",step.Key.Index),Y=Get(-1,"data_motPosY",step.Key.Index)});
        foreach(int bone in values.Keys.Where(k=>k.Part>=0).Select(k=>k.Part).Distinct().Order())
        {
            if(bone>=MotionSkeleton.Bones.Length)throw new InvalidDataException("Unsupported motion bone index: "+bone);
            var track=new MotionTrack{Bone=MotionSkeleton.Bones[bone]};
            foreach(var step in steps)
            {
                int i=step.Key.Index;double dir=Get(bone,"data_motPartPosDir",i)*Math.PI/180,dis=Get(bone,"data_motPartPosDis",i),picture=Get(bone,"data_motPartPicture",i);
                if(picture!=Math.Truncate(picture)||picture<0)throw new InvalidDataException("Invalid motion picture index.");
                track.Keys.Add(new(){Frame=step.Value*factor,X=Math.Cos(dir)*dis,Y=-Math.Sin(dir)*dis,Angle=Get(bone,"data_motPartAngle",i),ScaleX=Get(bone,"data_motPartScaleX",i,1),ScaleY=Get(bone,"data_motPartScaleY",i,1),Picture=(int)picture});
            }
            clip.Tracks.Add(track);
        }
        var graph=new MotionGraph{Id=id,Skeleton=skeleton,Clips=[clip],States=[new(){Clip=clip.Id}]};graph.Validate();return graph;
    }
}

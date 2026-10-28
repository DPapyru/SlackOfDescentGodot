using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private void ApplyGameplayFixes()
    {
        double achievement=0;
        foreach(var constant in Manifest.GetProperty("constants").EnumerateArray())
            if(constant.GetProperty("name").GetString()=="DATA_ACHIEVEMENT")double.TryParse(constant.GetProperty("value").GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out achievement);
        Globals.Write("gameData",achievement,0);
        void Patch(string key,Action<JsonArray> edit)
        {
            var tree=JsonNode.Parse(Code[key].GetRawText())!.AsArray();
            edit(tree);Code[key]=JsonSerializer.SerializeToElement(tree);
        }
        static IEnumerable<JsonArray> Nodes(JsonArray node)
        {
            yield return node;
            foreach(var child in node.OfType<JsonArray>())foreach(var descendant in Nodes(child))yield return descendant;
        }
        if(Environment.GetEnvironmentVariable("SLACK_MUD_EXPERIMENT")=="no-state")Patch("objects/objSwampSurface/8/0/0",tree=>
        {
            var assignments=Nodes(tree).Where(n=>n.Count==4&&n[0]?.ToString()=="binary"&&n[1]?.ToString()=="Assign"
                &&n[2]?[0]?.ToString()=="binary"&&n[2]?[1]?.ToString()=="Index"
                &&n[2]?[2]?[0]?.ToString()=="id"&&n[2]?[2]?[1]?.ToString() is "moss_m" or "moss_mt").ToArray();
            if(assignments.Length!=2)throw new InvalidOperationException("Expected two mud surface Draw state assignments");
            foreach(var assignment in assignments)
            {
                var value=assignment[3]!.DeepClone();
                assignment.Clear();foreach(var item in value.AsArray())assignment.Add(item?.DeepClone());
            }
        });
        Patch("external/Chara/SlimePlant/endstep.txt",tree=>
        {
            foreach(var statements in Nodes(tree).Where(n=>n.Count>0&&n[0] is JsonArray).ToArray())
                for(int i=statements.Count-1;i>=0;i--)
                    if(statements[i] is JsonArray label&&label[0]?.ToString()=="case"&&label[1]?[1]?.ToString()=="WARNING")statements.RemoveAt(i);
        });
        Patch("external/Chara/Haruki/step.txt",tree=>
        {
            foreach(var statements in Nodes(tree).Where(n=>n.Count>0&&n[0] is JsonArray).ToArray())
                for(int i=statements.Count-1;i>=0;i--)
                    if(statements[i] is JsonArray statement&&statement[0]?.ToString()=="switch"&&statement[2]?[1] is JsonArray body&&body.Count==2&&body[0]?[0]?.ToString()=="case"&&body[0]?[1]?[1]?.ToString()=="STAND"&&body[1]?[0]?.ToString()=="break")statements.RemoveAt(i);
        });
        Patch("objects/objJungle/0/0/0",tree=>
        {
            foreach(var pair in new Dictionary<string,int>{{"upper",-10},{"JUNGLE_BEGIN_X",-10000},{"JUNGLE_BEGIN_Y",-100},{"CAVE_BACK",0}})
                tree[1]!.AsArray().Insert(0,new JsonArray("if",new JsonArray("unary","Not",new JsonArray("call","variable_local_exists",new JsonArray(new JsonArray("str",pair.Key)))),new JsonArray("binary","Assign",new JsonArray("id",pair.Key),new JsonArray("num",pair.Value)),null));
        });
        Patch("scripts/LoadContext",tree=>
        {
            var statements=tree[1]!.AsArray();var original=statements[0]!.DeepClone();
            original![3]=JsonNode.Parse("[\"if\",[\"call\",\"instance_exists\",[[\"binary\",\"Deref\",[\"id\",\"global\"],[\"id\",\"PartCreator\"]]]],[\"binary\",\"Assign\",[\"id\",\"ctrlTarget\"],[\"binary\",\"Deref\",[\"id\",\"global\"],[\"id\",\"PartCreator\"]]],[\"binary\",\"Assign\",[\"id\",\"ctrlTarget\"],[\"id\",\"objPlayerDoll\"]]]");
            var select=JsonNode.Parse("[\"if\",[\"binary\",\"Or\",[\"binary\",\"Equal\",[\"id\",\"chara\"],[\"str\",\"Empty\"]],[\"binary\",\"Equal\",[\"id\",\"chara\"],[\"str\",\"Breasts\"]]],[\"binary\",\"Assign\",[\"id\",\"ctrlTarget\"],[\"binary\",\"Deref\",[\"id\",\"global\"],[\"id\",\"PartCreator\"]]],null]")!.AsArray();
            select[3]=original;statements[0]=select;
        });
        Patch("scripts/LoadBody_Top_Under0",tree=>
        {
            var setting=tree[1]!.AsArray().OfType<JsonArray>().First(n=>n[0]?.ToString()=="if");
            foreach(var node in Nodes(setting).Where(n=>n.Count==4&&n[0]?.ToString()=="binary"&&n[1]?.ToString()=="Deref"&&n[2]?[1]?.ToString()=="other"&&n[3]?[1]?.ToString()=="chara").ToArray())
            {node.Clear();node.Add("id");node.Add("chara");}
        });
        var demoMotions=new HashSet<string>(StringComparer.Ordinal);
        foreach(var key in new[]{"external/Chara/Nana/motion_list.txt","external/Chara/Ryoko/motion_list.txt","external/Chara/Yuki/motion_list.txt"})
            foreach(var call in Nodes(JsonNode.Parse(Code[key].GetRawText())!.AsArray()).Where(n=>n.Count==3&&n[0]?.ToString()=="call"&&n[1]?.ToString()=="PlayerMotionLoad"))
                demoMotions.Add(call[2]![1]![1]!.ToString());
        Patch("objects/objDemoDoll/7/10/0",tree=>
        {
            var statements=tree[1]!.AsArray();
            statements.Insert(0,new JsonArray("call","PlayerAnimationInit",new JsonArray()));
            foreach(string motion in demoMotions)statements.Insert(1,new JsonArray("binary","Assign",new JsonArray("id",motion),new JsonArray("id","null")));
            foreach(var (field,fallback,value) in new[]{("PartCreator","-4","[\"id\",\"id\"]"),("LoadOnPlayerDoll","1","[\"num\",0]"),("ForceLoadOnDoll","-1","[\"id\",\"id\"]")})
            {
                string saved="slackDemo"+field;
                statements.Insert(0,JsonNode.Parse($"[\"binary\",\"Assign\",[\"binary\",\"Deref\",[\"id\",\"global\"],[\"id\",\"{field}\"]],{value}]"));
                statements.Insert(0,JsonNode.Parse($"[\"if\",[\"call\",\"variable_global_exists\",[[\"str\",\"{field}\"]]],[\"binary\",\"Assign\",[\"id\",\"{saved}\"],[\"binary\",\"Deref\",[\"id\",\"global\"],[\"id\",\"{field}\"]]],[\"binary\",\"Assign\",[\"id\",\"{saved}\"],[\"num\",{fallback}]]]"));
                statements.Add(JsonNode.Parse($"[\"binary\",\"Assign\",[\"binary\",\"Deref\",[\"id\",\"global\"],[\"id\",\"{field}\"]],[\"id\",\"{saved}\"]]"));
            }
        });
        foreach(string key in Code.Keys.Where(k=>k.StartsWith("scripts/Load",StringComparison.Ordinal)).ToArray())
            Patch(key,tree=>
            {
                foreach(var statements in Nodes(tree).Where(n=>n.Count>0&&n[0] is JsonArray).ToArray())
                    for(int i=0;i<statements.Count;i++)
                        if(statements[i] is JsonArray assignment&&assignment.Count==4&&assignment[0]?.ToString()=="binary"&&assignment[1]?.ToString()=="Assign"&&assignment[2]?[1]?.ToString()=="withObject"&&assignment[3]?[0]?.ToString()=="id"&&assignment[3]?[1]?.ToString() is string field&&field.StartsWith("NEW_",StringComparison.Ordinal))
                            statements[i]=new JsonArray("if",new JsonArray("call","variable_local_exists",new JsonArray(new JsonArray("str",field))),assignment.DeepClone(),new JsonArray("exit"));
            });
        Patch("objects/objTitle/3/0/1",tree=>
        {
            foreach(var statements in Nodes(tree).Where(n=>n.Count>0&&n[0] is JsonArray).ToArray())
            {
                for(int i=0;i<statements.Count;i++)
                {
                    if(statements[i] is not JsonArray label||label[0]?.ToString()!="case"||label[1]?[1]?.ToString()!="NEWPLUSGAME")continue;
                    do{statements.RemoveAt(i);}while(i<statements.Count&&statements[i]?[0]?.ToString() is not ("case" or "default"));
                }
            }
        });
        Patch("objects/objController/3/0/29",tree=>
        {
            foreach(var assignment in Nodes(tree).Where(n=>n.Count==4&&n[0]?.ToString()=="binary"&&n[1]?.ToString()=="Assign"
                &&n[2]?[0]?.ToString()=="binary"&&n[2]?[1]?.ToString()=="Index"&&n[2]?[2]?[1]?.ToString()=="alarm"
                &&n[3]?[0]?.ToString()=="num"&&n[3]?[1]?.GetValue<double>()==7).ToArray())
                assignment[3]=JsonNode.Parse("[\"binary\",\"Divide\",[\"binary\",\"Multiply\",[\"num\",7],[\"call\",\"max\",[[\"num\",30],[\"call\",\"min\",[[\"id\",\"room_speed\"],[\"num\",60]]]]]],[\"num\",30]]");
        });
        Patch("objects/objController/3/2/6",tree=>
        {
            var statements=Nodes(tree).Single(n=>n.Count>0&&n[0] is JsonArray&&n.OfType<JsonArray>().Any(s=>s[0]?.ToString()=="case"&&s[1]?[1]?.ToString()=="WP_NANA_AR")&&n.OfType<JsonArray>().Any(s=>s[0]?.ToString()=="binary"&&s[2]?[1]?.ToString()=="ShotCheck"));
            int start=statements.Select((n,i)=>(n,i)).Single(p=>p.n?[0]?.ToString()=="case"&&p.n?[1]?[1]?.ToString()=="WP_NANA_AR").i;
            int end=start+1;while(statements[end]?[0]?.ToString()!="break")end++;
            statements.Insert(end,JsonNode.Parse("[\"if\",[\"call\",\"slack_secondary_fire\",[]],[\"binary\",\"Assign\",[\"id\",\"ShotCheck\"],[\"num\",2]],null]"));
        });
        ApplyModMenu();
    }
}

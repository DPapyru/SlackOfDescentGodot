using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private readonly Dictionary<(int Object,int Kind,int Sub),List<string>> addedEvents=new();
    private readonly HashSet<(int Object,int Kind,int Sub)> clearedEvents=new();
    private Dictionary<string,JsonElement>? media;
    private Dictionary<string,JsonElement> Media=>media??=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(Root,"media.json")))!.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);

    public int RegisterSprite(JsonElement definition)
    {
        int id=Sprites.Keys.DefaultIfEmpty(-1).Max()+1;
        Sprites[id]=definition;
        return id;
    }

    private void SetObject(int id,string property,GmValue value)
    {
        var node=JsonNode.Parse(Objects[id].GetRawText())!;
        node[property]=property is "solid" or "visible" or "persistent"?JsonValue.Create(value.Truth):JsonValue.Create(value.Int);
        Objects[id]=JsonSerializer.SerializeToElement(node);
        if(property=="parent"){parents[id]=value.Int;InvalidateObjects();}
    }

    private GmValue? Assets(string name,GmValue[] args,GmContext context)
    {
        int I(int n)=>args[n].Int;
        switch(name)
        {
            case "object_add":
            {
                int id=Objects.Keys.DefaultIfEmpty(-1).Max()+1;
                Objects[id]=JsonSerializer.SerializeToElement(new{id,name="object"+id,sprite=-1,mask=-1,parent=-1,depth=0,solid=false,visible=true,persistent=false,events=Array.Empty<object>()});
                parents[id]=-1;
                InvalidateObjects();
                return id;
            }
            case "object_set_sprite":case "object_set_mask":case "object_set_parent":case "object_set_depth":case "object_set_solid":case "object_set_visible":case "object_set_persistent":SetObject(I(0),name[11..],args[1]);return 0;
            case "object_delete":Objects.Remove(I(0));InvalidateObjects();foreach(var key in addedEvents.Keys.Where(k=>k.Object==I(0)).ToArray())addedEvents.Remove(key);return 0;
            case "object_event_add":
            {
                var key=(I(0),I(1),I(2));
                if(!addedEvents.TryGetValue(key,out var events))addedEvents[key]=events=new();
                events.Add(Compile(args[3].ToString()));InvalidateEvents();return 0;
            }
            case "object_event_clear":addedEvents[(I(0),I(1),I(2))]=new();clearedEvents.Add((I(0),I(1),I(2)));InvalidateEvents();return 0;
            case "sprite_add":
            {
                var path=args[0].ToString().Replace('\\','/');
                if(!Media.TryGetValue(path,out var definition))throw new FileNotFoundException("Sprite media missing: "+path,path);
                var node=JsonNode.Parse(definition.GetRawText())!;
                node["originX"]=I(4);node["originY"]=I(5);
                int id=RegisterSprite(JsonSerializer.SerializeToElement(node));
                var result=Platform?.Invoke("slack_prepare_sprite",[id,args[2],args[3]],context);
                if(result is null)throw new InvalidOperationException("Sprite preparation requires rendering platform");
                return id;
            }
            case "sprite_delete":Sprites.Remove(I(0));InvalidateCollisionSprites();return 0;
            case "sprite_duplicate":return RegisterSprite(Sprites[I(0)]);
            case "sprite_set_offset":
            {
                var node=JsonNode.Parse(Sprites[I(0)].GetRawText())!;node["originX"]=I(1);node["originY"]=I(2);Sprites[I(0)]=JsonSerializer.SerializeToElement(node);InvalidateCollisionSprites();return 0;
            }
            case "sprite_collision_mask":
            {
                if(I(7)!=1)throw new NotSupportedException("Nonrectangular dynamic collision mask");
                var node=JsonNode.Parse(Sprites[I(0)].GetRawText())!;
                int w=node["frames"]![0]!["width"]!.GetValue<int>(),h=node["frames"]![0]!["height"]!.GetValue<int>();
                int l=I(2)==2?I(3):0,t=I(2)==2?I(5):0,r=I(2)==2?I(4):w-1,b=I(2)==2?I(6):h-1;
                var pixels=new char[w*h];Array.Fill(pixels,'0');
                for(int y=Math.Max(0,t);y<=Math.Min(h-1,b);y++)for(int x=Math.Max(0,l);x<=Math.Min(w-1,r);x++)pixels[y*w+x]='1';
                node["masks"]=JsonSerializer.SerializeToNode(new[]{new{width=w,height=h,left=l,right=r,top=t,bottom=b,data=new string(pixels)}});
                Sprites[I(0)]=JsonSerializer.SerializeToElement(node);InvalidateCollisionSprites();return 0;
            }
        }
        return null;
    }
}

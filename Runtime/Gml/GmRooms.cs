using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private sealed class RoomOverrides
    {
        public int? Width;
        public int? Height;
        public string? Caption;
        public readonly Dictionary<int, ViewOverride> Views = new();

        public ViewOverride View(int view) =>
            Views.TryGetValue(view, out var found) ? found : Views[view] = new();
    }

    private sealed class ViewOverride
    {
        public bool? Visible;
        public readonly int?[] Fields = new int?[RoomViewFields.Length];
    }

    private readonly Dictionary<int, RoomOverrides> roomOverrides = new();

    private RoomOverrides Overrides(int id) =>
        roomOverrides.TryGetValue(id, out var found)
            ? found
            : roomOverrides[id] = new RoomOverrides();

    private static readonly string[] EngineViewNames =
    [
        "xview",
        "yview",
        "wview",
        "hview",
        "xport",
        "yport",
        "wport",
        "hport",
        "hborder",
        "vborder",
        "hspeed",
        "vspeed",
        "object",
    ];

    private static readonly string[] RoomViewFields =
    [
        "x",
        "y",
        "width",
        "height",
        "portX",
        "portY",
        "portWidth",
        "portHeight",
        "hborder",
        "vborder",
        "hspeed",
        "vspeed",
        "target",
    ];

    public readonly List<JsonElement> Tiles=new();
    public readonly HashSet<int> HiddenTileLayers=new();
    private int nextTile;
    public int TilesVersion;

    private void InitializeBackgrounds(JsonElement room)
    {
        int index=0;
        foreach(var background in room.GetProperty("backgrounds").EnumerateArray())
        {
            int id=background.GetProperty("background").GetInt32();
            var asset=Manifest.GetProperty("backgrounds").EnumerateArray().FirstOrDefault(b=>b.GetProperty("id").GetInt32()==id);
            double width=asset.ValueKind==JsonValueKind.Undefined?0:asset.GetProperty("width").GetDouble(),height=asset.ValueKind==JsonValueKind.Undefined?0:asset.GetProperty("height").GetDouble();
            foreach(var pair in new Dictionary<string,GmValue>
            {
                ["visible"]=background.GetProperty("visible").GetBoolean(),["foreground"]=background.GetProperty("foreground").GetBoolean(),["index"]=id,
                ["x"]=background.GetProperty("x").GetDouble(),["y"]=background.GetProperty("y").GetDouble(),["width"]=width,["height"]=height,
                ["htiled"]=background.GetProperty("tileX").GetBoolean(),["vtiled"]=background.GetProperty("tileY").GetBoolean(),
                ["hspeed"]=background.GetProperty("hspeed").GetDouble(),["vspeed"]=background.GetProperty("vspeed").GetDouble(),
                ["xscale"]=background.GetProperty("stretch").GetBoolean()&&width>0?room.GetProperty("width").GetDouble()/width:1,
                ["yscale"]=background.GetProperty("stretch").GetBoolean()&&height>0?room.GetProperty("height").GetDouble()/height:1,["blend"]=16777215,["alpha"]=1
            })Engine.Write("background_"+pair.Key,pair.Value,[index]);
            index++;
        }
        Engine.Write("background_showcolor",room.GetProperty("clearScreen").GetBoolean());
    }

    private void UpdateBackgrounds()
    {
        for(int index=0;index<8;index++)
        {
            Engine.Write("background_x",Engine.Read("background_x",[index]).Number+Engine.Read("background_hspeed",[index]).Number,[index]);
            Engine.Write("background_y",Engine.Read("background_y",[index]).Number+Engine.Read("background_vspeed",[index]).Number,[index]);
        }
    }

    private GmValue? RoomAssets(string name,GmValue[] args)
    {
        int I(int n)=>args[n].Int;
        switch(name)
        {
            case "room_instance_add":
            {
                int room=I(0);var node=JsonNode.Parse(Rooms[room].GetRawText())!;
                int id=NextId++;var code="room_added/"+id;Code[code]=JsonSerializer.SerializeToElement(new object[]{"group",Array.Empty<object>()});
                node["instances"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{id,@object=I(3),x=args[1].Number,y=args[2].Number,xscale=1,yscale=1,angle=0,blend=16777215,code}));
                Rooms[room]=JsonSerializer.SerializeToElement(node);return id;
            }
            case "room_instance_clear":
            {
                int room=I(0);var node=JsonNode.Parse(Rooms[room].GetRawText())!;node["instances"]=new JsonArray();Rooms[room]=JsonSerializer.SerializeToElement(node);return 0;
            }
            case "tile_add":
            {
                int id=++nextTile;
                TilesVersion++;
                Tiles.Add(JsonSerializer.SerializeToElement(new{id,background=I(0),sourceX=I(1),sourceY=I(2),width=I(3),height=I(4),x=args[5].Number,y=args[6].Number,depth=I(7),xscale=1,yscale=1,blend=16777215}));return id;
            }
            case "tile_layer_hide":HiddenTileLayers.Add(I(0));TilesVersion++;return 0;
            case "tile_layer_show":HiddenTileLayers.Remove(I(0));TilesVersion++;return 0;
            case "tile_layer_delete":Tiles.RemoveAll(t=>t.GetProperty("depth").GetInt32()==I(0));TilesVersion++;return 0;
            case "tile_layer_shift":
                TilesVersion++;
                for(int i=0;i<Tiles.Count;i++)if(Tiles[i].GetProperty("depth").GetInt32()==I(0))
                {var node=JsonNode.Parse(Tiles[i].GetRawText())!;node["x"]=Tiles[i].GetProperty("x").GetDouble()+args[1].Number;node["y"]=Tiles[i].GetProperty("y").GetDouble()+args[2].Number;Tiles[i]=JsonSerializer.SerializeToElement(node);}return 0;
        }
        return null;
    }
}

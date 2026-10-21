using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    public readonly List<JsonElement> Tiles=new();
    public readonly HashSet<int> HiddenTileLayers=new();
    private int nextTile;

    private GmValue? RoomAssets(string name,GmValue[] args)
    {
        int I(int n)=>args[n].Int;
        switch(name)
        {
            case "room_instance_add":
            {
                var node=JsonNode.Parse(Rooms[I(0)].GetRawText())!;
                int id=NextId++;var code="room_added/"+id;Code[code]=JsonSerializer.SerializeToElement(new object[]{"group",Array.Empty<object>()});
                node["instances"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{id,@object=I(3),x=args[1].Number,y=args[2].Number,xscale=1,yscale=1,angle=0,blend=16777215,code}));
                Rooms[I(0)]=JsonSerializer.SerializeToElement(node);return id;
            }
            case "room_instance_clear":
            {
                var node=JsonNode.Parse(Rooms[I(0)].GetRawText())!;node["instances"]=new JsonArray();Rooms[I(0)]=JsonSerializer.SerializeToElement(node);return 0;
            }
            case "tile_add":
            {
                int id=++nextTile;
                Tiles.Add(JsonSerializer.SerializeToElement(new{id,background=I(0),sourceX=I(1),sourceY=I(2),width=I(3),height=I(4),x=args[5].Number,y=args[6].Number,depth=I(7),xscale=1,yscale=1,blend=16777215}));return id;
            }
            case "tile_layer_hide":HiddenTileLayers.Add(I(0));return 0;
            case "tile_layer_show":HiddenTileLayers.Remove(I(0));return 0;
            case "tile_layer_delete":Tiles.RemoveAll(t=>t.GetProperty("depth").GetInt32()==I(0));return 0;
            case "tile_layer_shift":
                for(int i=0;i<Tiles.Count;i++)if(Tiles[i].GetProperty("depth").GetInt32()==I(0))
                {var node=JsonNode.Parse(Tiles[i].GetRawText())!;node["x"]=Tiles[i].GetProperty("x").GetDouble()+args[1].Number;node["y"]=Tiles[i].GetProperty("y").GetDouble()+args[2].Number;Tiles[i]=JsonSerializer.SerializeToElement(node);}return 0;
        }
        return null;
    }
}

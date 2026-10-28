using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    internal NativeModServices? NativeServices;
    internal GmInstance CreateNativeEntity(double x, double y)
    {
        const int kind = -100001;
        if (!Objects.ContainsKey(kind))
        {
            var definition = JsonNode.Parse(Objects[Constants["objLine"].Int].GetRawText())!.AsObject();
            definition["id"] = kind; definition["name"] = "native.entity"; definition["events"] = new JsonArray();
            definition["sprite"] = -1; definition["mask"] = -1; definition["parent"] = -100;
            Objects[kind] = JsonSerializer.SerializeToElement(definition); parents[kind] = -100; InvalidateObjects();
        }
        return Create(kind, x, y, run: false);
    }
}

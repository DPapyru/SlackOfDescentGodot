using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private void ApplyMapMenu()
    {
        var step = JsonNode.Parse(Code["objects/objTitle/3/0/1"].GetRawText())!.AsArray();
        var choices = MenuSwitch(step, "Cursor", "TEST"); int start = MenuLabel(choices, "TEST") + 1;
        while (start < choices.Count && choices[start]?[0]?.ToString() != "case") choices.RemoveAt(start);
        choices.Insert(start++, new JsonArray("call", "slack_map_select", new JsonArray())); choices.Insert(start, new JsonArray("break"));
        Code["objects/objTitle/3/0/1"] = JsonSerializer.SerializeToElement(step);
        var draw = JsonNode.Parse(Code["objects/objTitle/8/0/0"].GetRawText())!.AsArray();
        foreach (var node in MenuNodes(draw))
            if (node.Count == 2 && node[0]?.ToString() == "str" && node[1]?.ToString() is "Test Room" or "TEST ROOM") node[1] = "选择地图";
        Code["objects/objTitle/8/0/0"] = JsonSerializer.SerializeToElement(draw);
    }
}

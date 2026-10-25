using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private static IEnumerable<JsonArray> MenuNodes(JsonArray node)
    {
        yield return node;
        foreach(var child in node.OfType<JsonArray>())foreach(var descendant in MenuNodes(child))yield return descendant;
    }
    private static JsonArray MenuSwitch(JsonArray tree,string variable,string label)
    {
        foreach(var node in MenuNodes(tree))
            if(node.Count>0&&node[0]?.ToString()=="switch"&&node[1]?[1]?.ToString()==variable&&node[2]?[1] is JsonArray body)
                foreach(var statement in body.OfType<JsonArray>())
                    if(statement[0]?.ToString()=="case"&&statement[1]?[1]?.ToString()==label)return body;
        throw new InvalidOperationException("Missing menu branch: "+label);
    }
    private static int MenuLabel(JsonArray body,string label)
    {
        for(int i=0;i<body.Count;i++)if(body[i]?[0]?.ToString()=="case"&&body[i]?[1]?[1]?.ToString()==label)return i;
        throw new InvalidOperationException("Missing menu label: "+label);
    }
    private static JsonArray MenuBranch(JsonArray body,string label)
    {
        var branch=new JsonArray();
        for(int i=MenuLabel(body,label)+1;i<body.Count&&body[i]?[0]?.ToString()!="case";i++)branch.Add(body[i]!.DeepClone());
        return branch;
    }
    private static void MenuAppend(JsonArray target,JsonArray statements)
    {
        foreach(var statement in statements)target.Add(statement!.DeepClone());
    }
    private static JsonArray MenuText(JsonArray template,string text,int offset)
    {
        var call=template.DeepClone().AsArray();
        call[2]![1]=JsonNode.Parse($"[\"binary\",\"Add\",[\"id\",\"menu_y\"],[\"num\",{offset}]]");
        call[2]![2]=new JsonArray("str",text);return call;
    }
    private void ApplyModMenu()
    {
        var create=JsonNode.Parse(Code["objects/objTitle/0/0/1"].GetRawText())!.AsArray();
        string[] options=["KEYCONFIG","STRIP","SURVIVALCONFIG","SOUNDVOLUME","VOICEVOLUME","MUSICVOLUME","EFFECT","MOTIONBLUR","ASPECTRATIO","TURNSIDE","TURNCURSOL","DROPWEAPON","COVER","BLOOD","SHAKE","WEATHER","ARROWCAMERA","SAVEANDBACK"];
        foreach(var node in MenuNodes(create))
            if(node.Count==4&&node[0]?.ToString()=="binary"&&node[1]?.ToString()=="Assign"&&node[2]?[0]?.ToString()=="id")
            {
                int index=Array.IndexOf(options,node[2]![1]!.ToString());
                if(index>=0)node[3]=new JsonArray("num",index+1);
            }
        string[] names=["MOD","EXIT","VIEW_MOD","MOD_BACK","LANGUAGE","MOD_MAP","MOD_CHARACTER","MOD_CSHARP"];
        int[] values=[2,3,9,3,0,0,1,2];
        for(int i=0;i<names.Length;i++)create[1]!.AsArray().Add(new JsonArray("binary","Assign",new JsonArray("id",names[i]),new JsonArray("num",values[i])));
        Code["objects/objTitle/0/0/1"]=JsonSerializer.SerializeToElement(create);

        var step=JsonNode.Parse(Code["objects/objTitle/3/0/1"].GetRawText())!.AsArray();
        var titleChoices=MenuSwitch(step,"Cursor","EXIT");
        int exit=MenuLabel(titleChoices,"EXIT");
        var modChoice=JsonNode.Parse("[[\"case\",[\"id\",\"MOD\"]],[\"call\",\"sound_play\",[[\"id\",\"sndDecide\"]]],[\"binary\",\"Assign\",[\"id\",\"View\"],[\"id\",\"VIEW_MOD\"]],[\"binary\",\"Assign\",[\"id\",\"Cursor\"],[\"num\",0]],[\"break\"]]")!.AsArray();
        foreach(var statement in modChoice)titleChoices.Insert(exit++,statement!.DeepClone());
        var views=MenuSwitch(step,"View","VIEW_TITLE");
        int settings=MenuLabel(views,"VIEW_OPTION")+1;
        views[settings]![1]![3]=new JsonArray("id","LANGUAGE");
        views[settings+1]![2]![3]=new JsonArray("id","LANGUAGE");
        MenuAppend(views,JsonNode.Parse("[[\"case\",[\"id\",\"VIEW_MOD\"]],[\"if\",[\"binary\",\"LessThan\",[\"id\",\"Cursor\"],[\"num\",0]],[\"binary\",\"Assign\",[\"id\",\"Cursor\"],[\"id\",\"MOD_BACK\"]],null],[\"if\",[\"binary\",\"GreaterThan\",[\"id\",\"Cursor\"],[\"id\",\"MOD_BACK\"]],[\"binary\",\"Assign\",[\"id\",\"Cursor\"],[\"num\",0]],null],[\"if\",[\"binary\",\"Or\",[\"id\",\"Decide\"],[\"id\",\"Cancel\"]],[\"group\",[[\"call\",\"event_user\",[[\"num\",15]]],[\"if\",[\"binary\",\"Or\",[\"id\",\"Cancel\"],[\"binary\",\"Equal\",[\"id\",\"Cursor\"],[\"id\",\"MOD_BACK\"]]],[\"group\",[[\"call\",\"sound_play\",[[\"id\",\"sndCancel\"]]],[\"binary\",\"Assign\",[\"id\",\"View\"],[\"id\",\"VIEW_TITLE\"]],[\"binary\",\"Assign\",[\"id\",\"Cursor\"],[\"id\",\"MOD\"]]]],[\"group\",[[\"call\",\"sound_play\",[[\"id\",\"sndDecide\"]]],[\"call\",\"slack_mod_menu\",[[\"id\",\"Cursor\"]]]]]]]],null],[\"break\"]]")!.AsArray());
        foreach(var node in MenuNodes(step))
            if(node.Count==4&&node[0]?.ToString()=="binary"&&node[2]?[0]?.ToString()=="id"&&node[2]?[1]?.ToString()=="Cursor"&&node[3]?[0]?.ToString()=="id"&&node[3]?[1]?.ToString()=="BLOOD")node[3]=new JsonArray("id","COVER");
        var optionChoices=MenuSwitch(step,"Cursor","SOUNDVOLUME");
        MenuAppend(optionChoices,JsonNode.Parse("[[\"case\",[\"id\",\"LANGUAGE\"]],[\"if\",[\"binary\",\"Or\",[\"binary\",\"Or\",[\"id\",\"LEFT\"],[\"id\",\"RIGHT\"]],[\"id\",\"Decide\"]],[\"group\",[[\"call\",\"sound_play\",[[\"id\",\"sndSelect\"]]],[\"call\",\"slack_language\",[[\"num\",1]]]]],null],[\"break\"]]")!.AsArray());
        Code["objects/objTitle/3/0/1"]=JsonSerializer.SerializeToElement(step);

        var draw=JsonNode.Parse(Code["objects/objTitle/8/0/0"].GetRawText())!.AsArray();
        foreach(var node in draw[1]!.AsArray().OfType<JsonArray>())
        {
            if(node.Count==4&&node[0]?.ToString()=="binary"&&node[2]?[1]?.ToString()=="menu_y")node[3]=new JsonArray("num",312);
            if(node[0]?.ToString()!="call"||node[1]?.ToString()!="draw_sprite"||node[2]?[0]?[1]?.ToString()!="sprTitleSurvival")continue;
            node[1]="draw_sprite_ext";var args=node[2]!.AsArray();
            args[2]=JsonNode.Parse("[\"binary\",\"Add\",[\"id\",\"view_xview\"],[\"binary\",\"Multiply\",[\"binary\",\"Subtract\",[\"id\",\"view_wview\"],[\"num\",360.75]],[\"num\",0.5]]]");
            args.Add(new JsonArray("num",0.75));args.Add(new JsonArray("num",0.75));args.Add(new JsonArray("num",0));args.Add(new JsonArray("id","c_white"));args.Add(new JsonArray("num",1));
        }
        var drawViews=MenuSwitch(draw,"View","VIEW_TITLE");
        int title=MenuLabel(drawViews,"VIEW_TITLE");
        var shadow=drawViews[title+3]!.AsArray();var text=drawViews[title+4]!.AsArray();
        drawViews[title+7]=MenuText(shadow,"Exit",99);drawViews[title+8]=MenuText(text,"Exit",96);
        drawViews.Insert(title+7,MenuText(shadow,"Mod",67));drawViews.Insert(title+8,MenuText(text,"Mod",64));
        var modDraw=MenuBranch(drawViews,"VIEW_GAMESTART_CC");
        modDraw[0]![3]=new JsonArray("num",160);
        string[] labels=["地图编辑器","角色编辑器","CSharp 模组"];
        int line=0;
        foreach(var statement in modDraw.OfType<JsonArray>())
            if(statement[0]?.ToString()=="call"&&statement[1]?.ToString()=="draw_text_transformed_color")statement[2]![2]=new JsonArray("str",labels[line++/2]);
        modDraw.RemoveAt(modDraw.Count-1);
        modDraw.Add(MenuText(shadow,"Back",99));modDraw.Add(MenuText(text,"Back",96));modDraw.Add(new JsonArray("break"));
        drawViews.Add(new JsonArray("case",new JsonArray("id","VIEW_MOD")));MenuAppend(drawViews,modDraw);
        JsonArray? firstPage=null,secondPage=null;
        foreach(var node in MenuNodes(draw))
        {
            if(node.Count==0||node[0]?.ToString()!="if"||node[1] is not JsonArray condition||condition.Count!=4||condition[0]?.ToString()!="binary"||condition[2]?[1]?.ToString()!="OptionsPage"||condition[3]?[0]?.ToString()!="num")continue;
            double number=condition[3]![1]!.GetValue<double>();
            if(number==0)firstPage=node[2]![1]!.AsArray();
            if(number==1)secondPage=node[2]![1]!.AsArray();
        }
        if(firstPage is null||secondPage is null)throw new InvalidOperationException("Missing settings drawing pages");
        int blood=firstPage.Count-2;
        while(firstPage[blood]?[0]?.ToString()!="binary")blood--;
        var bloodRow=new JsonArray();
        for(int i=blood+1;i<firstPage.Count-1;i++)bloodRow.Add(firstPage[i]!.DeepClone());
        while(firstPage.Count>blood+1)firstPage.RemoveAt(blood);
        for(int i=0;i<bloodRow.Count;i++)secondPage.Insert(i+1,bloodRow[i]!.DeepClone());
        secondPage.Insert(bloodRow.Count+1,JsonNode.Parse("[\"binary\",\"Assign\",[\"id\",\"option_y\"],[\"binary\",\"Add\",[\"id\",\"option_y\"],[\"num\",32]]]")!);
        int firstLabel=0;
        while(firstPage[firstLabel]?[1]?.ToString()!="draw_text_transformed_color")firstLabel++;
        var languageShadow=firstPage[firstLabel]!.DeepClone();languageShadow![2]![2]=new JsonArray("str","语言");
        var languageText=firstPage[firstLabel+1]!.DeepClone();languageText![2]![2]=new JsonArray("str","语言");
        var valueShadow=languageShadow.DeepClone();valueShadow[2]![0]=JsonNode.Parse("[\"binary\",\"Add\",[\"id\",\"menu_x\"],[\"num\",253]]");valueShadow[2]![2]=JsonNode.Parse("[\"call\",\"slack_language\",[]]");
        var valueText=languageText.DeepClone();valueText[2]![0]=JsonNode.Parse("[\"binary\",\"Add\",[\"id\",\"menu_x\"],[\"num\",250]]");valueText[2]![2]=JsonNode.Parse("[\"call\",\"slack_language\",[]]");
        firstPage.Insert(firstLabel++,languageShadow);firstPage.Insert(firstLabel++,languageText);firstPage.Insert(firstLabel++,valueShadow);firstPage.Insert(firstLabel++,valueText);
        firstPage.Insert(firstLabel,JsonNode.Parse("[\"binary\",\"Assign\",[\"id\",\"option_y\"],[\"binary\",\"Add\",[\"id\",\"option_y\"],[\"num\",32]]]")!);
        Code["objects/objTitle/8/0/0"]=JsonSerializer.SerializeToElement(draw);
    }
}

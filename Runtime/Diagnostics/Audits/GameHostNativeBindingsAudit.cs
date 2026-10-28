using Godot;

namespace SlackOfDescent;

public partial class GameHost
{
    private async Task AuditNativeBindingsLocalization()
    {
        string originalLanguage=NativeLocalization.Language;
        bool hadMapInput=content.InputActions.ContainsKey("native.map.interact");
        string translated="audit.bindings.localized",fallback="audit.bindings.fallback",unnamed="audit.bindings.unnamed";
        var translationKeys=new List<string>();
        var bindingButton=nativeBindingsButton;
        var keys=content.InputActions.ToDictionary(pair=>pair.Key,pair=>nativeBridge.Services.Input.GetKey(pair.Key));
        try
        {
            using(var runtime=new NativeMapRuntime(world,content,nativeBridge.Services,[])) { }
            content.RegisterInput(new(){Id=translated,Name="Default action",Description="Default description",DefaultKey=76});
            content.RegisterInput(new(){Id=fallback,Name="Default action",Description="Default description",DefaultKey=70});
            content.RegisterInput(new(){Id=unnamed,DefaultKey=85});
            foreach(var entry in new[]{("zh_CN/"+translated+".name","审计操作"),("en/"+translated+".name","Audit action"),("zh_CN/"+translated+".description","审计说明"),("en/"+translated+".description","Audit description"),("en/"+fallback+".name","English fallback")})
            {content.Translations.Add(entry.Item1,entry.Item2);translationKeys.Add(entry.Item1);}
            foreach(string language in new[]{"zh_CN","en","zh_CN"})
            {
                NativeLocalization.SetLanguage(language,false);SyncNativeBindings();
                bool chinese=language=="zh_CN";
                RegressionExpect(nativeBindingsButton==bindingButton&&nativeBindingsButton!.Visible&&nativeBindingsButton.Text==(chinese?"Mod 按键":"Mod controls"),"reused binding entry follows "+language);
                nativeBindingsButton!.EmitSignal(BaseButton.SignalName.Pressed);
                var window=nativeBindingsWindow!;
                RegressionExpect(window.Title==(chinese?"Mod 按键":"Mod controls"),"reopened binding window follows "+language);
                Label Row(string id)
                {
                    var button=Flatten(window).OfType<Button>().Single(button=>button.GetMeta("action_id","").AsString()==id);
                    return button.GetParent().GetChildren().OfType<Label>().Single();
                }
                var localized=Row(translated);
                RegressionExpect(localized.Text==(chinese?"审计操作":"Audit action")&&localized.TooltipText==(chinese?"审计说明":"Audit description"),"named input translation and description follow "+language);
                RegressionExpect(Row("native.map.interact").Text==(chinese?"地图互动":"Map Interaction"),"real map input uses existing interface translation "+language);
                RegressionExpect(Row("testmod.reload").Text==(chinese?"狙击枪：换弹":"Sniper rifle: Reload")&&Row("testmod.reload").TooltipText==(chinese?"填满五发弹匣，需要 2.5 秒。":"Fill the five-round magazine in 2.5 seconds."),"official Mod input uses package-owned translations "+language);
                RegressionExpect(Row(fallback).Text=="English fallback"&&Row(fallback).TooltipText=="Default description"&&Row(unnamed).Text==unnamed,"input rows preserve English, default and stable ID fallbacks "+language);
                foreach(var pair in keys)RegressionExpect(nativeBridge.Services.Input.GetKey(pair.Key)==pair.Value,"language switch preserves binding "+pair.Key);
                window.EmitSignal(Window.SignalName.CloseRequested);
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                RegressionExpect(nativeBindingsWindow is null,"binding window closes after "+language);
            }
            GD.Print("NATIVE_BINDINGS_LOCALIZATION_OK switches=zh_CN,en,zh_CN map=PASS mod=PASS fallback=PASS stable_keys=PASS");
        }
        finally
        {
            nativeBindingsWindow?.EmitSignal(Window.SignalName.CloseRequested);
            foreach(string key in translationKeys)content.Translations.Remove(key);
            foreach(string id in new[]{translated,fallback,unnamed})content.InputActions.Remove(id);
            if(!hadMapInput)content.InputActions.Remove("native.map.interact");
            NativeLocalization.SetLanguage(originalLanguage,false);SyncNativeBindings();
        }
    }
}

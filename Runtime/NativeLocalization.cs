using Godot;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public static class NativeLocalization
{
    private static Dictionary<string, string> entries = new(StringComparer.Ordinal);
    private static Dictionary<string, string> english = new(StringComparer.Ordinal);
    private static KeyValuePair<string,string>[] fragments = [];
    private static KeyValuePair<string,string>[] englishPrefixes = [];
    private static readonly HashSet<string> names = new(StringComparer.Ordinal);
    private static readonly HashSet<string> localizedImages = new(StringComparer.Ordinal);
    private static readonly List<Translation> translations = [];
    private static string? settingsPath;
    public static string Language { get; private set; } = "zh_CN";
    public static bool IsChinese => Language == "zh_CN";
    public static FontFile? Font { get; private set; }
    public static FontFile? OriginalUiFont { get; private set; }
    public static void Initialize(string dataRoot,string? userRoot=null)
    {
        entries = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dataRoot,"localization","zh-CN.json")))!;
        english = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(dataRoot,"localization","en.json")))!;
        fragments = entries.Where(p => p.Key.Length > 2).OrderByDescending(p => p.Key.Length).ToArray();
        englishPrefixes = english.Where(p => p.Key.EndsWith('：')).OrderByDescending(p => p.Key.Length).ToArray();
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot,"manifest.json")));
        names.Clear();
        foreach(var room in manifest.RootElement.GetProperty("rooms").EnumerateArray())names.Add(room.GetProperty("name").GetString()!);
        foreach(string name in new[]{"Nana","Yuki","Ryoko","Ramda","Crackle Cradle","CrackleCradle","Slack of Descent","Slack Of Descent","Test Room","Playground Survival","Playground_Survival"})names.Add(name);
        localizedImages.Clear();
        string imageRoot=Path.Combine(dataRoot,"localization","sprites");
        foreach(string path in Directory.EnumerateFiles(imageRoot,"*.png"))localizedImages.Add("sprites/"+Path.GetFileName(path));
        localizedImages.Remove("sprites/326_0.png");localizedImages.Remove("sprites/327_0.png");
        Font = new FontFile { Data = File.ReadAllBytes(Path.Combine(dataRoot,"localization","NotoSansCJK-Regular.ttc")) };
        var original=manifest.RootElement.GetProperty("fonts").EnumerateArray().First(f=>f.GetProperty("id").GetInt32()==0);
        int size=original.GetProperty("size").GetInt32();var map=original.GetProperty("map");
        OriginalUiFont=new FontFile { FixedSize=size,Fallbacks=[Font] };
        int ascent=Enumerable.Range(32,96).Max(c=>map[c*6+3].GetInt32());
        OriginalUiFont.SetCacheAscent(0,size,ascent);OriginalUiFont.SetCacheDescent(0,size,0);
        using(var atlas=Image.LoadFromFile(Path.Combine(dataRoot,original.GetProperty("path").GetString()!)))OriginalUiFont.SetTextureImage(0,new(size,0),0,atlas);
        for(int c=32;c<128;c++)
        {
            int n=c*6;var cacheSize=new Vector2I(size,0);
            OriginalUiFont.SetGlyphAdvance(0,size,c,new(unchecked((int)map[n+4].GetUInt32()),0));
            OriginalUiFont.SetGlyphOffset(0,cacheSize,c,new(unchecked((int)map[n+5].GetUInt32()),-ascent));
            OriginalUiFont.SetGlyphSize(0,cacheSize,c,new(map[n+2].GetSingle(),map[n+3].GetSingle()));
            OriginalUiFont.SetGlyphUVRect(0,cacheSize,c,new(map[n].GetSingle(),map[n+1].GetSingle(),map[n+2].GetSingle(),map[n+3].GetSingle()));
            OriginalUiFont.SetGlyphTextureIdx(0,cacheSize,c,0);
        }
        foreach(var translation in translations)TranslationServer.RemoveTranslation(translation);
        translations.Clear();
        foreach(string locale in new[]{"zh_CN","en"}){var translation=new NativeUiTranslation { Locale=locale };translations.Add(translation);TranslationServer.AddTranslation(translation);}
        settingsPath=userRoot is null?null:Path.Combine(userRoot,"Cfg","Language.json");
        string selected="zh_CN";
        if(settingsPath is not null&&File.Exists(settingsPath))
            try{selected=NativeContentStore.Load<string>(settingsPath);}catch(Exception e)when(e is IOException or JsonException or InvalidDataException){}
        SetLanguage(selected,false);
    }
    public static void SetLanguage(string language,bool persist=true)
    {
        Language=language=="en"?"en":"zh_CN";
        ThemeDB.FallbackFont=IsChinese?Font:OriginalUiFont;
        TranslationServer.SetLocale(Language);
        if(persist&&settingsPath is not null)NativeContentStore.Save(settingsPath,Language);
    }
    public static string AssetPath(string path)=>IsChinese&&localizedImages.Contains(path)?"localization/"+path:path;
    internal static string InterfaceText(string source)=>(IsChinese?entries:english).GetValueOrDefault(source,source);
    public static string Text(string source)
    {
        if(names.Contains(source)||source.StartsWith("Stage",StringComparison.Ordinal)&&Regex.IsMatch(source,@"^Stage\d+[-_][^\r\n|#]+$"))return source;
        if(!IsChinese)
        {
            if(english.TryGetValue(source,out var value))return value;
            if(source.Contains('\n'))return string.Join('\n',source.Split('\n').Select(Text));
            foreach(var pair in englishPrefixes)if(source.StartsWith(pair.Key,StringComparison.Ordinal))return pair.Value+source[pair.Key.Length..];
            return source;
        }
        if (entries.TryGetValue(source, out var translated)) return translated;
        if (source.Contains('|')) return string.Join('|', source.Split('|').Select(Text));
        if (source.Contains('#') || source.Contains('\n')) return string.Join('\n',source.Replace('#','\n').Split('\n').Select(Text));
        foreach (var pair in fragments)
            if ((pair.Key.EndsWith(' ') || pair.Key.EndsWith('=') || pair.Key.StartsWith("No Exists:",StringComparison.Ordinal)) && source.StartsWith(pair.Key,StringComparison.Ordinal))
                return pair.Value + source[pair.Key.Length..];
        return source;
    }
}

internal partial class NativeUiTranslation : Translation
{
    public override StringName _GetMessage(StringName source,StringName context)
    {
        string translated=NativeLocalization.InterfaceText(source.ToString());
        return translated==source.ToString()?new StringName():new StringName(translated);
    }
}

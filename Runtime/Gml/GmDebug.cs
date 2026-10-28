using System.Text.Json;

namespace SlackOfDescent;

public static class GmDebug
{
    public static bool Audits=>Resolve("SLACK_AUDITS","audits",false);
    public static bool Overlay=>Audits||Resolve("SLACK_OVERLAY","overlay",true);
    public static bool Editors=>Resolve("SLACK_EDITORS","editors",Audits);
    public static bool Perf=>Resolve("SLACK_PERF","perf",false);
    public static string Root=>root??=Path.GetFullPath(System.Environment.GetEnvironmentVariable("SLACK_USER_ROOT")??Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),"SlackOfDescentGodot"));
    private static string? root;
    private static Dictionary<string,bool>? keys;
    private static bool Resolve(string variable,string key,bool fallback)
    {
        var text=System.Environment.GetEnvironmentVariable(variable)?.Trim().ToLowerInvariant();
        if(text is "1" or "true" or "yes" or "on")return true;
        if(text is "0" or "false" or "no" or "off")return false;
        return Keys().TryGetValue(key,out bool configured)?configured:fallback;
    }
    private static Dictionary<string,bool> Keys()
    {
        if(keys is not null)return keys;
        var loaded=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        keys=loaded;
        try
        {
            string path=Path.Combine(Root,"Cfg","Debug.json");
            if(!File.Exists(path))return loaded;
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            foreach(var property in document.RootElement.EnumerateObject())
                if(property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)loaded[property.Name]=property.Value.GetBoolean();
                else if(property.Value.ValueKind==JsonValueKind.Number)loaded[property.Name]=property.Value.GetDouble()!=0;
                else if(property.Value.ValueKind==JsonValueKind.String&&bool.TryParse(property.Value.GetString(),out bool parsed))loaded[property.Name]=parsed;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){Console.Error.WriteLine("DEBUG_CONFIG_FAILED "+e.Message);}
        return loaded;
    }
}

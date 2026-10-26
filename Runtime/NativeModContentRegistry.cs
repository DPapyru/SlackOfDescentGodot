namespace SlackOfDescent;

public sealed partial class ContentRegistry
{
    public Dictionary<string, string> Maps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Languages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CharacterDirectories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Translations { get; } = new(StringComparer.Ordinal);

    internal IReadOnlyList<(string Kind, string Id)> ContributionIds()
    {
        var ids = new List<(string Kind, string Id)>();
        ids.AddRange(Actions.Keys.Select(id => ("action", id)));
        ids.AddRange(Entities.Keys.Select(id => ("entity", id)));
        ids.AddRange(Characters.Keys.Select(id => ("character", id)));
        ids.AddRange(Resources.Keys.Select(id => ("resource", id)));
        ids.AddRange(Maps.Keys.Select(id => ("map", id)));
        ids.AddRange(Languages.Keys.Select(id => ("language", id)));
        ids.AddRange(CharacterDirectories.Keys.Select(id => ("character-directory", id)));
        ids.AddRange(Translations.Keys.Select(id => ("translation", id)));
        AppendSdkIds(ids);
        return ids;
    }
    internal void AddContributionsTo(ContentRegistry target)
    {
        foreach (var value in Actions) target.Actions.Add(value.Key, value.Value);
        foreach (var value in Entities) target.Entities.Add(value.Key, value.Value);
        foreach (var value in Characters) target.Characters.Add(value.Key, value.Value);
        foreach (var value in Resources) target.Resources.Add(value.Key, value.Value);
        foreach (var value in Maps) target.Maps.Add(value.Key, value.Value);
        foreach (var value in Languages) target.Languages.Add(value.Key, value.Value);
        foreach (var value in CharacterDirectories) target.CharacterDirectories.Add(value.Key, value.Value);
        foreach (var value in Translations) target.Translations.Add(value.Key, value.Value);
        CopySdkTo(target);
    }
    internal void RemoveContributionsFrom(ContentRegistry target)
    {
        foreach (string id in Actions.Keys) target.Actions.Remove(id);
        foreach (string id in Entities.Keys) target.Entities.Remove(id);
        foreach (string id in Characters.Keys) target.Characters.Remove(id);
        foreach (string id in Resources.Keys) target.Resources.Remove(id);
        foreach (string id in Maps.Keys) target.Maps.Remove(id);
        foreach (string id in Languages.Keys) target.Languages.Remove(id);
        foreach (string id in CharacterDirectories.Keys) target.CharacterDirectories.Remove(id);
        foreach (string id in Translations.Keys) target.Translations.Remove(id);
        RemoveSdkFrom(target);
    }
    internal void ClearContributions()
    {
        Actions.Clear(); Entities.Clear(); Characters.Clear(); Resources.Clear();
        Maps.Clear(); Languages.Clear(); CharacterDirectories.Clear(); Translations.Clear(); ClearSdk();
    }
    partial void AppendSdkIds(List<(string Kind, string Id)> ids);
    partial void CopySdkTo(ContentRegistry target);
    partial void RemoveSdkFrom(ContentRegistry target);
    partial void ClearSdk();
}

namespace SlackOfDescent;

public sealed class NativeAudioService(NativeModServices services) : IDisposable
{
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Resource), int> loaded = new();
    public void PlayBuiltin(ActorContext actor, string name)
    {
        if (!services.World.Constants.TryGetValue(name, out var sound)) throw new InvalidDataException("原版声音不存在：" + name);
        actor.Call("sound_play", sound);
    }
    public void Play(ActorContext actor, string resource, bool loop = false, double volume = 1)
    {
        if (!services.Registry.Resources.TryGetValue(resource, out string? path)) throw new InvalidDataException("音频资源未加载：" + resource);
        var key = (services.CurrentScope, actor.Id, resource);
        if (!loaded.TryGetValue(key, out int sound)) loaded[key] = sound = actor.Call("sound_add", path, 0, 0).Int;
        actor.Call("sound_volume", sound, Math.Clamp(volume, 0, 1));
        actor.Call(loop ? "sound_loop" : "sound_play", sound);
    }
    private void ClearWhere(Func<(NativeResourceScope Scope, int Owner, string Resource), bool> matches)
    {
        foreach (var pair in loaded.Where(p => matches(p.Key)).ToArray())
        {
            services.World.Call("sound_delete", [pair.Value], new(new GmInstance())); loaded.Remove(pair.Key);
        }
    }
    public void Clear(int owner) => ClearWhere(k => k.Owner == owner && k.Scope == services.CurrentScope);
    internal void ClearActor(int owner) => ClearWhere(k => k.Owner == owner);
    internal void ClearScope(NativeResourceScope scope) => ClearWhere(k => k.Scope == scope);
    internal void ClearOwner(NativeModOwner owner) => ClearWhere(k => k.Scope.Owner == owner);
    public void Dispose() => ClearWhere(_ => true);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed record NativeSprite(int Owner, string Id, string Resource, double X, double Y, double Angle, double ScaleX, double ScaleY, double OriginX, double OriginY, double Depth);
public sealed record NativeHudItem(int Owner, string Id, string Text, double X, double Y);
public sealed record NativeLine(int Owner, string Id, double X1, double Y1, double X2, double Y2, double Expires, int Colour, double Width);
public sealed class NativePresentation(NativeModServices services)
{
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Id), NativeSprite> sprites = new();
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Id), NativeHudItem> hud = new();
    private readonly Dictionary<(NativeResourceScope Scope, int Owner, string Id), NativeLine> lines = new();
    public IReadOnlyCollection<NativeSprite> Sprites => sprites.Values;
    public IReadOnlyCollection<NativeHudItem> Hud => hud.Values;
    public IReadOnlyCollection<NativeLine> Lines => lines.Values;
    public void Line(int owner, string id, double x1, double y1, double x2, double y2, double seconds = .08, int colour = 0x88eeff, double width = 1)
        => lines[(services.CurrentScope, owner, id)] = new(owner, id, x1, y1, x2, y2, services.Frame.GameTime + Math.Max(0, seconds), colour, width);
    internal void Update() { foreach (var pair in lines.Where(p => p.Value.Expires <= services.Frame.GameTime).ToArray()) lines.Remove(pair.Key); }
    public void Sprite(int owner, string id, string resource, double x, double y, double angle = 0, double scaleX = 1, double scaleY = 1, double originX = 0, double originY = 0, double depth = -10000)
    {
        if (!services.Registry.Resources.ContainsKey(resource)) throw new InvalidDataException("资源未加载：" + resource);
        sprites[(services.CurrentScope, owner, id)] = new(owner, id, resource, x, y, angle, scaleX, scaleY, originX, originY, depth);
    }
    public void Text(int owner, string id, string text, double x = 12, double y = 56) => hud[(services.CurrentScope, owner, id)] = new(owner, id, text, x, y);
    public void Remove(int owner, string id) { var key = (services.CurrentScope, owner, id); sprites.Remove(key); hud.Remove(key); lines.Remove(key); }
    private void ClearWhere(Func<(NativeResourceScope Scope, int Owner, string Id), bool> matches)
    {
        foreach (var key in sprites.Keys.Where(matches).ToArray()) sprites.Remove(key);
        foreach (var key in hud.Keys.Where(matches).ToArray()) hud.Remove(key);
        foreach (var key in lines.Keys.Where(matches).ToArray()) lines.Remove(key);
    }
    public void Clear(int owner) => ClearWhere(k => k.Owner == owner && k.Scope == services.CurrentScope);
    public void Clear() => ClearScope(services.CurrentScope);
    internal void ClearScope(NativeResourceScope scope) => ClearWhere(k => k.Scope == scope);
    internal void ClearOwner(NativeModOwner owner) => ClearWhere(k => k.Scope.Owner == owner);
    internal void ClearActor(int owner) => ClearWhere(k => k.Owner == owner);
    internal void ClearAll() { sprites.Clear(); hud.Clear(); lines.Clear(); }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public readonly record struct NativeRayHit(ActorContext Target, double X, double Y, double Distance, bool Wall);

public sealed class NativeCombatService
{
    private readonly NativeModServices services;
    private readonly JsonElement damage;
    internal NativeCombatService(NativeModServices services)
    {
        this.services = services;
        var tree = JsonNode.Parse(services.World.Code["objects/objBullet/3/1/0"].GetRawText())!;
        var body = Nodes(tree).Single(n => n[0]?.ToString() == "with" && n[1]?[0]?.ToString() == "id" && n[1]?[1]?.ToString() == "col").DeepClone();
        var amount = Nodes(body).Single(n => n[0]?.ToString() == "binary" && n[1]?.ToString() == "AssignSubtract" && n[2]?[1]?.ToString() == "HP");
        amount[3]![2] = new JsonArray("id", "argument0");
        damage = JsonSerializer.SerializeToElement(body);
    }
    private static IEnumerable<JsonArray> Nodes(JsonNode node)
    {
        if (node is not JsonArray array) yield break;
        if (array.Count > 0 && array[0] is JsonValue) yield return array;
        foreach (var child in array) if (child is not null) foreach (var descendant in Nodes(child)) yield return descendant;
    }
    public NativeRayHit? Raycast(ActorContext source, double x, double y, double angle, double range)
    {
        if (!double.IsFinite(range) || range <= 0) throw new ArgumentOutOfRangeException(nameof(range));
        double radians = angle * Math.PI / 180, dx = Math.Cos(radians), dy = -Math.Sin(radians), closest = range;
        NativeRayHit? result = null;
        var world = services.World;
        foreach (var target in world.Instances)
        {
            if (target.Destroyed || !target.Active || target.Id == source.Id) continue;
            bool wall = target.Scope.ReadOr("solid").Truth || world.IsObject(target.Object, world.Constants["objBlock"].Int);
            bool enemy = world.IsObject(target.Object, world.Constants["objEnemyDoll"].Int) && target.Scope.ReadOr("DAMAGE_ACTIVE").Truth;
            if (!wall && !enemy) continue;
            var bounds = world.Bounds(target); if (bounds.Empty) continue;
            double near = 0, far = range;
            bool Axis(double start, double direction, double low, double high)
            {
                if (Math.Abs(direction) < 1e-12) return start >= low && start <= high;
                double a = (low - start) / direction, b = (high - start) / direction;
                near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b)); return near <= far;
            }
            if (Axis(x, dx, bounds.Left, bounds.Right) && Axis(y, dy, bounds.Top, bounds.Bottom) && near <= closest && (result is null || near < closest || wall))
            { closest = near; result = new(new(world, target), x + dx * near, y + dy * near, near, wall); }
        }
        return result;
    }
    public bool Damage(ActorContext source, NativeRayHit hit, double amount, double angle)
    {
        if (hit.Wall || hit.Target.Instance.Destroyed || !hit.Target.Instance.Scope.ReadOr("DAMAGE_ACTIVE").Truth || !double.IsFinite(amount) || amount < 0) return false;
        var value = new NativeGameEvent("combat.damage", hit.Target); value.Arguments["damage"] = amount; value.Arguments["source"] = source.Id;
        services.Dispatch(value, NativeEventPhase.Before);
        if (!value.Handled)
        {
            var probe = new GmInstance { Id = -999999, Object = services.World.Constants["objBullet"].Int };
            foreach (var pair in new Dictionary<string, double> { ["preX"] = source.X, ["preY"] = source.Y, ["x"] = hit.X, ["y"] = hit.Y, ["fireAngle"] = angle, ["direction"] = angle }) probe.Scope.Write(pair.Key, pair.Value);
            var context = new GmContext(probe, args: [value.Arguments["damage"]]); context.Locals.Write("col", hit.Target.Id);
            services.World.Evaluator.Eval(damage, context);
            value.Result = hit.Target.Read("INV") == 0;
        }
        services.Dispatch(value, NativeEventPhase.After); return value.Result.Truth;
    }
}

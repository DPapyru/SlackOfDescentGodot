using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

[Flags]
public enum NativeInputContext { Game = 1, Menu = 2, Editor = 4, All = 7 }
public enum NativeEventPhase { Before, After }

public sealed class NativeInputAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DefaultKey { get; set; }
    public NativeInputContext Contexts { get; set; } = NativeInputContext.Game;
}

public sealed class NativeEntityParameter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "number";
    public string Default { get; set; } = "0";
    public double? Min { get; set; }
    public double? Max { get; set; }
}

public sealed class NativeEntityDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string BehaviorId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public double Width { get; set; } = 16;
    public double Height { get; set; } = 16;
    public bool Solid { get; set; }
    public Dictionary<string, NativeEntityParameter> Parameters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class NativeGameEvent(string name, ActorContext? actor = null)
{
    public string Name { get; } = name;
    public ActorContext? Actor { get; } = actor;
    public Dictionary<string, GmValue> Arguments { get; } = new(StringComparer.Ordinal);
    public GmValue Result { get; set; }
    public bool Handled { get; set; }
}

public sealed record NativeEventHandler(string Owner, string Event, NativeEventPhase Phase, Action<NativeGameEvent> Handle);
public readonly record struct NativeFireRequest(double X, double Y, double Angle, bool Aiming);
public readonly record struct NativeWeaponState(int Ammunition,int Capacity,bool Reloading);
public interface ICharacterWeapon : ICharacterAction
{
    NativeWeaponState? State => null;
    bool IsEquipped(ActorContext actor) => true;
    void Fire(ActorContext actor, NativeFireRequest request);
    void Reload(ActorContext actor) { }
}

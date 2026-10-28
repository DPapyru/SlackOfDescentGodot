namespace SlackOfDescent;

public sealed record InputSnapshot(IReadOnlySet<int> Held, IReadOnlySet<int> Pressed, IReadOnlySet<int> Released)
{
    public static readonly InputSnapshot Empty = new(new HashSet<int>(), new HashSet<int>(), new HashSet<int>());
    public bool Down(int key) => Held.Contains(key);
    public bool JustPressed(int key) => Pressed.Contains(key);
}

public readonly record struct FrameContext(double Delta, double GameTime, long Tick, InputSnapshot Input)
{
    public const double FixedDelta = 1.0 / 60;
}

public enum CharacterActionSlot { Jump, Dash, Attack, Controller }
public enum CharacterActionMode { Replace, Additive, Controller }

public sealed partial class ActorContext(GmWorld world, GmInstance instance)
{
    public GmWorld World { get; } = world;
    public GmInstance Instance { get; } = instance;
    public int Id => Instance.Id;
    public double X { get => Read("x"); set => Write("x", value); }
    public double Y { get => Read("y"); set => Write("y", value); }
    public double HorizontalSpeed { get => Read("hspeed"); set => Write("hspeed", value); }
    public double VerticalSpeed { get => Read("vspeed"); set => Write("vspeed", value); }
    public double Read(string field) => Instance.Scope.ReadOr(field).Number;
    public void Write(string field, double value) => World.Write(Instance, field, value);
    public GmValue Call(string function, params GmValue[] args) => World.Call(function, args, new(Instance));
}

public interface IMod
{
    string Id { get; }
    void Load(ModContext context);
    void Unload();
}

public sealed partial class ModContext(ContentRegistry content, string directory, CancellationToken cancellation, Action<IDisposable> track)
{
    public ContentRegistry Content { get; } = content;
    public string Directory { get; } = directory;
    public CancellationToken Cancellation { get; } = cancellation;
    public void Track(IDisposable resource) => track(resource);
}

public interface ICharacterAction
{
    string Id { get; }
    CharacterActionSlot Slot { get; }
    CharacterActionMode Mode => CharacterActionMode.Replace;
    void Update(ActorContext actor, FrameContext frame);
    void Detach(ActorContext actor) { }
}

public interface ICharacterJumpExtension : ICharacterAction
{
    bool TryExtendJump(ActorContext actor);
    void Jumped(ActorContext actor);
}

public interface IEntityBehavior
{
    string Id { get; }
    void Created(ActorContext entity) { }
    void Collided(ActorContext entity, ActorContext other) { }
    void Interacted(ActorContext entity, ActorContext other) { }
    void RoomEntered(ActorContext entity) { }
    void RoomExited(ActorContext entity) { }
    void Destroyed(ActorContext entity) { }
    void Update(ActorContext entity, FrameContext frame);
    void Detach(ActorContext entity) { }
}

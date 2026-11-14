using System.Text.Json;

namespace SlackOfDescent;

public sealed class GmInstance
{
    public int Id;
    public int Object;
    private bool destroyed;
    private bool active = true;
    public bool Destroyed
    {
        get => destroyed;
        set
        {
            if (destroyed == value)
                return;
            destroyed = value;
            VisibilityChanged?.Invoke();
        }
    }
    public bool Active
    {
        get => active;
        set
        {
            if (active == value)
                return;
            active = value;
            VisibilityChanged?.Invoke();
        }
    }
    internal Action? VisibilityChanged;
    internal bool AnimationSettled;
    public GmScope Scope = new();
    internal (
        GmMask Mask,
        double X,
        double Y,
        double ScaleX,
        double ScaleY,
        double Angle
    ) BoundsKey;
    public GmBounds CachedBounds;
    public bool HasBounds;
    internal int CollisionOrder;
    internal long InactiveEffectExpiry;
    internal bool DestroyedNotified;
    internal bool MotionRest;
    internal bool MotionTouched;
    internal bool PreviousKnown;
    internal double PreviousX;
    internal double PreviousY;
}

public sealed partial class GmWorld
{
    public readonly Dictionary<string, JsonElement> Code;
    public readonly JsonElement Manifest;
    public readonly Dictionary<int, JsonElement> Objects = new();
    private readonly Dictionary<int, int> parents = new();
    private readonly Dictionary<(int Object, int Kind, int Sub), JsonElement> staticEvents = new();
    private readonly Dictionary<int, GmInstance> instanceLookup = new();
    private readonly Dictionary<int, List<GmInstance>> selectionLookup = new();
    private readonly Dictionary<int, List<GmInstance>> exactInstances = new();
    private readonly Dictionary<int, int> instanceCounts = new();
    private readonly Dictionary<(int Kind, int Sub), int[]> eventObjects = new();
    private readonly Dictionary<(int Object, int Kind, int Sub), int> eventOwners = new();
    private readonly Dictionary<int, int[]> collisionTargets = new();
    private readonly Dictionary<(int Actual, int Target), bool> objectRelations = new();
    private readonly Dictionary<JsonElement, ActionPlan[]> actionEntries = new(
        GmJsonIdentity.Instance
    );
    private int indexedCount = -1;
    private int nextCollisionOrder;

    private sealed record CollisionSnapshot(
        GmInstance[] Instances,
        Dictionary<GmInstance, int> Order
    );

    private readonly Dictionary<int, CollisionSnapshot> collisionSnapshots = new();
    private readonly Stack<List<int>> collisionCandidatePool = new();
    public long CollisionSnapshotRebuilds,
        CollisionCandidateVisits,
        CollisionExactTests;

    private CollisionSnapshot Snapshot(int target, GmContext context)
    {
        if (indexedCount != Instances.Count)
            ResetInstanceLookup();
        if (collisionSnapshots.TryGetValue(target, out var snapshot))
            return snapshot;
        var instances = Select(target, context).ToArray();
        var order = new Dictionary<GmInstance, int>(instances.Length);
        for (int n = 0; n < instances.Length; n++)
            order[instances[n]] = n;
        CollisionSnapshotRebuilds++;
        return collisionSnapshots[target] = new(instances, order);
    }

    private void ResetInstanceLookup()
    {
        collisionSnapshots.Clear();
        instanceCounts.Clear();
        instanceLookup.Clear();
        selectionLookup.Clear();
        exactInstances.Clear();
        foreach (var instance in Instances)
        {
            instanceLookup[instance.Id] = instance;
            if (!exactInstances.TryGetValue(instance.Object, out var matches))
                exactInstances[instance.Object] = matches = new();
            matches.Add(instance);
        }
        indexedCount = Instances.Count;
    }

    private void InvalidateEvents()
    {
        eventOwners.Clear();
        collisionTargets.Clear();
        eventObjects.Clear();
    }

    private void InvalidateObjects()
    {
        collisionSnapshots.Clear();
        indexedCount = -1;
        selectionLookup.Clear();
        objectRelations.Clear();
        instanceCounts.Clear();
        InvalidateEvents();
        InvalidateCollisionSprites();
    }

    public readonly Dictionary<int, JsonElement> Rooms = new();
    public readonly Dictionary<int, JsonElement> Sprites = new();
    public readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal);
    public readonly Dictionary<string, string> External = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, GmValue> Constants = new(StringComparer.Ordinal);
    public readonly HashSet<string> GlobalNames = new(StringComparer.Ordinal);
    public readonly List<GmInstance> Instances = new();
    public readonly GmScope Globals = new();
    public readonly GmScope Engine = new();
    public readonly GmEvaluator Evaluator;
    public readonly string Root;
    public readonly string SourceRoot;
    public readonly string UserRoot;
    public readonly HashSet<string> Unsupported = new();
    public readonly Dictionary<string, int> Calls = new();
    public readonly List<string> Trace = new();
    public readonly Dictionary<int, int> Created = new();
    public readonly GmRandom Random = new();
    public readonly GmPerformance Performance = new();
    public int CurrentRoom = -1;
    public int? PendingRoom;
    public int NextId;
    public long Frame;
    public double GameTime { get; private set; }
    public bool IsLoadingRoom =>
        Rooms.TryGetValue(CurrentRoom, out var room)
        && (
            room.GetProperty("speed").GetDouble() > 240
            || room.GetProperty("name").GetString()!.Contains("_Loading", StringComparison.Ordinal)
        );
    public Func<string, GmContext, bool>? SuppressCode;
    public Func<GmInstance, bool>? ExtendCharacterJump;
    public event Action<GmInstance>? CharacterJumped;
    public bool Ended;
    public Action? Redraw;
    public GmFramePlan? FramePlan;

    public void PublishFrame()
    {
        Redraw?.Invoke();
        FramePlan?.Publish(Frame, CurrentRoom);
    }

    public Action? InputEvents;
    public Func<ValueTask>? InputEventsAsync;
    public Func<string, GmValue[], GmContext, GmValue?>? Platform;

    public GmWorld(string root, string sourceRoot, string? userRoot = null)
    {
        Root = root;
        SourceRoot = sourceRoot;
        UserRoot = Path.GetFullPath(
            userRoot
                ?? Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData
                    ),
                    "SlackOfDescentGodot"
                )
        );
        Manifest = JsonDocument
            .Parse(File.ReadAllText(Path.Combine(root, "manifest.json")))
            .RootElement;
        Code = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(Path.Combine(root, "code.json")),
            new JsonSerializerOptions { MaxDepth = 1024 }
        )!;
        ApplyGameplayFixes();
        Evaluator = new(this);
        NextId = Manifest.GetProperty("lastInstanceId").GetInt32() + 1;
        foreach (
            var category in new[]
            {
                "scripts",
                "objects",
                "rooms",
                "sprites",
                "sounds",
                "backgrounds",
                "fonts",
            }
        )
        foreach (var asset in Manifest.GetProperty(category).EnumerateArray())
        {
            int id = asset.GetProperty("id").GetInt32();
            string name = asset.GetProperty("name").GetString()!;
            Constants[name] = id;
            switch (category)
            {
                case "scripts":
                    Scripts[name] = asset.GetProperty("code").GetString()!;
                    break;
                case "objects":
                    Objects[id] = asset;
                    parents[id] = asset.GetProperty("parent").GetInt32();
                    foreach (var ev in asset.GetProperty("events").EnumerateArray())
                        staticEvents[
                            (
                                id,
                                ev.GetProperty("kind").GetInt32(),
                                ev.GetProperty("sub").GetInt32()
                            )
                        ] = ev.GetProperty("actions");
                    break;
                case "rooms":
                    Rooms[id] = asset;
                    break;
                case "sprites":
                    Sprites[id] = asset;
                    break;
            }
        }
        foreach (var asset in Manifest.GetProperty("external").EnumerateArray())
            External[asset.GetProperty("path").GetString()!] = asset
                .GetProperty("code")
                .GetString()!;
        Constants["room_first"] = Manifest.GetProperty("roomOrder")[0].GetInt32();
        Constants["room_last"] = Manifest
            .GetProperty("roomOrder")[Manifest.GetProperty("roomOrder").GetArrayLength() - 1]
            .GetInt32();
        foreach (
            var pair in new Dictionary<string, double>
            {
                ["true"] = 1,
                ["false"] = 0,
                ["pi"] = Math.PI,
                ["noone"] = -4,
                ["all"] = -3,
                ["self"] = -1,
                ["other"] = -2,
                ["global"] = -5,
                ["c_white"] = 16777215,
                ["c_black"] = 0,
                ["c_red"] = 255,
                ["c_lime"] = 65280,
                ["c_blue"] = 16711680,
                ["c_yellow"] = 65535,
                ["c_gray"] = 8421504,
                ["c_silver"] = 12632256,
                ["c_aqua"] = 16776960,
                ["c_fuchsia"] = 16711935,
                ["c_maroon"] = 128,
                ["c_green"] = 32768,
                ["c_navy"] = 8388608,
                ["c_olive"] = 32896,
                ["c_purple"] = 8388736,
                ["c_teal"] = 8421376,
                ["c_orange"] = 4235519,
                ["vk_nokey"] = 0,
                ["vk_anykey"] = 1,
                ["vk_backspace"] = 8,
                ["vk_tab"] = 9,
                ["vk_enter"] = 13,
                ["vk_shift"] = 16,
                ["vk_control"] = 17,
                ["vk_alt"] = 18,
                ["vk_escape"] = 27,
                ["vk_space"] = 32,
                ["vk_left"] = 37,
                ["vk_up"] = 38,
                ["vk_right"] = 39,
                ["vk_down"] = 40,
                ["vk_home"] = 36,
                ["vk_end"] = 35,
                ["vk_pageup"] = 33,
                ["vk_pagedown"] = 34,
                ["vk_insert"] = 45,
                ["vk_delete"] = 46,
                ["mb_left"] = 1,
                ["mb_right"] = 2,
                ["mb_middle"] = 3,
                ["mb_none"] = 0,
                ["mb_any"] = -1,
                ["fa_directory"] = 16,
                ["fa_archive"] = 32,
                ["dll_cdecl"] = 0,
                ["dll_stdcall"] = 1,
                ["ty_real"] = 0,
                ["ty_string"] = 1,
                ["bm_normal"] = 0,
                ["bm_add"] = 1,
                ["bm_max"] = 2,
                ["bm_subtract"] = 3,
                ["bm_zero"] = 1,
                ["bm_one"] = 2,
                ["bm_src_alpha"] = 5,
                ["bm_inv_src_alpha"] = 6,
                ["fa_left"] = 0,
                ["fa_center"] = 1,
                ["fa_right"] = 2,
                ["pr_pointlist"] = 1,
                ["pr_linelist"] = 2,
                ["pr_linestrip"] = 3,
                ["pr_trianglelist"] = 4,
                ["pr_trianglestrip"] = 5,
                ["pr_trianglefan"] = 6,
                ["ev_create"] = 0,
                ["ev_destroy"] = 1,
                ["ev_alarm"] = 2,
                ["ev_step"] = 3,
                ["ev_collision"] = 4,
                ["ev_keyboard"] = 5,
                ["ev_other"] = 7,
                ["ev_draw"] = 8,
                ["ev_keypress"] = 9,
                ["ev_keyrelease"] = 10,
                ["ev_step_normal"] = 0,
                ["ev_step_begin"] = 1,
                ["ev_step_end"] = 2,
                ["ev_room_start"] = 4,
                ["ev_room_end"] = 5,
                ["cr_default"] = -1,
                ["cr_none"] = -1,
            }
        )
            Constants[pair.Key] = pair.Value;
        for (int i = 1; i <= 12; i++)
            Constants["vk_f" + i] = 111 + i;
        for (int i = 0; i < 16; i++)
            Constants["ev_user" + i] = 10 + i;
        foreach (
            var pair in new Dictionary<string, int>
            {
                ["bm_src_color"] = 3,
                ["bm_inv_src_color"] = 4,
                ["bm_dest_alpha"] = 7,
                ["bm_inv_dest_alpha"] = 8,
                ["bm_dest_color"] = 9,
                ["bm_inv_dest_color"] = 10,
                ["bm_src_alpha_sat"] = 11,
            }
        )
            Constants[pair.Key] = pair.Value;
        foreach (
            var pair in new Dictionary<string, int>
            {
                ["vk_return"] = 13,
                ["vk_pause"] = 19,
                ["vk_printscreen"] = 44,
                ["vk_numpad0"] = 96,
                ["vk_numpad1"] = 97,
                ["vk_numpad2"] = 98,
                ["vk_numpad3"] = 99,
                ["vk_numpad4"] = 100,
                ["vk_numpad5"] = 101,
                ["vk_numpad6"] = 102,
                ["vk_numpad7"] = 103,
                ["vk_numpad8"] = 104,
                ["vk_numpad9"] = 105,
                ["vk_multiply"] = 106,
                ["vk_add"] = 107,
                ["vk_subtract"] = 109,
                ["vk_decimal"] = 110,
                ["vk_divide"] = 111,
                ["vk_lshift"] = 160,
                ["vk_rshift"] = 161,
                ["vk_lcontrol"] = 162,
                ["vk_rcontrol"] = 163,
                ["vk_lalt"] = 164,
                ["vk_ralt"] = 165,
            }
        )
            Constants[pair.Key] = pair.Value;
        for (int i = 0; i < 14; i++)
            Constants[
                "pt_shape_"
                    + new[]
                    {
                        "pixel",
                        "disk",
                        "square",
                        "line",
                        "star",
                        "circle",
                        "ring",
                        "sphere",
                        "flare",
                        "spark",
                        "explosion",
                        "cloud",
                        "smoke",
                        "snow",
                    }[i]
            ] = i;
        for (int i = 0; i < 12; i++)
            Constants[
                "ef_"
                    + new[]
                    {
                        "explosion",
                        "ring",
                        "ellipse",
                        "firework",
                        "smoke",
                        "smokeup",
                        "star",
                        "spark",
                        "flare",
                        "cloud",
                        "rain",
                        "snow",
                    }[i]
            ] = i;
        for (int i = 0; i < 4; i++)
            Constants["ps_shape_" + new[] { "rectangle", "ellipse", "diamond", "line" }[i]] = i;
        for (int i = 0; i < 3; i++)
            Constants["ps_distr_" + new[] { "linear", "gaussian", "invgaussian" }[i]] = i;
        Constants["c_dkgray"] = 4210752;
        Constants["c_ltgray"] = 12632256;
        foreach (var constant in Manifest.GetProperty("constants").EnumerateArray())
        {
            var text = constant.GetProperty("value").GetString()!;
            var constantName = constant.GetProperty("name").GetString()!;
            if (
                double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var n
                )
            )
                Constants[constantName] = n;
            else if (Constants.TryGetValue(text, out var referenced))
                Constants[constantName] = referenced;
            else if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
                Constants[constantName] = text[1..^1];
            else
                throw new NotSupportedException("Constant expression " + text);
        }
        foreach (
            var pair in new Dictionary<string, GmValue>
            {
                ["room_speed"] = 30,
                ["room_width"] = 640,
                ["room_height"] = 480,
                ["mouse_x"] = 0,
                ["mouse_y"] = 0,
                ["current_time"] = 0,
                ["fps"] = 30,
                ["game_id"] = 0,
                ["working_directory"] = sourceRoot + Path.DirectorySeparatorChar,
                ["program_directory"] = sourceRoot + Path.DirectorySeparatorChar,
                ["temp_directory"] = Path.GetTempPath(),
                ["keyboard_key"] = 0,
                ["keyboard_lastkey"] = 0,
                ["keyboard_string"] = "",
                ["mouse_button"] = 0,
                ["mouse_lastbutton"] = 0,
                ["cursor_sprite"] = -1,
                ["view_enabled"] = 0,
                ["view_current"] = 0,
                ["background_color"] = 0,
                ["background_showcolor"] = 1,
                ["show_score"] = 0,
                ["show_lives"] = 0,
                ["show_health"] = 0,
                ["score"] = 0,
                ["lives"] = 0,
                ["health"] = 100,
            }
        )
            Engine.Write(pair.Key, pair.Value);
        foreach (
            string name in new[]
            {
                "view_xview",
                "view_yview",
                "view_wview",
                "view_hview",
                "view_xport",
                "view_yport",
                "view_wport",
                "view_hport",
                "view_hborder",
                "view_vborder",
                "view_hspeed",
                "view_vspeed",
                "view_object",
                "view_angle",
                "view_visible",
                "background_visible",
                "background_foreground",
                "background_index",
                "background_x",
                "background_y",
                "background_width",
                "background_height",
                "background_htiled",
                "background_vtiled",
                "background_xscale",
                "background_yscale",
                "background_hspeed",
                "background_vspeed",
                "background_blend",
                "background_alpha",
            }
        )
            for (int i = 0; i < 8; i++)
                Engine.Write(name, name.Contains("scale") || name.Contains("alpha") ? 1 : 0, i);
    }

    public GmValue Read(GmInstance instance, string name, IReadOnlyList<int>? indices = null) =>
        Read(instance, name, new GmIndices(indices));

    internal GmValue Read(GmInstance instance, string name, GmIndices indices)
    {
        if (name == "id")
            return instance.Id;
        if (name == "object_index")
            return instance.Object;
        if (name is "bbox_left" or "bbox_top" or "bbox_right" or "bbox_bottom")
        {
            var b = Bounds(instance);
            return name switch
            {
                "bbox_left" => b.Left,
                "bbox_top" => b.Top,
                "bbox_right" => b.Right,
                _ => b.Bottom,
            };
        }
        if (name == "room")
            return CurrentRoom;
        if (name == "debug_mode")
            return false;
        if (name == "instance_count")
            return Instances.Count(i => !i.Destroyed);
        if (name == "instance_id")
            return Instances.Where(i => !i.Destroyed).ElementAt(indices.FirstOr(0)).Id;
        if (name == "event_type" || name == "event_number")
            return 0;
        if (
            name.StartsWith("sprite_", StringComparison.Ordinal)
            && name is "sprite_width" or "sprite_height" or "sprite_xoffset" or "sprite_yoffset"
        )
        {
            var sprite = instance.Scope.ReadOr("sprite_index", -1).Int;
            if (!Sprites.TryGetValue(sprite, out var s))
                return 0;
            return name switch
            {
                "sprite_width" => s.GetProperty("frames")[0].GetProperty("width").GetInt32(),
                "sprite_height" => s.GetProperty("frames")[0].GetProperty("height").GetInt32(),
                "sprite_xoffset" => s.GetProperty("originX").GetInt32(),
                _ => s.GetProperty("originY").GetInt32(),
            };
        }
        if (name == "image_number")
            return Sprites.TryGetValue(instance.Scope.ReadOr("sprite_index", -1).Int, out var sp)
                ? sp.GetProperty("frames").GetArrayLength()
                : 0;
        if (Engine.Contains(name, indices))
            return Engine.Read(name, indices);
        return instance.Scope.Read(name, indices);
    }

    public void Write(
        GmInstance instance,
        string name,
        GmValue value,
        IReadOnlyList<int>? indices = null
    ) => Write(instance, name, value, new GmIndices(indices));

    internal void Write(GmInstance instance, string name, GmValue value, GmIndices indices)
    {
        if (name == "debug_mode")
            throw new InvalidOperationException("Read-only variable debug_mode");
        if (name == "room")
        {
            PendingRoom = value.Int;
            return;
        }
        if (Engine.Contains(name, indices))
        {
            Engine.Write(name, value, indices);
            return;
        }
        if (name is "hspeed" or "vspeed" && instance.Scope.ReadOr(name).Equals(value))
            return;
        if (name == "direction")
        {
            double direction = value.Number % 360;
            value = direction < 0 ? direction + 360 : direction;
        }
        instance.Scope.Write(name, value, indices);
        if (name is "hspeed" or "vspeed")
        {
            var h = instance.Scope.ReadOr("hspeed").Number;
            var v = instance.Scope.ReadOr("vspeed").Number;
            instance.Scope.Write("speed", Math.Sqrt(h * h + v * v));
            double direction = Direction(0, 0, h, v),
                rounded = Math.Round(direction, MidpointRounding.ToEven);
            instance.Scope.Write(
                "direction",
                Math.Abs(direction - rounded) < 0.0001 ? rounded : direction
            );
        }
        if (name is "speed" or "direction")
        {
            var speed = instance.Scope.ReadOr("speed").Number;
            var dir = instance.Scope.ReadOr("direction").Number * Math.PI / 180;
            double h = Math.Cos(dir) * speed,
                v = -Math.Sin(dir) * speed;
            double roundedH = Math.Round(h, MidpointRounding.ToEven),
                roundedV = Math.Round(v, MidpointRounding.ToEven);
            instance.Scope.Write("hspeed", Math.Abs(h - roundedH) < 0.0001 ? roundedH : h);
            instance.Scope.Write("vspeed", Math.Abs(v - roundedV) < 0.0001 ? roundedV : v);
        }
    }

    public bool IsObject(int actual, int target)
    {
        if (actual == target)
            return Objects.ContainsKey(actual);
        var key = (actual, target);
        if (objectRelations.TryGetValue(key, out bool result))
            return result;
        for (int depth = 0; depth < 128 && Objects.ContainsKey(actual); depth++)
        {
            if (actual == target)
                return objectRelations[key] = true;
            actual = parents.GetValueOrDefault(actual, -1);
        }
        return objectRelations[key] = false;
    }

    public IEnumerable<GmInstance> Select(int target, GmContext context)
    {
        if (target == -1)
            return context.Self.Destroyed ? [] : [context.Self];
        if (target == -2)
            return context.Other.Destroyed ? [] : [context.Other];
        if (target == -4)
            return [];
        if (target == -3)
            return Instances.Where(i => !i.Destroyed && i.Active);
        if (indexedCount != Instances.Count)
            ResetInstanceLookup();
        if (target >= 100000)
            return
                instanceLookup.TryGetValue(target, out var found)
                && !found.Destroyed
                && found.Active
                ? [found]
                : [];
        if (!selectionLookup.TryGetValue(target, out var matches))
            selectionLookup[target] = matches = Instances
                .Where(i => i.Id == target || IsObject(i.Object, target))
                .ToList();
        return matches.Where(i => !i.Destroyed && i.Active);
    }

    internal IEnumerable<GmInstance> ReferenceInstances(int target, GmContext context) =>
        target == -1 ? [context.Self]
        : target == -2 ? [context.Other]
        : Select(target, context);

    private IEnumerable<GmInstance> ActivationInstances(int target)
    {
        if (target == -3)
            return Instances.Where(i => !i.Destroyed);
        if (indexedCount != Instances.Count)
            ResetInstanceLookup();
        if (target >= 100000)
            return instanceLookup.TryGetValue(target, out var found) && !found.Destroyed
                ? [found]
                : [];
        if (!selectionLookup.TryGetValue(target, out var matches))
            selectionLookup[target] = matches = Instances
                .Where(i => i.Id == target || IsObject(i.Object, target))
                .ToList();
        return matches.Where(i => !i.Destroyed);
    }

    private int InstanceCount(int target, GmContext context)
    {
        if (target is -1 or -2)
            return Select(target, context).Count();
        if (indexedCount != Instances.Count)
            ResetInstanceLookup();
        if (!instanceCounts.TryGetValue(target, out int count))
            instanceCounts[target] = count = Select(target, context).Count();
        return count;
    }

    public GmInstance Create(int obj, double x, double y, int? id = null, bool run = true)
    {
        using var measurement = Performance.Objects.Begin(obj, 0);
        if (!Objects.TryGetValue(obj, out var o))
            throw new InvalidOperationException("Missing object " + obj);
        var instance = new GmInstance { Id = id ?? NextId++, Object = obj };
        if (SlotExperiment && slotLayouts.TryGetValue(obj, out var slotPlan) && slotPlan is not null)
            instance.Scope.UseLayout(slotPlan.Scalars, slotPlan.Arrays);
        foreach (
            var pair in new Dictionary<string, GmValue>
            {
                ["x"] = x,
                ["y"] = y,
                ["xprevious"] = x,
                ["yprevious"] = y,
                ["xstart"] = x,
                ["ystart"] = y,
                ["sprite_index"] = o.GetProperty("sprite").GetInt32(),
                ["mask_index"] = o.GetProperty("mask").GetInt32(),
                ["solid"] = o.GetProperty("solid").GetBoolean(),
                ["visible"] = o.GetProperty("visible").GetBoolean(),
                ["persistent"] = o.GetProperty("persistent").GetBoolean(),
                ["depth"] = o.GetProperty("depth").GetInt32(),
                ["image_index"] = 0,
                ["image_speed"] = 1,
                ["image_xscale"] = 1,
                ["image_yscale"] = 1,
                ["image_angle"] = 0,
                ["image_alpha"] = 1,
                ["image_blend"] = 16777215,
                ["hspeed"] = 0,
                ["vspeed"] = 0,
                ["speed"] = 0,
                ["direction"] = 0,
                ["friction"] = 0,
                ["gravity"] = 0,
                ["gravity_direction"] = 270,
                ["path_index"] = -1,
                ["timeline_index"] = -1,
            }
        )
            instance.Scope.Write(pair.Key, pair.Value);
        for (int i = 0; i < 12; i++)
            instance.Scope.Write("alarm", -1, i);
        if (indexedCount == Instances.Count)
        {
            instanceLookup[instance.Id] = instance;
            foreach (var pair in selectionLookup)
                if (instance.Id == pair.Key || IsObject(instance.Object, pair.Key))
                    pair.Value.Add(instance);
            if (!exactInstances.TryGetValue(obj, out var matches))
                exactInstances[obj] = matches = new();
            matches.Add(instance);
            indexedCount++;
        }
        Instances.Add(instance);
        collisionSnapshots.Clear();
        geometryVersion++;
        instanceCounts.Clear();
        instance.VisibilityChanged = () =>
        {
            collisionSnapshots.Clear();
            geometryVersion++;
            instanceCounts.Clear();
            if (instance.Destroyed)
            {
                changedColliders.Add(instance);
                NotifyDestroyed(instance);
            }
            if (
                instance.Object == Constants["objFluid"].Int
                || instance.Object == Constants["objFluidInWater"].Int
            )
                instance.InactiveEffectExpiry = instance.Active
                    ? 0
                    : Frame + Math.Max(1, instance.Scope.Read("alarm", 0).Int);
        };
        WatchCollider(instance);
        if (collisionIndexedCount == Instances.Count - 1)
        {
            instance.CollisionOrder = nextCollisionOrder++;
            changedColliders.Add(instance);
            collisionIndexedCount++;
        }
        Created[obj] = Created.GetValueOrDefault(obj) + 1;
        CreatedObserver?.Invoke(instance);
        if (run)
            Event(instance, 0, 0);
        return instance;
    }

    private int EventOwner(int obj, int kind, int sub)
    {
        var key = (obj, kind, sub);
        if (eventOwners.TryGetValue(key, out int owner))
            return owner;
        while (Objects.ContainsKey(obj))
        {
            if (
                addedEvents.ContainsKey((obj, kind, sub))
                || staticEvents.ContainsKey((obj, kind, sub))
            )
                return eventOwners[key] = obj;
            obj = parents.GetValueOrDefault(obj, -1);
        }
        return eventOwners[key] = -1;
    }

    public bool HasEvent(int obj, int kind, int sub) => EventOwner(obj, kind, sub) >= 0;

    public JsonElement[]? EventActions(int obj, int kind, int sub)
    {
        if (addedEvents.ContainsKey((obj, kind, sub)))
            return null;
        int owner = EventOwner(obj, kind, sub);
        if (owner < 0)
            return [];
        return staticEvents.TryGetValue((owner, kind, sub), out var actions)
            ? actions.EnumerateArray().ToArray()
            : null;
    }

    public IEnumerable<GmInstance> EventInstances(int kind, int sub)
    {
        if (!eventObjects.TryGetValue((kind, sub), out var objects))
            eventObjects[(kind, sub)] = objects = Objects
                .Keys.Where(obj => HasEvent(obj, kind, sub))
                .Order()
                .ToArray();
        foreach (int obj in objects)
        {
            if (indexedCount != Instances.Count)
                ResetInstanceLookup();
            if (!exactInstances.TryGetValue(obj, out var matches))
                continue;
            foreach (var instance in matches.ToArray())
                if (!instance.Destroyed && instance.Active && instance.Object == obj)
                    yield return instance;
        }
    }

    private readonly Stack<GmContext> contextPool = new();
    private readonly Stack<GmScope> localsPool = new();

    private GmContext RentContext(
        GmInstance instance,
        GmInstance? other,
        GmValue[] arguments,
        GmScope? locals
    )
    {
        var context = contextPool.Count > 0 ? contextPool.Pop() : new GmContext(instance);
        if (locals is null)
        {
            locals = localsPool.Count > 0 ? localsPool.Pop() : new GmScope();
            locals.Reset();
        }
        context.Self = instance;
        context.Other = other ?? instance;
        context.Arguments = arguments;
        context.Locals = locals;
        context.Flow = null;
        context.Result = default;
        return context;
    }

    private void ReturnContext(GmContext context, bool releaseLocals)
    {
        var locals = releaseLocals ? context.Locals : null;
        context.Self = null!;
        context.Other = null!;
        context.Arguments = [];
        context.Locals = null!;
        context.Flow = null;
        context.Result = default;
        context.EventObject = -1;
        context.EventKind = 0;
        context.EventSub = 0;
        contextPool.Push(context);
        if (locals is null)
            return;
        locals.Reset();
        localsPool.Push(locals);
    }

    public bool Event(
        GmInstance instance,
        int kind,
        int sub,
        GmInstance? other = null,
        int? start = null
    )
    {
        int obj = EventOwner(start ?? instance.Object, kind, sub);
        using var measurement = Performance.Objects.Begin(obj < 0 ? -1 : instance.Object, kind);
        while (Objects.TryGetValue(obj, out var definition))
        {
            if (addedEvents.TryGetValue((obj, kind, sub), out var added))
            {
                var context = RentContext(instance, other, [], null);
                context.EventObject = obj;
                context.EventKind = kind;
                context.EventSub = sub;
                try
                {
                    if (
                        !clearedEvents.Contains((obj, kind, sub))
                        && staticEvents.TryGetValue((obj, kind, sub), out var addedActions)
                    )
                        Actions(addedActions, context);
                    foreach (var code in added.ToArray())
                        Evaluator.Run(code, context);
                }
                catch (GmFlow f) when (f.Kind is "exit" or "return") { }
                finally
                {
                    ReturnContext(context, true);
                }
                return true;
            }
            if (staticEvents.TryGetValue((obj, kind, sub), out var entries))
            {
                var c = RentContext(instance, other, [], null);
                c.EventObject = obj;
                c.EventKind = kind;
                c.EventSub = sub;
                try
                {
                    Actions(entries, c);
                }
                catch (GmFlow f) when (f.Kind is "exit" or "return") { }
                finally
                {
                    ReturnContext(c, true);
                }
                return true;
            }
            obj = parents.GetValueOrDefault(obj, -1);
        }
        return false;
    }

    private readonly struct ActionPlan
    {
        public readonly JsonElement Action;
        public readonly int Kind;
        public readonly bool Condition;
        public readonly bool Invert;
        public readonly int Target;
        public readonly string? Code;
        public readonly string? Function;
        public readonly bool Executes;
        public readonly bool Relative;

        public ActionPlan(JsonElement action)
        {
            Action = action;
            Kind = action.GetProperty("kind").GetInt32();
            Condition =
                action.TryGetProperty("condition", out var condition) && condition.GetBoolean();
            Invert = action.TryGetProperty("invert", out var invert) && invert.GetBoolean();
            Target = action.TryGetProperty("target", out var target) ? target.GetInt32() : -1;
            Code =
                action.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
            Function =
                action.TryGetProperty("function", out var function)
                && function.ValueKind == JsonValueKind.String
                    ? function.GetString()
                    : null;
            Executes =
                action.TryGetProperty("execution", out var execution)
                && execution.GetInt32() == 1;
            Relative = action.TryGetProperty("relative", out var relative) && relative.GetBoolean();
        }
    }

    private void Actions(JsonElement actions, GmContext context)
    {
        if (!actionEntries.TryGetValue(actions, out var plans))
            actionEntries[actions] = plans = actions
                .EnumerateArray()
                .Where(a =>
                    a.GetProperty("kind").GetInt32() != 0
                    || a.GetProperty("execution").GetInt32() != 0
                )
                .Select(a => new ActionPlan(a))
                .ToArray();
        int index = 0;
        while (index < plans.Length)
            ActionBlock(plans, ref index, context, true);
    }

    private void ActionBlock(ActionPlan[] plans, ref int index, GmContext context, bool execute)
    {
        if (index >= plans.Length)
            return;
        var plan = plans[index++];
        if (plan.Kind == 1)
        {
            while (index < plans.Length && plans[index].Kind != 2)
                ActionBlock(plans, ref index, context, execute);
            if (index < plans.Length)
                index++;
            return;
        }
        if (plan.Kind == 4)
        {
            if (execute)
                throw new GmFlow("exit");
            return;
        }
        if (plan.Kind is 2 or 3)
            return;
        var result = execute ? InvokeAction(plan, context) : new GmValue(0);
        if (!plan.Condition)
            return;
        bool passed = result.Truth ^ plan.Invert;
        ActionBlock(plans, ref index, context, execute && passed);
        if (index < plans.Length && plans[index].Kind == 3)
        {
            index++;
            ActionBlock(plans, ref index, context, execute && !passed);
        }
    }

    private GmValue InvokeAction(ActionPlan plan, GmContext context)
    {
        GmValue result = 0;
        if (plan.Target == -1)
            return InvokeActionOn(plan, context, context.Self) ?? result;
        foreach (var other in Select(plan.Target, context).ToArray())
            if (InvokeActionOn(plan, context, other) is GmValue executed)
                result = executed;
        return result;
    }

    private GmValue? InvokeActionOn(ActionPlan plan, GmContext context, GmInstance target)
    {
        var c = RentContext(target, context.Other, context.Arguments, context.Locals);
        c.EventObject = context.EventObject;
        c.EventKind = context.EventKind;
        c.EventSub = context.EventSub;
        try
        {
            if (plan.Code is string code)
                return Evaluator.Run(code, c);
            if (!plan.Executes)
                return null;
            var args = plan
                .Action.GetProperty("parameters")
                .EnumerateArray()
                .Select(p => Evaluator.Eval(p, c))
                .ToArray();
            c.Locals.Write("argument_relative", plan.Relative);
            return Call(plan.Function!, args, c);
        }
        finally
        {
            ReturnContext(c, false);
        }
    }

    internal void ChangeObject(GmInstance instance, int obj)
    {
        instance.Object = obj;
        if (SlotExperiment)
        {
            var plan = slotLayouts.GetValueOrDefault(obj);
            instance.Scope.UseLayout(plan?.Scalars, plan?.Arrays);
        }
        InvalidateObjects();
    }

    public void LoadRoom(int id) => LoadRoomAsync(id).GetAwaiter().GetResult();

    private bool prewarmed;

    internal static bool SlotArraySlots =
        System.Environment.GetEnvironmentVariable("SLACK_SLOT_ARRAY") != "0";
    internal static bool SlotExperiment =
        System.Environment.GetEnvironmentVariable("SLACK_SLOT_DRAW") != "0";
    internal sealed record SlotPlan(
        Dictionary<string, int> Scalars,
        Dictionary<string, int>? Arrays
    );

    private readonly Dictionary<int, SlotPlan?> slotLayouts = new();
    private readonly HashSet<int> slotLayoutBuilding = new();
    private readonly HashSet<string> engineNames = new(StringComparer.Ordinal);
    private int slotGlobalsSeen;
    private static readonly HashSet<string> slotReserved = new(StringComparer.Ordinal)
    {
        "id","object_index","bbox_left","bbox_top","bbox_right","bbox_bottom","room","debug_mode",
        "instance_count","instance_id","event_type","event_number","sprite_width","sprite_height",
        "sprite_xoffset","sprite_yoffset","image_number","self","other","global","argument_count",
        "hspeed","vspeed","speed","direction",
    };

    private bool SlotEligible(int obj)
    {
        if (!Objects.TryGetValue(obj, out var definition))
            return false;
        var name = definition.GetProperty("name").GetString()!;
        if (
            name is "objMud" or "objSwampSurface" or "objSwampSurface2"
            || name.StartsWith("objQuicksand", StringComparison.Ordinal)
        )
            return true;
        for (
            int current = obj, depth = 0;
            current >= 0 && depth < 64;
            current = parents.GetValueOrDefault(current, -1), depth++
        )
        {
            if (!Objects.TryGetValue(current, out var ancestor))
                break;
            if (
                ancestor.GetProperty("name").GetString()!
                is "objDoll" or "objControllableDoll" or "objExtParts" or "objParts"
            )
                return true;
        }
        return false;
    }

    internal SlotPlan? SlotLayout(int obj)
    {
        if (!SlotExperiment)
            return null;
        if (GlobalNames.Count != slotGlobalsSeen)
        {
            slotGlobalsSeen = GlobalNames.Count;
            slotLayouts.Clear();
            slotPrewarmed.Clear();
            foreach (var instance in Instances)
                instance.Scope.UseLayout(null);
        }
        if (slotLayouts.TryGetValue(obj, out var cached))
            return cached;
        if (!SlotEligible(obj))
            return slotLayouts[obj] = null;
        if (!slotLayoutBuilding.Add(obj))
            return null;
        try
        {
            if (engineNames.Count == 0)
                foreach (var key in Engine.Values.Keys)
                {
                    int bracket = key.IndexOf('[');
                    engineNames.Add(bracket > 0 ? key[..bracket] : key);
                }
            var names = new HashSet<string>(StringComparer.Ordinal);
            var indexed = new HashSet<string>(StringComparer.Ordinal);
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            for (
                int current = obj, depth = 0;
                current >= 0 && depth < 64;
                current = parents.GetValueOrDefault(current, -1), depth++
            )
                foreach (var entry in staticEvents)
                    if (entry.Key.Object == current)
                        ScanActions(entry.Value, names, indexed, excluded);
            names.RemoveWhere(indexed.Contains);
            var layout = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in names)
                if (CanBeSlot(name))
                    layout[name] = layout.Count;
            var arrayLayout = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in indexed)
                if (!excluded.Contains(name) && CanBeSlot(name))
                    arrayLayout[name] = arrayLayout.Count;
            if (!SlotArraySlots)
                arrayLayout.Clear();
            if (layout.Count == 0 && arrayLayout.Count == 0)
                return slotLayouts[obj] = null;
            return slotLayouts[obj] = new SlotPlan(
                layout,
                arrayLayout.Count > 0 ? arrayLayout : null
            );
        }
        finally
        {
            slotLayoutBuilding.Remove(obj);
        }
    }

    private bool CanBeSlot(string name) =>
        !slotReserved.Contains(name)
        && !IsArgumentName(name)
        && !Constants.ContainsKey(name)
        && !engineNames.Contains(name)
        && !GlobalNames.Contains(name);

    private static bool IsArgumentName(string name) =>
        name == "argument"
        || (name.StartsWith("argument", StringComparison.Ordinal) && int.TryParse(name[8..], out _));

    private void ScanActions(
        JsonElement actions,
        HashSet<string> names,
        HashSet<string> indexed,
        HashSet<string> excluded
    )
    {
        foreach (var action in actions.EnumerateArray())
        {
            if (
                action.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && Code.TryGetValue(code.GetString()!, out var ast)
            )
                ScanNode(ast, names, indexed, excluded);
            if (action.TryGetProperty("parameters", out var parameters))
                ScanNode(parameters, names, indexed, excluded);
        }
    }

    private static void ScanNode(
        JsonElement node,
        HashSet<string> names,
        HashSet<string> indexed,
        HashSet<string> excluded
    )
    {
        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() == 0)
            return;
        var tag = node[0].ValueKind == JsonValueKind.String ? node[0].GetString() : null;
        switch (tag)
        {
            case "id":
                names.Add(node[1].GetString()!);
                return;
            case "num":
            case "str":
                return;
            case "var":
                return;
            case "globalvar":
                foreach (var declared in node[1].EnumerateArray())
                    excluded.Add(declared.GetString()!);
                return;
        }
        if (tag == "binary" && node[1].GetString() is "Index" or "Deref")
        {
            var source = node[2];
            bool isIndex = node[1].GetString() == "Index";
            if (isIndex && source.ValueKind == JsonValueKind.Array && source[0].GetString() == "id")
                indexed.Add(source[1].GetString()!);
            else
                ScanNode(source, names, indexed, excluded);
            if (!isIndex)
                return;
        }
        for (int index = 0; index < node.GetArrayLength(); index++)
            ScanNode(node[index], names, indexed, excluded);
    }

    internal SlotPlan? SlotLayoutFor(int obj)
    {
        if (!SlotExperiment)
            return null;
        if (GlobalNames.Count != slotGlobalsSeen)
            return SlotLayout(obj);
        return slotLayouts.TryGetValue(obj, out var layout) ? layout : null;
    }

    private void PrewarmSlots()
    {
        if (!SlotExperiment)
            return;
        foreach (int obj in Objects.Keys.ToArray())
            SlotLayout(obj);
        foreach (var instance in Instances)
            if (slotLayouts.TryGetValue(instance.Object, out var plan) && plan is not null)
                instance.Scope.UseLayout(plan.Scalars, plan.Arrays);
        var live = new HashSet<int>(Instances.Select(instance => instance.Object));
        foreach (var pair in slotLayouts)
            if (pair.Value is { } plan && live.Contains(pair.Key))
                PrewarmLayout(pair.Key, plan);
    }

    private void PrewarmLayout(int obj, SlotPlan plan)
    {
        if (!slotPrewarmed.Add(obj))
            return;
        for (
            int current = obj, depth = 0;
            current >= 0 && depth < 64;
            current = parents.GetValueOrDefault(current, -1), depth++
        )
            foreach (var entry in staticEvents)
                if (entry.Key.Object == current)
                    foreach (var action in entry.Value.EnumerateArray())
                        if (
                            action.TryGetProperty("code", out var code)
                            && code.ValueKind == JsonValueKind.String
                            && Code.TryGetValue(code.GetString()!, out var ast)
                        )
                            Evaluator.PrewarmSlot(ast, plan);
    }

    private readonly HashSet<int> slotPrewarmed = new();

    private void PrewarmCode()
    {
        if (prewarmed)
            return;
        prewarmed = true;
        foreach (var code in Code.Values)
            Evaluator.Prewarm(code);
    }

    public async ValueTask LoadRoomAsync(int id)
    {
        PrewarmCode();
        long loadStarted = Performance.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long loadTicks = Performance.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        id = NativeServices?.Maps.ResolveRoom(id) ?? id;
        if (!Rooms.TryGetValue(id, out var room))
            throw new InvalidOperationException("Missing room " + id);
        var roomEvent = new NativeGameEvent(
            NativeEventNames.Room,
            Instances.FirstOrDefault(i => !i.Destroyed) is { } actor ? new(this, actor) : null
        );
        roomEvent.Arguments["previous"] = CurrentRoom;
        roomEvent.Arguments["next"] = id;
        NativeServices?.Dispatch(roomEvent, NativeEventPhase.Before);
        NativeServices?.Maps.BeforeRoomLoad(id);
        Evaluator.ResetBudget();
        foreach (var i in Instances.Where(i => !i.Destroyed).ToArray())
            await EventAsync(i, 7, 5);
        if (DestroyedObserver is not null)
            foreach (var i in Instances.ToArray())
                if (!i.Scope.ReadOr("persistent").Truth || i.Destroyed)
                    NotifyDestroyed(i);
        Instances.RemoveAll(i => !i.Scope.ReadOr("persistent").Truth || i.Destroyed);
        ResetInstanceLookup();
        InvalidateCollisionSprites();
        CurrentRoom = id;
        PendingRoom = null;
        Tiles.Clear();
        Tiles.AddRange(room.GetProperty("tiles").EnumerateArray());
        HiddenTileLayers.Clear();
        TilesVersion++;
        nextTile = Math.Max(nextTile, Manifest.GetProperty("lastTileId").GetInt32());
        roomOverrides.TryGetValue(id, out var overrides);
        Engine.Write(
            "room_width",
            overrides?.Width ?? room.GetProperty("width").GetInt32()
        );
        Engine.Write(
            "room_height",
            overrides?.Height ?? room.GetProperty("height").GetInt32()
        );
        Engine.Write("room_speed", room.GetProperty("speed").GetInt32());
        Engine.Write("background_color", room.GetProperty("colour").GetDouble());
        Engine.Write("view_enabled", room.GetProperty("viewsEnabled").GetBoolean());
        InitializeBackgrounds(room);
        int vi = 0;
        foreach (var view in room.GetProperty("views").EnumerateArray())
        {
            var editedView = overrides is not null && overrides.Views.TryGetValue(vi, out var foundView) ? foundView : null;
            for (int field = 0; field < RoomViewFields.Length; field++)
                Engine.Write(
                    "view_" + EngineViewNames[field],
                    editedView?.Fields[field] ?? view.GetProperty(RoomViewFields[field]).GetDouble(),
                    vi
                );
            Engine.Write(
                "view_visible",
                editedView?.Visible ?? view.GetProperty("visible").GetBoolean(),
                vi++
            );
        }
        var created = new List<(GmInstance Instance, JsonElement Data)>();
        foreach (var data in room.GetProperty("instances").EnumerateArray())
        {
            var i = Create(
                data.GetProperty("object").GetInt32(),
                data.GetProperty("x").GetDouble(),
                data.GetProperty("y").GetDouble(),
                data.GetProperty("id").GetInt32(),
                false
            );
            i.Scope.Write("image_xscale", data.GetProperty("xscale").GetDouble());
            i.Scope.Write("image_yscale", data.GetProperty("yscale").GetDouble());
            i.Scope.Write("image_angle", data.GetProperty("angle").GetDouble());
            i.Scope.Write("image_blend", data.GetProperty("blend").GetDouble());
            created.Add((i, data));
        }
        foreach (var (i, data) in created)
        {
            await Evaluator.RunAsync(data.GetProperty("code").GetString()!, new(i));
            await EventAsync(i, 0, 0);
            NativeServices?.Maps.EntityCreated(i);
        }
        var dummy =
            Instances.FirstOrDefault(i => !i.Destroyed)
            ?? new GmInstance { Id = -1000, Object = -1 };
        await Evaluator.RunAsync(room.GetProperty("code").GetString()!, new(dummy));
        foreach (var i in Instances.Where(i => !i.Destroyed).ToArray())
            await EventAsync(i, 7, 4);
        NativeServices?.Maps.RoomEntered(id);
        NativeServices?.Dispatch(roomEvent, NativeEventPhase.After);
        PrewarmSlots();
        Trace.Add("room:" + room.GetProperty("name").GetString());
        if (loadStarted > 0)
            Console.WriteLine(
                "ROOM_LOAD room=" + id
                + " alloc=" + (GC.GetAllocatedBytesForCurrentThread() - loadStarted)
                + "B ms=" + System.Diagnostics.Stopwatch
                    .GetElapsedTime(loadTicks).TotalMilliseconds.ToString("0.0")
            );
    }

    public void Step() => StepAsync().GetAwaiter().GetResult();

    public async ValueTask StepAsync()
    {
        if (Ended)
            return;
        long total = Performance.Start(),
            phase = total;
        Evaluator.ResetBudget();
        if (PendingRoom is int next)
        {
            await LoadRoomAsync(next);
            return;
        }
        Frame++;
        StepBeginObserver?.Invoke();
        if (!IsLoadingRoom)
            GameTime += 1 / Math.Max(1, Engine.Read("room_speed").Number);
        Engine.Write("current_time", GameTime * 1000);
        var live = LiveSnapshot();
        foreach (var i in live)
        {
            var x = i.Scope.ReadOr("x");
            var y = i.Scope.ReadOr("y");
            if (!i.PreviousKnown || x.IsString || i.PreviousX != x.Number)
                i.Scope.Write("xprevious", x);
            if (!i.PreviousKnown || y.IsString || i.PreviousY != y.Number)
                i.Scope.Write("yprevious", y);
            i.PreviousX = x.Number;
            i.PreviousY = y.Number;
            i.PreviousKnown = true;
        }
        long sub = Performance.Start();
        await RunObjectEventAsync(3, 1);
        Performance.End("begin_events", sub);
        sub = Performance.Start();
        for (int a = 0; a < 12; a++)
            foreach (var i in EventInstances(2, a))
            {
                if (i.Destroyed || !i.Active)
                    continue;
                var t = i.Scope.Read("alarm", a).Int;
                if (t >= 0)
                {
                    i.Scope.Write("alarm", t - 1, a);
                    if (t == 1)
                    {
                        NotifyAlarm(i, a);
                        await EventAsync(i, 2, a);
                    }
                }
            }
        Performance.End("begin_alarms", sub);
        sub = Performance.Start();
        if (InputEventsAsync is not null)
            await InputEventsAsync();
        else
            InputEvents?.Invoke();
        Performance.End("begin_dispatch", sub);
        Performance.End("begin_input", phase);
        phase = Performance.Start();
        await RunObjectEventAsync(3, 0);
        live = LiveSnapshot();
        Performance.End("step", phase);
        phase = Performance.Start();
        NativeServices?.Motions.BeforePhysics();
        foreach (var i in live)
        {
            if (i.Destroyed || !i.Active)
                continue;
            if (!i.MotionTouched && i.MotionRest)
                continue;
            var h = i.Scope.ReadOr("hspeed").Number;
            var v = i.Scope.ReadOr("vspeed").Number;
            var friction = i.Scope.ReadOr("friction").Number;
            if (friction != 0)
            {
                double speed = Math.Sqrt(h * h + v * v),
                    remaining = Math.Max(0, speed - friction);
                if (speed > 0)
                {
                    h *= remaining / speed;
                    v *= remaining / speed;
                    Write(i, "hspeed", h);
                    Write(i, "vspeed", v);
                }
            }
            var g = i.Scope.ReadOr("gravity").Number;
            var d = i.Scope.ReadOr("gravity_direction").Number * Math.PI / 180;
            if (g != 0)
            {
                h += g * Math.Cos(d);
                v -= g * Math.Sin(d);
                Write(i, "hspeed", h);
                Write(i, "vspeed", v);
            }
            i.Scope.Write("x", i.Scope.ReadOr("x").Number + h);
            i.Scope.Write("y", i.Scope.ReadOr("y").Number + v);
            i.MotionTouched = false;
            i.MotionRest = h == 0 && v == 0 && friction == 0 && g == 0;
        }
        BoundaryEvents();
        Performance.End("motion_boundary", phase);
        phase = Performance.Start();
        long pairProbe = Performance.Start();
        foreach (var i in live)
        {
            if (i.Destroyed || !i.Active)
                continue;
            if (!collisionTargets.TryGetValue(i.Object, out var targets))
            {
                var collected = new HashSet<int>();
                int obj = i.Object;
                while (Objects.TryGetValue(obj, out var definition))
                {
                    foreach (var ev in definition.GetProperty("events").EnumerateArray())
                        if (ev.GetProperty("kind").GetInt32() == 4)
                            collected.Add(ev.GetProperty("sub").GetInt32());
                    foreach (var ev in addedEvents.Keys)
                        if (ev.Object == obj && ev.Kind == 4)
                            collected.Add(ev.Sub);
                    obj = parents.GetValueOrDefault(obj, -1);
                }
                collisionTargets[i.Object] = targets = collected.ToArray();
            }
            foreach (int target in targets)
            {
                if (target < 0 || target >= 100000)
                {
                    foreach (var other in Select(target, new(i)).Where(o => o != i).ToArray())
                    {
                        if (i.Destroyed)
                            break;
                        CollisionCandidateVisits++;
                        CollisionExactTests++;
                        if (
                            !Touches(
                                i,
                                other,
                                i.Scope.ReadOr("x").Number,
                                i.Scope.ReadOr("y").Number
                            )
                        )
                            continue;
                        if (other.Scope.ReadOr("solid").Truth)
                        {
                            Write(i, "x", i.Scope.ReadOr("xprevious"));
                            Write(i, "y", i.Scope.ReadOr("yprevious"));
                        }
                        NotifyCollision(i, other, target);
                        await EventAsync(i, 4, target, other);
                    }
                    continue;
                }
                var snapshot = Snapshot(target, new(i));
                var nearby = nearbyPool.Count > 0 ? nearbyPool.Pop() : new HashSet<GmInstance>();
                var candidates =
                    collisionCandidatePool.Count > 0
                        ? collisionCandidatePool.Pop()
                        : new List<int>();
                int cursor = -1,
                    index = 0;
                long version = -1;
                try
                {
                    while (!i.Destroyed)
                    {
                        EnsureCollisionIndex();
                        if (version != geometryVersion)
                        {
                            Nearby(Bounds(i), nearby);
                            version = geometryVersion;
                            candidates.Clear();
                            index = 0;
                            foreach (var member in nearby)
                                if (
                                    member != i
                                    && snapshot.Order.TryGetValue(member, out int order)
                                    && order > cursor
                                )
                                    candidates.Add(order);
                            candidates.Sort();
                        }
                        if (index >= candidates.Count)
                            break;
                        cursor = candidates[index++];
                        var other = snapshot.Instances[cursor];
                        CollisionCandidateVisits++;
                        CollisionExactTests++;
                        if (
                            !Touches(
                                i,
                                other,
                                i.Scope.ReadOr("x").Number,
                                i.Scope.ReadOr("y").Number
                            )
                        )
                            continue;
                        if (other.Scope.ReadOr("solid").Truth)
                        {
                            Write(i, "x", i.Scope.ReadOr("xprevious"));
                            Write(i, "y", i.Scope.ReadOr("yprevious"));
                        }
                        NotifyCollision(i, other, target);
                        await EventAsync(i, 4, target, other);
                    }
                }
                finally
                {
                    nearby.Clear();
                    nearbyPool.Push(nearby);
                    candidates.Clear();
                    collisionCandidatePool.Push(candidates);
                }
            }
        }
        Performance.End("collision_pairs", pairProbe);
        await RunObjectEventAsync(3, 2);
        NativeServices?.Motions.AfterStep();
        UpdateBackgrounds();
        Performance.End("collision_end", phase);
        phase = Performance.Start();
        RemoveDestroyed();
        Particles.Update(Random);
        PublishFrame();
        Performance.End("draw", phase);
        phase = Performance.Start();
        foreach (var i in LiveSnapshot())
        {
            double speed = i.Scope.ReadOr("image_speed").Number;
            if (speed == 0 && i.AnimationSettled)
                continue;
            int n = SpriteFrameCount(i.Scope.ReadOr("sprite_index", -1).Int);
            var image = i.Scope.ReadOr("image_index");
            double index = image.Number + speed;
            if (n == 1 && speed == 1 && index == 1 && !image.IsString && !HasEvent(i.Object, 7, 7))
            {
                i.AnimationSettled = false;
                continue;
            }
            if (index >= n)
            {
                i.Scope.Write("image_index", index - n);
                i.AnimationSettled = false;
                await EventAsync(i, 7, 7);
            }
            else
            {
                i.Scope.Write("image_index", index);
                i.AnimationSettled = speed == 0;
            }
        }
        RemoveDestroyed();
        if (PendingRoom is int destination)
            await LoadRoomAsync(destination);
        NativeServices?.Maps.Update();
        Performance.End("animation_room", phase);
        StepEndObserver?.Invoke();
        Performance.End("total", total);
        Performance.Report(Frame, CurrentRoom);
    }

    private readonly Dictionary<int, int> spriteFrameCounts = new();
    private readonly List<GmInstance> liveSnapshot = new();

    private List<GmInstance> LiveSnapshot()
    {
        liveSnapshot.Clear();
        foreach (var instance in Instances)
            if (!instance.Destroyed && instance.Active)
                liveSnapshot.Add(instance);
        return liveSnapshot;
    }

    private int SpriteFrameCount(int sprite)
    {
        if (spriteFrameCounts.TryGetValue(sprite, out int count))
            return count;
        count = Sprites.TryGetValue(sprite, out var definition)
            ? definition.GetProperty("frames").GetArrayLength()
            : 0;
        return spriteFrameCounts[sprite] = count;
    }

    private void RemoveDestroyed()
    {
        foreach (var instance in Instances)
            if (
                !instance.Active
                && instance.InactiveEffectExpiry > 0
                && Frame >= instance.InactiveEffectExpiry
            )
                instance.Destroyed = true;
        int previous = Instances.Count;
        int removed = Instances.RemoveAll(i => i.Destroyed);
        if (removed > 0)
            indexedCount = -1;
        if (collisionIndexedCount == previous)
            collisionIndexedCount -= removed;
    }

    public static double Direction(double x1, double y1, double x2, double y2) =>
        (Math.Atan2(y1 - y2, x2 - x1) * 180 / Math.PI + 360) % 360;
}

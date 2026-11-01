using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace SlackOfDescent;

public readonly struct GmDrawCommand
{
    private enum Kind
    {
        Action,
        Texture,
        Rectangle,
        Line,
        Circle,
        Ellipse,
        Triangles,
        Text,
    }

    private readonly Kind kind;
    private readonly Action<GmCanvas>? action;
    private readonly Texture2D? texture;
    private readonly Vector2 origin,
        position,
        scale;
    private readonly Rect2 source;
    private readonly float angle;
    private readonly Color tint;
    private readonly bool filled;
    private readonly Vector2[]? points,
        coords;
    private readonly Color[]? colours;
    private readonly int[]? indices;
    private readonly GmValue[]? arguments;
    private readonly Font? font;
    private readonly string? text;
    private readonly int fontSize;
    private bool Batched { get; init; }
    private int Blend { get; init; }
    private int Filter { get; init; }

    public GmDrawCommand(Action<GmCanvas> action)
    {
        this = default;
        this.action = action;
    }

    public GmDrawCommand(
        Texture2D texture,
        Vector2 origin,
        Rect2 source,
        Vector2 position,
        Vector2 scale,
        float angle,
        Color tint
    )
    {
        this = default;
        kind = Kind.Texture;
        this.texture = texture;
        this.origin = origin;
        this.source = source;
        this.position = position;
        this.scale = scale;
        this.angle = angle;
        this.tint = tint;
    }

    public GmDrawCommand(Rect2 rectangle, Color tint, bool filled = true)
    {
        this = default;
        kind = Kind.Rectangle;
        source = rectangle;
        this.tint = tint;
        this.filled = filled;
    }

    public GmDrawCommand(Vector2 start, Vector2 end, Color tint, float width = -1)
    {
        this = default;
        kind = Kind.Line;
        position = start;
        origin = end;
        this.tint = tint;
        angle = width;
    }

    public GmDrawCommand(Vector2 centre, float radius, Color tint, bool filled)
    {
        this = default;
        kind = Kind.Circle;
        position = centre;
        angle = radius;
        this.tint = tint;
        this.filled = filled;
    }

    public GmDrawCommand(Vector2 centre, Vector2 radius, Color tint, GmValue[] arguments)
    {
        this = default;
        kind = Kind.Ellipse;
        position = centre;
        scale = radius;
        this.tint = tint;
        this.arguments = arguments;
    }

    public GmDrawCommand(
        int[] indices,
        Vector2[] points,
        Color[] colours,
        Vector2[] coords,
        Texture2D? texture
    )
    {
        this = default;
        kind = Kind.Triangles;
        this.indices = indices;
        this.points = points;
        this.colours = colours;
        this.coords = coords;
        this.texture = texture;
    }

    public GmDrawCommand(
        Font font,
        string text,
        int fontSize,
        Vector2 position,
        float offset,
        Vector2 scale,
        float angle,
        Color tint
    )
    {
        this = default;
        kind = Kind.Text;
        this.font = font;
        this.text = text;
        this.fontSize = fontSize;
        this.position = position;
        origin = new(offset, 0);
        this.scale = scale;
        this.angle = angle;
        this.tint = tint;
    }

    public GmDrawCommand InBatch(int blend, int filter) =>
        this with
        {
            Batched = true,
            Blend = blend,
            Filter = filter,
        };

    public void Execute(GmCanvas canvas)
    {
        if (Batched)
        {
            canvas.Batch(this with { Batched = false }, Blend, Filter);
            return;
        }
        switch (kind)
        {
            case Kind.Action:
                canvas.ResetDrawTransform();
                action?.Invoke(canvas);
                break;
            case Kind.Texture:
                canvas.SetDrawTransform(position, angle, scale);
                canvas.DrawTextureRectRegion(
                    texture!,
                    new Rect2(-origin, source.Size),
                    source,
                    tint
                );
                break;
            case Kind.Rectangle:
                canvas.ResetDrawTransform();
                canvas.DrawRect(source, tint, filled);
                break;
            case Kind.Line:
                canvas.ResetDrawTransform();
                canvas.DrawLine(position, origin, tint, angle);
                break;
            case Kind.Circle:
                canvas.ResetDrawTransform();
                canvas.DrawCircle(position, angle, tint, filled);
                break;
            case Kind.Ellipse:
                canvas.ResetDrawTransform();
                canvas.DrawEllipse(
                    position,
                    scale,
                    tint,
                    arguments!.Length <= 6 || arguments[6].Number < 0.5
                );
                break;
            case Kind.Triangles:
                canvas.ResetDrawTransform();
                RenderingServer.CanvasItemAddTriangleArray(
                    canvas.GetCanvasItem(),
                    indices!,
                    points!,
                    colours!,
                    coords!,
                    texture: texture?.GetRid() ?? default
                );
                break;
            case Kind.Text:
                canvas.SetDrawTransform(position, angle, scale);
                canvas.DrawString(
                    font!,
                    new(origin.X, font!.GetAscent(fontSize)),
                    text!,
                    HorizontalAlignment.Left,
                    -1,
                    fontSize,
                    tint
                );
                canvas.ResetDrawTransform();
                break;
        }
    }
}

public partial class GmCanvas : Node2D
{
    public readonly List<GmDrawCommand> Commands = new();
    public Action<GmCanvas>? Paint;
    private readonly List<GmDrawCommand> drawing = new();
    private Vector2[]? ellipsePoints;
    private readonly List<GmCanvas> batches = new();
    private readonly Dictionary<int, Material> materials = new();
    private BackBufferCopy? screenCopy;
    private int batchIndex;
    private int lastBlend = -1;
    private int lastFilter = -1;


    private bool transformDirty;
    internal bool RedrawQueued;
    public static long TransformSetCalls, TransformResetCalls, RedrawCalls;

    internal void SetDrawTransform(Vector2 position, float angle, Vector2 scale)
    {
        DrawSetTransform(position, angle, scale);
        transformDirty = true;
        TransformSetCalls++;
    }

    internal void ResetDrawTransform()
    {
        if (!transformDirty)
            return;
        DrawSetTransform(Vector2.Zero);
        transformDirty = false;
        TransformResetCalls++;
    }

    private void Queue()
    {
        if (RedrawQueued)
            return;
        RedrawQueued = true;
        RedrawCalls++;
        QueueRedraw();
    }

    internal void DrawEllipse(Vector2 centre, Vector2 radius, Color tint, bool filled)
    {
        ellipsePoints ??= new Vector2[65];
        for (int i = 0; i < ellipsePoints.Length; i++)
            ellipsePoints[i] =
                centre
                + new Vector2(
                    Mathf.Cos(i * Mathf.Tau / 64) * radius.X,
                    Mathf.Sin(i * Mathf.Tau / 64) * radius.Y
                );
        if (filled)
            DrawColoredPolygon(ellipsePoints, tint);
        else
            DrawPolyline(ellipsePoints, tint);
    }

    public void StartBatches()
    {
        batchIndex = 0;
        lastBlend = -1;
        foreach (var batch in batches)
        {
            batch.Visible = false;
            batch.RedrawQueued = false;
            batch.Commands.Clear();
            if (batch.screenCopy is not null)
                batch.screenCopy.Visible = false;
        }
    }

    public void Batch(Action<GmCanvas> command, int blend, int filter = 0) =>
        Batch(new GmDrawCommand(command), blend, filter);

    public void Batch(GmDrawCommand command, int blend, int filter = 0)
    {
        if (batchIndex > 0 && lastBlend == blend && lastFilter == filter && blend is not (5 or 6))
        {
            batches[batchIndex - 1].Commands.Add(command);
            batches[batchIndex - 1].Queue();
            return;
        }
        lastBlend = blend;
        lastFilter = filter;
        GmCanvas batch;
        if (batchIndex < batches.Count)
            batch = batches[batchIndex];
        else
        {
            batch = new GmCanvas();
            batches.Add(batch);
            AddChild(batch);
        }
        batchIndex++;
        batch.Visible = true;
        batch.TextureFilter =
            filter == 2 ? TextureFilterEnum.Linear
            : filter == 1 ? TextureFilterEnum.Nearest
            : TextureFilterEnum.ParentNode;
        if (!materials.TryGetValue(blend, out var material))
        {
            if (blend is 4 or 5 or 6)
            {
                string expression =
                    blend == 5
                        ? "source * destination.a + destination * (1.0 - destination.a)"
                        : "source * destination + destination";
                string code =
                    blend == 4
                        ? "shader_type canvas_item; render_mode unshaded, blend_disabled;"
                        : "shader_type canvas_item; render_mode unshaded, blend_disabled; uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest; varying vec4 vertex_tint; void vertex() { vertex_tint = COLOR; } void fragment() { vec4 source = vertex_tint * texture(TEXTURE, UV); vec4 destination = texture(screen_texture, SCREEN_UV); COLOR = "
                            + expression
                            + "; }";
                var shader = new Shader { Code = code };
                material = new ShaderMaterial { Shader = shader };
            }
            else
                material = new CanvasItemMaterial
                {
                    BlendMode = blend switch
                    {
                        1 => CanvasItemMaterial.BlendModeEnum.Add,
                        2 => CanvasItemMaterial.BlendModeEnum.Mul,
                        3 => CanvasItemMaterial.BlendModeEnum.Sub,
                        4 => CanvasItemMaterial.BlendModeEnum.PremultAlpha,
                        _ => CanvasItemMaterial.BlendModeEnum.Mix,
                    },
                };
            materials[blend] = material;
        }
        if (blend is 5 or 6)
        {
            if (batch.screenCopy is null)
            {
                batch.screenCopy = new BackBufferCopy
                {
                    CopyMode = BackBufferCopy.CopyModeEnum.Viewport,
                };
                AddChild(batch.screenCopy);
            }
            MoveChild(batch.screenCopy, GetChildCount() - 1);
            batch.screenCopy.Visible = true;
        }
        if (batch.GetIndex() != GetChildCount() - 1)
            MoveChild(batch, GetChildCount() - 1);
        batch.Material = material;
        batch.Commands.Add(command);
        batch.Queue();
    }

    public GmPerformance? Perf;

    public override void _Draw()
    {
        Perf?.BeginDraw("canvas_draw");
        try
        {
            transformDirty = false;
            if (Commands.Count > 0)
                StartBatches();
            Paint?.Invoke(this);
            drawing.Clear();
            drawing.AddRange(Commands);
            try
            {
                foreach (var command in drawing)
                    command.Execute(this);
            }
            finally
            {
                drawing.Clear();
            }
        }
        finally
        {
            ResetDrawTransform();
            if (Perf is GmPerformance perf)
                perf.EndDraw("canvas_draw");
        }
    }
}

public sealed partial class GmGraphics
{
    internal readonly struct DepthDraw
    {
        public readonly double Depth;
        public int Order { get; init; }
        private readonly Action? action;
        private readonly GmParticleSystem? particles;
        private readonly GmInstance? instance;
        private readonly Texture2D? texture;
        private readonly string? path;
        private readonly Rect2 source;
        private readonly Vector2 origin,
            position,
            scale;
        private readonly Color tint;
        private readonly float angle;

        public DepthDraw(double depth, Action action)
        {
            this = default;
            Depth = depth;
            this.action = action;
        }

        public DepthDraw(GmParticleSystem particles)
        {
            this = default;
            Depth = particles.Depth;
            this.particles = particles;
        }

        public DepthDraw(GmInstance instance)
        {
            this = default;
            Depth = instance.Scope.ReadOr("depth").Number;
            this.instance = instance;
        }

        public DepthDraw(
            double depth,
            Texture2D texture,
            Rect2 source,
            Vector2 position,
            Vector2 scale,
            Color tint
        )
        {
            this = default;
            Depth = depth;
            this.texture = texture;
            this.source = source;
            this.position = position;
            this.scale = scale;
            this.tint = tint;
        }

        public DepthDraw(
            double depth,
            GmInstance instance,
            string path,
            Vector2 origin,
            Vector2 position,
            Vector2 scale,
            float angle
        )
        {
            this = default;
            Depth = depth;
            this.instance = instance;
            this.path = path;
            this.origin = origin;
            this.position = position;
            this.scale = scale;
            this.angle = angle;
        }

        public void Execute(GmGraphics graphics)
        {
            if (action is not null)
            {
                using var phase = graphics.world.Performance.MeasureDraw("misc_draw_build");
                action();
                return;
            }

            if (particles is not null)
            {
                using var phase = graphics.world.Performance.MeasureDraw("misc_draw_build");
                graphics.DrawParticles(particles);
                return;
            }

            if (instance is not null)
            {
                using var phase = graphics.world.Performance.MeasureDraw("object_draw_build");
                using var measurement = graphics.world.Performance.Objects.Begin(
                    instance.Object,
                    8
                );

                if (path is not null)
                    graphics.DrawExternal(path, origin, position, scale, angle);
                else
                {
                    var previous = graphics.characterDrawingInstance;
                    graphics.characterDrawingInstance = instance;
                    try
                    {
                        long subTicks = 0,
                            subAlloc = 0;
                        if (buildPhases)
                        {
                            subTicks = (long)Time.GetTicksUsec();
                            subAlloc = System.GC.GetAllocatedBytesForCurrentThread();
                        }
                        bool handled = !instance.Destroyed && graphics.world.Event(instance, 8, 0);
                        if (buildPhases)
                            MarkPhase(8, ref subTicks, ref subAlloc);
                        if (!handled)
                            graphics.Instance(instance);
                        if (buildPhases)
                            MarkPhase(9, ref subTicks, ref subAlloc);
                    }
                    finally
                    {
                        graphics.characterDrawingInstance = previous;
                    }
                }
                return;
            }

            using var texturePhase = graphics.world.Performance.MeasureDraw("misc_draw_build");
            graphics.Textured(texture!, Vector2.Zero, source, position, scale, 0, tint);
        }
    }

    private sealed class DepthDrawBuffer
    {
        public readonly List<DepthDraw> Draws = new();
        public readonly List<GmInstance> Instances = new();
    }

    private readonly List<DepthDrawBuffer> depthDrawBuffers = new();
    private int depthDrawLevel;
    private static readonly IComparer<DepthDraw> depthDrawOrder = Comparer<DepthDraw>.Create(
        static (a, b) =>
        {
            int depth = b.Depth.CompareTo(a.Depth);
            return depth != 0 ? depth : a.Order.CompareTo(b.Order);
        }
    );
    private readonly Node owner;
    private readonly GmWorld world;
    private readonly HashSet<int> mudObjectIds = new();
    private readonly GmTriangleCache triangleCache = new();
    private int triangleCacheRoom = int.MinValue;
    private readonly Dictionary<string, Texture2D> textures = new();
    private ContentRegistry? textureRegistry;
    private readonly Dictionary<int, (SubViewport View, GmCanvas Canvas)> surfaces = new();
    private readonly Dictionary<int, Image> surfacePixels = new();
    private readonly Dictionary<int, byte[]> surfaceData = new();
    private readonly Dictionary<int, ImageTexture> surfaceTextures = new();
    private readonly HashSet<int> dirtySurfaces = new();
    private readonly HashSet<int> dirtySurfaceTextures = new();
    private readonly Dictionary<Texture2D, (byte[] Data, int Width, int Height)> textureData =
        new();
    private readonly List<(
        double Depth,
        Texture2D Texture,
        Rect2 Source,
        Vector2 Position,
        Vector2 Scale,
        Color Tint,
        Rect2 Bounds
    )> tileData = new();
    private int tilesVersion = -1;
    private readonly Dictionary<int, string> backgroundPaths = new();
    private readonly List<GmDrawCommand> main = new();
    private int nextSurface = 1;
    private int target = -1;
    private Color colour = Colors.White;
    private float alpha = 1;
    private int font = -1;
    private int halign;
    private int blend;
    private Vector2 viewSize = new(640, 480);
    public Vector2 Camera;
    private readonly List<Vector2> vertices = new();
    private readonly List<Vector2> uvs = new();
    private readonly List<Color> vertexColours = new();
    private int primitive;
    private Texture2D? primitiveTexture;
    public Func<IEnumerable<(double Depth, Action Draw)>>? AdditionalDraws;
    internal Action<List<DepthDraw>>? AdditionalDrawCommands;

    public void DrawExternal(
        string path,
        Vector2 origin,
        Vector2 position,
        Vector2 scale,
        float angle,
        Color? tint = null
    )
    {
        var texture = Texture(path);
        Textured(
            texture,
            origin,
            new(Vector2.Zero, texture.GetSize()),
            position,
            scale,
            angle,
            tint ?? Colors.White
        );
    }

    private Texture2D[]? particleShapes;
    private ImageTexture? white;

    internal int SpriteOpaquePixels(int id, double frame)
    {
        if (!world.Sprites.TryGetValue(id, out var sprite))
            return 0;
        var frames = sprite.GetProperty("frames");
        if (frames.GetArrayLength() == 0)
            return 0;
        int index =
            ((int)Math.Floor(frame) % frames.GetArrayLength() + frames.GetArrayLength())
            % frames.GetArrayLength();
        var pixels = TexturePixels(Texture(frames[index].GetProperty("path").GetString()!)).Data;
        int count = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] > 0)
                count++;
        return count;
    }

    public object Statistics() =>
        new GmGraphicsStats()
        {
            Surfaces = surfaces.Count,
            Textures = textures.Count,
            PixelCaches = textureData.Count,
            Generated = textures.Keys.Count(k =>
                k.StartsWith("generated/", StringComparison.Ordinal)
            ),
            TriangleCacheEntries = triangleCache.Count,
            TriangleCacheBytes = triangleCache.Bytes,
            TriangleCacheHits = triangleCache.Hits,
            TriangleCacheMisses = triangleCache.Misses,
            Effects = EffectStatistics(),
            CpuFallbacks = effectFallbacks,
            Builds = Builds,
            BuildTotalMs = TotalBuildMilliseconds,
            LastBuildMs = LastBuildMilliseconds,
        };

    private void ReleaseTexture(string path)
    {
        if (
            world.Sprites.Values.Any(s =>
                s.GetProperty("frames")
                    .EnumerateArray()
                    .Any(f => f.GetProperty("path").GetString() == path)
            )
        )
            return;
        foreach (string key in new[] { path, "localization/" + path })
            if (textures.Remove(key, out var texture))
                textureData.Remove(texture);
    }

    private ImageTexture White
    {
        get
        {
            if (white is not null)
                return white;
            using var image = Image.CreateFromData(
                1,
                1,
                false,
                Image.Format.Rgba8,
                [255, 255, 255, 255]
            );
            return white = ImageTexture.CreateFromImage(image);
        }
    }

    public GmGraphics(Node owner, GmWorld world)
    {
        this.owner = owner;
        this.world = world;
        foreach (var name in new[]
        {
            "objMud",
            "objQuicksand",
            "objQuicksandPit",
            "objSwampSurface",
            "objQuicksandNoCol",
            "objQuicksandNoCol2",
            "objSwampSurface2",
            "objSwPlants",
            "objBackMud",
        })
            if (world.Constants.TryGetValue(name, out var id))
                mudObjectIds.Add(id.Int);
        foreach (var background in world.Manifest.GetProperty("backgrounds").EnumerateArray())
            backgroundPaths[background.GetProperty("id").GetInt32()] = background
                .GetProperty("path")
                .GetString()!;
    }

    public static Color Colour(double value, double opacity = 1)
    {
        uint n = (uint)value;
        return new Color(
            (n & 255) / 255f,
            ((n >> 8) & 255) / 255f,
            ((n >> 16) & 255) / 255f,
            (float)Math.Clamp(opacity, 0, 1)
        );
    }

    private Color Tint => new(colour.R, colour.G, colour.B, Math.Clamp(alpha, 0, 1));

    private bool IsMudVisible(GmInstance instance)
    {
        if (!mudObjectIds.Contains(instance.Object))
            return true;
        var position = new Vector2(
            (float)instance.Scope.ReadOr("x").Number,
            (float)instance.Scope.ReadOr("y").Number
        );
        const float halfExtent = 256;
        const float viewportMargin = 128;
        var bounds = new Rect2(
            position - new Vector2(halfExtent, halfExtent),
            new Vector2(halfExtent * 2, halfExtent * 2)
        );
        var viewport = new Rect2(Camera, viewSize).Grow(viewportMargin);
        return viewport.Intersects(bounds);
    }

    private void Add(Action<GmCanvas> command) => Add(new GmDrawCommand(command));

    private void Add(GmDrawCommand command)
    {
        long addTicks = 0,
            addAlloc = 0;
        if (buildPhases)
        {
            addTicks = (long)Time.GetTicksUsec();
            addAlloc = System.GC.GetAllocatedBytesForCurrentThread();
        }
        if (target >= 0 && effects.ContainsKey(target))
            SoftwareEffect(target);
        var draw = command.InBatch(blend, drawFilter);
        if (target < 0)
            main.Add(draw);
        else if (surfaces.TryGetValue(target, out var surface))
        {
            surface.Canvas.Commands.Add(draw);
            surface.Canvas.QueueRedraw();
        }
        if (buildPhases)
            MarkPhase(7, ref addTicks, ref addAlloc);
    }

    public static double CaptureSpriteMs;

    private static readonly bool buildPhases =
        System.Environment.GetEnvironmentVariable("SLACK_BUILD_PHASES") == "1";
    private static readonly long[] phaseTicks = new long[10];
    private static readonly long[] phaseAlloc = new long[10];
    private static readonly string[] phaseNames =
    [
        "setup_bg",
        "collect",
        "wrap",
        "tiles_order",
        "sort",
        "dispatch",
        "fg_bg",
        "add_cmd",
        "event",
        "auto_inst",
    ];
    private static long phaseFrames;

    private static long phaseDispatches, phaseCollected, phaseCommands;
    private static readonly System.Collections.Generic.Dictionary<
        string,
        (long Ticks, long Alloc, long Calls)
    > callStats = new();
    private static readonly string[] callTop = new string[6];

    internal static bool PhaseStats => buildPhases;

    internal static void RecordCall(string name, long ticks, long alloc)
    {
        var previous = callStats.TryGetValue(name, out var value) ? value : default;
        callStats[name] = (previous.Ticks + ticks, previous.Alloc + alloc, previous.Calls + 1);
    }

    private static void MarkPhase(int phase, ref long ticks, ref long alloc)
    {
        long now = (long)Time.GetTicksUsec();
        long bytes = System.GC.GetAllocatedBytesForCurrentThread();
        phaseTicks[phase] += now - ticks;
        phaseAlloc[phase] += bytes - alloc;
        ticks = now;
        alloc = bytes;
    }

    private static void ReportPhases(double buildMs)
    {
        SampleCanvasCounters();
        phaseFrames++;
        phaseDispatches += lastDispatches;
        phaseCollected += lastCollected;
        phaseCommands += lastCommands;
        if (phaseFrames % 30 != 0)
            return;
        var text = new System.Text.StringBuilder(
            "[build-phases] n=" + phaseFrames
                + " build=" + buildMs.ToString("F2") + "ms"
                + " dispatched=" + (phaseDispatches / phaseFrames)
                + " collected=" + (phaseCollected / phaseFrames)
                + " commands=" + (phaseCommands / phaseFrames)
        );
        for (int i = 0; i < phaseNames.Length; i++)
            if (phaseTicks[i] != 0)
                text.Append(' ')
                    .Append(phaseNames[i])
                    .Append('=')
                    .Append((phaseTicks[i] / (double)phaseFrames / 1000.0).ToString("F2"))
                    .Append("ms/")
                    .Append((phaseAlloc[i] / phaseFrames).ToString())
                    .Append("B");
        text.Append(" | canvas tset=")
            .Append(phaseTransformSets / phaseFrames)
            .Append(" treset=")
            .Append(phaseTransformResets / phaseFrames)
            .Append(" redraw=")
            .Append(phaseRedraws / phaseFrames);
        if (callStats.Count > 0)
        {
            var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<
                string,
                (long Ticks, long Alloc, long Calls)
            >>(callStats);
            top.Sort(static (x, y) => y.Value.Alloc.CompareTo(x.Value.Alloc));
            for (int i = 0; i < top.Count && i < 6; i++)
            {
                var (ticks, alloc, calls) = top[i].Value;
                text.Append(" | ")
                    .Append(top[i].Key)
                    .Append(" n=")
                    .Append(calls / phaseFrames)
                    .Append(" us=")
                    .Append(calls == 0 ? 0 : ticks / calls)
                    .Append(" B=")
                    .Append(calls == 0 ? 0 : alloc / calls);
            }
            callStats.Clear();
        }
        GD.Print(text.ToString());
        System.Array.Clear(phaseTicks);
        System.Array.Clear(phaseAlloc);
        phaseDispatches = phaseCollected = phaseCommands = 0;
        phaseTransformSets = phaseTransformResets = phaseRedraws = 0;
        phaseFrames = 0;
    }

    private static int lastDispatches, lastCollected, lastCommands;
    private static long lastTransformSets, lastTransformResets, lastRedraws;
    private static long phaseTransformSets, phaseTransformResets, phaseRedraws;

    private static void SampleCanvasCounters()
    {
        long sets = GmCanvas.TransformSetCalls,
            resets = GmCanvas.TransformResetCalls,
            redraws = GmCanvas.RedrawCalls;
        phaseTransformSets += sets - lastTransformSets;
        phaseTransformResets += resets - lastTransformResets;
        phaseRedraws += redraws - lastRedraws;
        lastTransformSets = sets;
        lastTransformResets = resets;
        lastRedraws = redraws;
    }

    public long Builds { get; private set; }
    public double LastBuildMilliseconds { get; private set; }
    public double TotalBuildMilliseconds { get; private set; }
    private bool building;

    partial void ClearDrawAudit();

    partial void RecordSpriteAudit(
        int id,
        double frame,
        float alpha,
        Vector2 position,
        Vector2 scale
    );

    partial void RecordTextAudit(string text, Vector2 position, Vector2 scale);

    public void Begin()
    {
        main.Clear();
        ClearDrawAudit();
    }

    public void Render(GmCanvas canvas)
    {
        canvas.Transform = Transform2D.Identity;
        if (world.Engine.Read("view_enabled").Truth)
        {
            var view = world.View(0);
            var zero = view.ToScreen(Camera.X, Camera.Y);
            var right = view.ToScreen(Camera.X + 1, Camera.Y);
            var down = view.ToScreen(Camera.X, Camera.Y + 1);
            canvas.Transform = new(
                new((float)(right.X - zero.X), (float)(right.Y - zero.Y)),
                new((float)(down.X - zero.X), (float)(down.Y - zero.Y)),
                new((float)zero.X, (float)zero.Y)
            );
        }
        if (world.Performance.Enabled)
        {
            canvas.Perf = world.Performance;
            world.Performance.BeginDraw("canvas_render");
            try
            {
                canvas.StartBatches();
                foreach (var command in main)
                    command.Execute(canvas);
            }
            finally
            {
                world.Performance.EndDraw("canvas_render");
            }
            return;
        }
        canvas.StartBatches();
        foreach (var command in main)
            command.Execute(canvas);
    }

    public void Build()
    {
        if (building)
            return;
        building = true;
        FlushSprites();
        ulong started = Time.GetTicksUsec();
        long markTicks = buildPhases ? (long)Time.GetTicksUsec() : 0,
            markAlloc = buildPhases ? System.GC.GetAllocatedBytesForCurrentThread() : 0;
        int dispatched = 0,
            collected = 0;
        try
        {
            using var measurement = world.Performance.Objects.Begin(-2, 8);

            Begin();

            Camera = world.Engine.ReadOr("view_enabled").Truth
                ? new(
                    (float)world.Engine.Read("view_xview", [0]).Number,
                    (float)world.Engine.Read("view_yview", [0]).Number
                )
                : Vector2.Zero;

            viewSize = world.Engine.ReadOr("view_enabled").Truth
                ? new(
                    (float)world.Engine.Read("view_wview", [0]).Number,
                    (float)world.Engine.Read("view_hview", [0]).Number
                )
                : new(world.Engine.Read("room_width").Int, world.Engine.Read("room_height").Int);

            RoomBackgrounds(false);

            if (depthDrawLevel == depthDrawBuffers.Count)
                depthDrawBuffers.Add(new());
            var buffer = depthDrawBuffers[depthDrawLevel++];
            var draws = buffer.Draws;
            if (buildPhases)
                MarkPhase(0, ref markTicks, ref markAlloc);
            try
            {
                var collectMeasurement = world.Performance.MeasureDraw("build_collect");
                AdditionalDrawCommands?.Invoke(draws);

                if (AdditionalDraws is not null)
                    foreach (var draw in AdditionalDraws())
                        draws.Add(new(draw.Depth, draw.Draw));

                foreach (var system in world.Particles.Systems.Values)
                    if (system.AutoDraw)
                        draws.Add(new(system));

                foreach (var instance in world.Instances)
                    if (
                        !instance.Destroyed
                        && instance.Active
                        && instance.Scope.ReadOr("visible").Truth
                        && IsMudVisible(instance)
                    )
                        buffer.Instances.Add(instance);
                if (buildPhases)
                {
                    collected = buffer.Instances.Count;
                    MarkPhase(1, ref markTicks, ref markAlloc);
                }

                foreach (var instance in buffer.Instances)
                    draws.Add(new(instance));
                if (buildPhases)
                    MarkPhase(2, ref markTicks, ref markAlloc);

                if (tilesVersion != world.TilesVersion)
                {
                    tileData.Clear();
                    tilesVersion = world.TilesVersion;
                    foreach (var tile in world.Tiles)
                    {
                        int depth = tile.GetProperty("depth").GetInt32();
                        if (world.HiddenTileLayers.Contains(depth))
                            continue;

                        var source = new Rect2(
                            (float)tile.GetProperty("sourceX").GetDouble(),
                            (float)tile.GetProperty("sourceY").GetDouble(),
                            (float)tile.GetProperty("width").GetDouble(),
                            (float)tile.GetProperty("height").GetDouble()
                        );
                        var position = new Vector2(
                            (float)tile.GetProperty("x").GetDouble(),
                            (float)tile.GetProperty("y").GetDouble()
                        );
                        var scale = new Vector2(
                            (float)tile.GetProperty("xscale").GetDouble(),
                            (float)tile.GetProperty("yscale").GetDouble()
                        );
                        var end = position + source.Size * scale;
                        tileData.Add(
                            (
                                depth,
                                Texture(backgroundPaths[tile.GetProperty("background").GetInt32()]),
                                source,
                                position,
                                scale,
                                Colour(tile.GetProperty("blend").GetDouble()),
                                new(position.Min(end), (end - position).Abs())
                            )
                        );
                    }
                }

                var visible = new Rect2(Camera - Vector2.One, viewSize + Vector2.One * 2);
                foreach (var tile in tileData)
                {
                    if (!visible.Intersects(tile.Bounds, true))
                        continue;
                    draws.Add(
                        new(
                            tile.Depth,
                            tile.Texture,
                            tile.Source,
                            tile.Position,
                            tile.Scale,
                            tile.Tint
                        )
                    );
                }

                for (int i = 0; i < draws.Count; i++)
                    draws[i] = draws[i] with { Order = i };

                collectMeasurement.Dispose();
                if (buildPhases)
                    MarkPhase(3, ref markTicks, ref markAlloc);

                using (world.Performance.MeasureDraw("depth_sort"))
                    draws.Sort(depthDrawOrder);
                if (buildPhases)
                    MarkPhase(4, ref markTicks, ref markAlloc);

                if (buildPhases)
                    dispatched = draws.Count;
                foreach (var draw in draws)
                    draw.Execute(this);
                if (buildPhases)
                    MarkPhase(5, ref markTicks, ref markAlloc);

                using (world.Performance.MeasureDraw("misc_draw_build"))
                    RoomBackgrounds(true);
                if (buildPhases)
                    MarkPhase(6, ref markTicks, ref markAlloc);
            }
            finally
            {
                draws.Clear();
                buffer.Instances.Clear();
                depthDrawLevel--;
            }
        }
        finally
        {
            building = false;
            Builds++;
            LastBuildMilliseconds = (Time.GetTicksUsec() - started) / 1000.0;
            TotalBuildMilliseconds += LastBuildMilliseconds;
            world.Performance.BuildCompleted(LastBuildMilliseconds);
            if (buildPhases)
            {
                lastDispatches = dispatched;
                lastCollected = collected;
                lastCommands = main.Count;
                ReportPhases(LastBuildMilliseconds);
            }
        }
    }

    private void Tiled(
        Texture2D texture,
        Vector2 position,
        Vector2 scale,
        Color tint,
        bool horizontal = true,
        bool vertical = true
    )
    {
        var size = texture.GetSize() * scale;
        if (size.X <= 0 || size.Y <= 0)
            return;
        var bounds =
            target < 0 ? new Rect2(Camera, viewSize) : new Rect2(Vector2.Zero, SurfaceSize(target));
        float x1 = horizontal
            ? position.X + MathF.Floor((bounds.Position.X - position.X) / size.X) * size.X
            : position.X;
        float y1 = vertical
            ? position.Y + MathF.Floor((bounds.Position.Y - position.Y) / size.Y) * size.Y
            : position.Y;
        float x2 = horizontal ? bounds.End.X : x1 + size.X,
            y2 = vertical ? bounds.End.Y : y1 + size.Y;
        for (float y = y1; y < y2; y += size.Y)
        for (float x = x1; x < x2; x += size.X)
            Textured(
                texture,
                Vector2.Zero,
                new(Vector2.Zero, texture.GetSize()),
                new(x, y),
                scale,
                0,
                tint
            );
    }

    private void RoomBackgrounds(bool foreground)
    {
        int savedBlend = blend;
        blend = 0;
        for (int index = 0; index < 8; index++)
        {
            GmValue Read(string name) => world.Engine.Read("background_" + name, [index]);
            if (
                !Read("visible").Truth
                || Read("foreground").Truth != foreground
                || !backgroundPaths.TryGetValue(Read("index").Int, out var path)
            )
                continue;
            Tiled(
                Texture(path),
                new((float)Read("x").Number, (float)Read("y").Number),
                new((float)Read("xscale").Number, (float)Read("yscale").Number),
                Colour(Read("blend").Number, Read("alpha").Number),
                Read("htiled").Truth,
                Read("vtiled").Truth
            );
        }
        blend = savedBlend;
    }

    private sealed class SpriteFrames
    {
        public int Hash;
        public int Count;
        public Vector2 Origin;
        public Texture2D?[] Textures = System.Array.Empty<Texture2D?>();
    }

    private readonly Dictionary<int, SpriteFrames> spriteFrames = new();

    private bool SpriteFrame(
        int id,
        double frame,
        out Texture2D texture,
        out Vector2 origin
    )
    {
        texture = null!;
        origin = default;
        if (!world.Sprites.TryGetValue(id, out var element))
            return false;
        var frames = element.GetProperty("frames");
        int count = frames.GetArrayLength();
        int hash = element.GetHashCode();
        int originX = element.GetProperty("originX").GetInt32(),
            originY = element.GetProperty("originY").GetInt32();
        if (
            !spriteFrames.TryGetValue(id, out var entry)
            || entry.Hash != hash
            || entry.Count != count
            || entry.Origin.X != originX
            || entry.Origin.Y != originY
        )
        {
            entry = new SpriteFrames
            {
                Hash = hash,
                Count = count,
                Textures = count == 0 ? System.Array.Empty<Texture2D?>() : new Texture2D?[count],
                Origin = new(originX, originY),
            };
            spriteFrames[id] = entry;
        }
        if (count == 0)
            return false;
        int index = ((int)Math.Floor(frame) % count + count) % count;
        var cached = entry.Textures[index];
        if (cached is null)
            cached = entry.Textures[index] = Texture(frames[index].GetProperty("path").GetString()!);
        texture = cached;
        origin = entry.Origin;
        return true;
    }

    private Texture2D Texture(string path)
    {
        path = NativeLocalization.AssetPath(path);
        if (textures.TryGetValue(path, out var texture))
            return texture;
        using var image = Image.LoadFromFile(Path.Combine(world.Root, path));
        return textures[path] = ImageTexture.CreateFromImage(image);
    }

    internal void TrackNativeTextures(ContentRegistry registry)
    {
        if (textureRegistry == registry)
            return;
        ReleaseNativeTextures();
        textureRegistry = registry;
        registry.ResourcesCommitted += InvalidateNativeTextures;
        owner.TreeExiting += ReleaseNativeTextures;
    }

    private void InvalidateNativeTextures(IReadOnlyCollection<string> paths)
    {
        foreach (string path in paths)
            if (Path.IsPathRooted(path) && textures.Remove(Path.GetFullPath(path), out var texture))
                textureData.Remove(texture);
    }

    private void ReleaseNativeTextures()
    {
        if (textureRegistry is null)
            return;
        textureRegistry.ResourcesCommitted -= InvalidateNativeTextures;
        textureRegistry = null;
        owner.TreeExiting -= ReleaseNativeTextures;
    }

    private void DrawParticles(GmParticleSystem system)
    {
        int savedBlend = blend;
        IEnumerable<GmParticle> particles = system.OldToNew
            ? system.Particles
            : system.Particles.AsEnumerable().Reverse();
        foreach (var p in particles)
        {
            if (!world.Particles.Types.TryGetValue(p.Type, out var t))
                continue;
            blend = t.Additive ? 1 : 0;
            double size = p.Size + t.SizeWiggle * GmParticles.Wiggle(p.Timer, p.RandomStart, 1, 16);
            float angle = -Mathf.DegToRad(
                (float)(
                    p.Angle
                    + t.AngleWiggle * GmParticles.Wiggle(p.Timer, p.RandomStart, 2, 16)
                    + (t.RelativeAngle ? p.Direction : 0)
                )
            );
            var scale = new Vector2((float)(size * t.XScale), (float)(size * t.YScale));
            var position = new Vector2((float)(system.X + p.X), (float)(system.Y + p.Y));
            var tint = Colour(p.Colour, p.Alpha);
            if (t.Sprite >= 0)
            {
                int frame = p.Subimage;
                if (t.Animated)
                    frame +=
                        t.Stretched
                        && p.Lifetime > 0
                        && world.Sprites.TryGetValue(t.Sprite, out var sprite)
                            ? sprite.GetProperty("frames").GetArrayLength() * p.Timer / p.Lifetime
                            : p.Timer;
                Sprite(t.Sprite, frame, position, scale, angle, tint);
            }
            else if (t.Shape >= 0 && t.Shape < 14)
            {
                if (particleShapes is null)
                {
                    var atlas = File.ReadAllBytes(Path.Combine(world.Root, "particles.bin"));
                    particleShapes = new Texture2D[14];
                    for (int shape = 0; shape < 14; shape++)
                    {
                        var rgba = new byte[64 * 64 * 4];
                        Array.Fill(rgba, (byte)255);
                        for (int pixel = 0; pixel < 64 * 64; pixel++)
                            rgba[pixel * 4 + 3] = atlas[shape * 64 * 64 + pixel];
                        using var image = Image.CreateFromData(
                            64,
                            64,
                            false,
                            Image.Format.Rgba8,
                            rgba
                        );
                        particleShapes[shape] = ImageTexture.CreateFromImage(image);
                    }
                }
                Textured(
                    particleShapes[t.Shape],
                    new(32, 32),
                    new(0, 0, 64, 64),
                    position,
                    scale,
                    angle,
                    tint
                );
            }
        }
        blend = savedBlend;
    }

    private static readonly bool captureProbe =
        System.Environment.GetEnvironmentVariable("SLACK_CAPTURE_PROBE") == "1";
    private static readonly long[] captureTicks = new long[8];
    private static readonly long[] captureBytes = new long[8];
    private static readonly long[] captureCounts = new long[8];
    private static readonly string[] captureStages =
    [
        "getregion",
        "getdata",
        "prepare",
        "imagetexture",
        "mask",
        "jsondoc",
        "jsonupdate",
        "other",
    ];
    private static long captureCalls, capturePixels, captureMaskMismatch;

    private static void CaptureStage(int stage, ref long ticks, ref long bytes)
    {
        long now = (long)Time.GetTicksUsec(),
            allocated = System.GC.GetAllocatedBytesForCurrentThread();
        captureTicks[stage] += now - ticks;
        captureBytes[stage] += allocated - bytes;
        ticks = now;
        bytes = allocated;
    }

    private static void ReportCapture()
    {
        if (!captureProbe || captureCalls == 0)
            return;
        var text = new System.Text.StringBuilder(
            "[capture] calls=" + captureCalls + " pixels=" + capturePixels + " totalB="
        );
        long total = 0;
        for (int i = 0; i < captureStages.Length; i++)
            total += captureBytes[i];
        text.Append(total).Append(" maskMismatch=").Append(captureMaskMismatch);
        for (int i = 0; i < captureStages.Length; i++)
            if (captureCounts[i] != 0 || captureBytes[i] != 0)
                text.Append(' ')
                    .Append(captureStages[i])
                    .Append('=')
                    .Append((captureTicks[i] / 1000.0).ToString("F1"))
                    .Append("ms/")
                    .Append(captureBytes[i])
                    .Append('B');
        GD.Print(text.ToString());
        System.Array.Clear(captureTicks);
        System.Array.Clear(captureBytes);
        System.Array.Clear(captureCounts);
        captureCalls = 0;
        capturePixels = 0;
    }

    private sealed class SpriteNodeCache
    {
        public JsonNode Node = null!;
        public int Hash;
        public int Frames;
    }

    private readonly Dictionary<int, SpriteNodeCache> spriteNodes = new();
    private readonly HashSet<int> spriteDirty = new();
    private static readonly bool deferSpriteUpdates =
        System.Environment.GetEnvironmentVariable("SLACK_NO_SPRITE_DEFER") != "1";

    private void FlushSprites()
    {
        if (spriteDirty.Count == 0)
            return;
        foreach (int id in spriteDirty)
            if (spriteNodes.TryGetValue(id, out var cached))
            {
                var built = JsonSerializer.SerializeToElement(cached.Node);
                world.Sprites[id] = built;
                cached.Hash = built.GetHashCode();
            }
        spriteDirty.Clear();
        world.InvalidateCollisionSprites();
    }

    private static void MaskStats(
        byte[] rgba,
        int width,
        int height,
        out int left,
        out int right,
        out int top,
        out int bottom
    )
    {
        left = width - 1;
        right = 0;
        top = height - 1;
        bottom = 0;
        bool found = false;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (rgba[(y * width + x) * 4 + 3] == 0)
                continue;
            if (!found)
            {
                left = right = x;
                top = bottom = y;
                found = true;
                continue;
            }
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }
    }

    private static string MaskData(byte[] rgba, int width, int height) =>
        string.Create(
            width * height,
            (Pixels: rgba, Width: width),
            static (span, state) =>
            {
                int index = 3;
                for (int i = 0; i < span.Length; i++, index += 4)
                    span[i] = state.Pixels[index] != 0 ? '1' : '0';
            }
        );

    private int CaptureSprite(
        Image source,
        Rect2I region,
        int transparency,
        bool smooth,
        int originX,
        int originY,
        int? append = null
    )
    {
        long stageTicks = 0,
            stageBytes = 0;
        if (captureProbe)
        {
            stageTicks = (long)Time.GetTicksUsec();
            stageBytes = System.GC.GetAllocatedBytesForCurrentThread();
            captureCalls++;
        }
        int x = Math.Max(0, region.Position.X),
            y = Math.Max(0, region.Position.Y);
        int width = Math.Min(region.Size.X, source.GetWidth() - x),
            height = Math.Min(region.Size.Y, source.GetHeight() - y);
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Empty sprite capture region");
        using var image = source.GetRegion(new(x, y, width, height));
        image.Convert(Image.Format.Rgba8);
        if (captureProbe)
        {
            capturePixels += (long)width * height;
            CaptureStage(0, ref stageTicks, ref stageBytes);
        }
        var pixels = image.GetData();
        if (captureProbe)
            CaptureStage(1, ref stageTicks, ref stageBytes);
        GmPixels.Prepare(pixels, width, height, transparency, smooth);
        if (captureProbe)
            CaptureStage(2, ref stageTicks, ref stageBytes);
        using var prepared = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
        var path = "generated/" + Guid.NewGuid().ToString("N");
        textures[path] = ImageTexture.CreateFromImage(prepared);
        if (captureProbe)
            CaptureStage(3, ref stageTicks, ref stageBytes);
        MaskStats(pixels, width, height, out int maskLeft, out int maskRight, out int maskTop, out int maskBottom);
        string maskData = MaskData(pixels, width, height);
        if (captureProbe && System.Environment.GetEnvironmentVariable("SLACK_CAPTURE_CHECK") == "1")
        {
            var reference = GmPixels.Mask(pixels, width, height);
            bool same =
                reference.GetProperty("left").GetInt32() == maskLeft
                && reference.GetProperty("right").GetInt32() == maskRight
                && reference.GetProperty("top").GetInt32() == maskTop
                && reference.GetProperty("bottom").GetInt32() == maskBottom
                && reference.GetProperty("data").GetString() == maskData;
            if (!same)
                captureMaskMismatch++;
        }
        var mask = new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["left"] = maskLeft,
            ["right"] = maskRight,
            ["top"] = maskTop,
            ["bottom"] = maskBottom,
            ["data"] = maskData,
        };
        if (captureProbe)
            CaptureStage(4, ref stageTicks, ref stageBytes);
        if (append is int id)
        {
            var current = world.Sprites[id];
            int currentHash = current.GetHashCode();
            int currentFrames = current.GetProperty("frames").GetArrayLength();
            if (
                !spriteNodes.TryGetValue(id, out var cached)
                || cached.Hash != currentHash
                || cached.Frames != currentFrames
            )
            {
                cached = new SpriteNodeCache
                {
                    Node = JsonNode.Parse(current.GetRawText())!,
                    Hash = currentHash,
                    Frames = currentFrames,
                };
                spriteNodes[id] = cached;
            }
            if (captureProbe)
                CaptureStage(5, ref stageTicks, ref stageBytes);
            cached.Node["frames"]!.AsArray().Add(
                new JsonObject
                {
                    ["path"] = path,
                    ["width"] = width,
                    ["height"] = height,
                }
            );
            cached.Node["masks"]!.AsArray().Add(mask);
            cached.Frames = currentFrames + 1;
            if (deferSpriteUpdates)
                spriteDirty.Add(id);
            else
            {
                var built = JsonSerializer.SerializeToElement(cached.Node);
                world.Sprites[id] = built;
                cached.Hash = built.GetHashCode();
                world.InvalidateCollisionSprites();
            }
            if (captureProbe)
            {
                CaptureStage(6, ref stageTicks, ref stageBytes);
                if (captureCalls % 64 == 0)
                    ReportCapture();
            }
            return id;
        }
        int registered = world.RegisterSprite(
            JsonSerializer.SerializeToElement(
                new JsonObject
                {
                    ["originX"] = originX,
                    ["originY"] = originY,
                    ["frames"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["path"] = path,
                            ["width"] = width,
                            ["height"] = height,
                        },
                    },
                    ["masks"] = new JsonArray { mask },
                }
            )
        );
        if (captureProbe)
        {
            CaptureStage(6, ref stageTicks, ref stageBytes);
            if (captureCalls % 64 == 0)
                ReportCapture();
        }
        return registered;
    }

    private void Textured(
        Texture2D texture,
        Vector2 origin,
        Rect2 source,
        Vector2 position,
        Vector2 scale,
        float angle,
        Color tint
    )
    {
        if (target >= 0 && effects.TryGetValue(target, out var effect))
        {
            var transform = new Transform2D(angle, scale, 0, position);
            var a = transform * -origin;
            var b = transform * (new Vector2(source.Size.X, 0) - origin);
            var c = transform * (source.Size - origin);
            var d = transform * (new Vector2(0, source.Size.Y) - origin);
            if (
                texture == White
                && tint.A >= 1
                && blend is 0 or 4
                && angle == 0
                && a.X <= 0
                && a.Y <= 0
                && c.X >= effect.Logical.X
                && c.Y >= effect.Logical.Y
            )
            {
                effect.Reset();
                c.Y = d.Y = effect.View.Size.Y * effect.Divisor;
                c.X = b.X = effect.View.Size.X * effect.Divisor;
            }
            var size = texture.GetSize();
            var uv = source.Position / size;
            var end = source.End / size;
            effect.Triangle(texture, blend, a, b, c, tint, tint, tint, uv, new(end.X, uv.Y), end);
            effect.Triangle(texture, blend, a, c, d, tint, tint, tint, uv, end, new(uv.X, end.Y));
            return;
        }
        if (target >= 0 && surfacePixels.TryGetValue(target, out var pixels))
        {
            var data = TexturePixels(texture);
            Composite(
                surfaceData[target],
                pixels.GetWidth(),
                pixels.GetHeight(),
                data.Data,
                data.Width,
                data.Height,
                origin,
                source,
                position,
                scale,
                angle,
                tint
            );
            DirtySurface(target);
            return;
        }
        var camera = target < 0 ? Camera : Vector2.Zero;
        if (target < 0)
        {
            var transform = new Transform2D(angle, scale, 0, position - camera);
            var a = transform * -origin;
            var b = transform * (new Vector2(source.Size.X, 0) - origin);
            var c = transform * (source.Size - origin);
            var d = transform * (new Vector2(0, source.Size.Y) - origin);
            if (
                Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) < 0
                || Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) < 0
                || Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) > viewSize.X
                || Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) > viewSize.Y
            )
                return;
        }
        Add(new GmDrawCommand(texture, origin, source, position - camera, scale, angle, tint));
    }

    private Image SurfaceImage(int id)
    {
        SoftwareEffect(id);
        var image = surfacePixels[id];
        if (dirtySurfaces.Remove(id))
            image.SetData(
                image.GetWidth(),
                image.GetHeight(),
                false,
                Image.Format.Rgba8,
                surfaceData[id]
            );
        return image;
    }

    private void DirtySurface(int id)
    {
        dirtySurfaces.Add(id);
        dirtySurfaceTextures.Add(id);
    }

    private Texture2D SurfaceTexture(int id)
    {
        if (effects.TryGetValue(id, out var effect))
        {
            effect.Schedule();
            return surfaces[id].View.GetTexture();
        }
        bool changed = dirtySurfaceTextures.Remove(id);
        var image = SurfaceImage(id);
        if (!surfaceTextures.TryGetValue(id, out var texture))
            surfaceTextures[id] = texture = ImageTexture.CreateFromImage(image);
        else if (changed)
        {
            texture.Update(image);
            textureData.Remove(texture);
        }
        return texture;
    }

    private void Composite(
        byte[] destination,
        int width,
        int height,
        byte[] image,
        int imageWidth,
        int imageHeight,
        Vector2 origin,
        Rect2 source,
        Vector2 position,
        Vector2 scale,
        float angle,
        Color tint
    )
    {
        if (scale.X == 0 || scale.Y == 0)
            return;
        var transform = new Transform2D(angle, scale, 0, position);
        var inverse = transform.AffineInverse();
        var corners = new[]
        {
            -origin,
            new Vector2(source.Size.X, 0) - origin,
            source.Size - origin,
            new Vector2(0, source.Size.Y) - origin,
        }
            .Select(p => transform * p)
            .ToArray();
        int left = Math.Max(0, (int)Math.Floor(corners.Min(p => p.X))),
            right = Math.Min(width - 1, (int)Math.Ceiling(corners.Max(p => p.X)));
        int top = Math.Max(0, (int)Math.Floor(corners.Min(p => p.Y))),
            bottom = Math.Min(height - 1, (int)Math.Ceiling(corners.Max(p => p.Y)));
        if (imageWidth == 1 && imageHeight == 1 && angle == 0 && tint.A >= 1 && blend is 0 or 4)
        {
            int x1 = Math.Max(0, (int)Math.Ceiling(corners.Min(p => p.X) - 0.5)),
                x2 = Math.Min(width, (int)Math.Ceiling(corners.Max(p => p.X) - 0.5));
            int y1 = Math.Max(0, (int)Math.Ceiling(corners.Min(p => p.Y) - 0.5)),
                y2 = Math.Min(height, (int)Math.Ceiling(corners.Max(p => p.Y) - 0.5));
            GmPixels.FillRectangle(
                destination,
                width,
                height,
                x1,
                y1,
                x2,
                y2,
                (byte)(Math.Clamp(tint.R, 0, 1) * 255),
                (byte)(Math.Clamp(tint.G, 0, 1) * 255),
                (byte)(Math.Clamp(tint.B, 0, 1) * 255),
                255
            );
            return;
        }
        float xx = inverse.X.X,
            xy = inverse.Y.X,
            yx = inverse.X.Y,
            yy = inverse.Y.Y,
            tx = inverse.Origin.X,
            ty = inverse.Origin.Y;
        if (
            angle == 0
            && scale.X > 0
            && scale.Y > 0
            && origin == Vector2.Zero
            && source.Position == Vector2.Zero
            && source.Size == new Vector2(imageWidth, imageHeight)
            && tint == Colors.White
            && blend == 0
        )
        {
            GmPixels.CompositeNormal(
                destination,
                width,
                height,
                image,
                imageWidth,
                imageHeight,
                left,
                top,
                right + 1,
                bottom + 1,
                xx,
                yy,
                tx,
                ty
            );
            return;
        }
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            float localX = xx * (x + 0.5f) + xy * (y + 0.5f) + tx + origin.X,
                localY = yx * (x + 0.5f) + yy * (y + 0.5f) + ty + origin.Y;
            if (localX < 0 || localY < 0 || localX >= source.Size.X || localY >= source.Size.Y)
                continue;
            int sx = (int)MathF.Floor(source.Position.X + localX),
                sy = (int)MathF.Floor(source.Position.Y + localY);
            if (sx < 0 || sy < 0 || sx >= imageWidth || sy >= imageHeight)
                continue;
            int si = (sy * imageWidth + sx) * 4,
                di = (y * width + x) * 4;
            float sr = image[si] / 255f * tint.R,
                sg = image[si + 1] / 255f * tint.G,
                sb = image[si + 2] / 255f * tint.B,
                sa = image[si + 3] / 255f * tint.A;
            BlendPixel(destination, di, sr, sg, sb, sa);
        }
    }

    private void BlendPixel(
        byte[] destination,
        int di,
        float sr,
        float sg,
        float sb,
        float sa,
        bool round = false
    )
    {
        float dr = destination[di] / 255f,
            dg = destination[di + 1] / 255f,
            db = destination[di + 2] / 255f,
            da = destination[di + 3] / 255f;
        float r,
            g,
            b,
            a;
        switch (blend)
        {
            case 0:
                r = sr * sa + dr * (1 - sa);
                g = sg * sa + dg * (1 - sa);
                b = sb * sa + db * (1 - sa);
                a = sa * sa + da * (1 - sa);
                break;
            case 1:
                r = sr * sa + dr;
                g = sg * sa + dg;
                b = sb * sa + db;
                a = sa * sa + da;
                break;
            case 2:
                r = sr * dr;
                g = sg * dg;
                b = sb * db;
                a = sa * da;
                break;
            case 3:
                r = dr - sr * sa;
                g = dg - sg * sa;
                b = db - sb * sa;
                a = da - sa * sa;
                break;
            case 4:
                r = sr;
                g = sg;
                b = sb;
                a = sa;
                break;
            case 5:
                r = sr * da + dr * (1 - da);
                g = sg * da + dg * (1 - da);
                b = sb * da + db * (1 - da);
                a = sa * da + da * (1 - da);
                break;
            case 6:
                r = sr * dr + dr;
                g = sg * dg + dg;
                b = sb * db + db;
                a = sa * da + da;
                break;
            default:
                throw new NotSupportedException("Surface blend " + blend);
        }
        byte Quantize(float value) =>
            (byte)(
                round ? MathF.Round(Math.Clamp(value, 0, 1) * 255) : Math.Clamp(value, 0, 1) * 255
            );
        destination[di] = Quantize(r);
        destination[di + 1] = Quantize(g);
        destination[di + 2] = Quantize(b);
        destination[di + 3] = Quantize(a);
    }

    private void Triangle(
        Vector2 a,
        Vector2 b,
        Vector2 c,
        Color ca,
        Color cb,
        Color cc,
        Vector2 ua,
        Vector2 ub,
        Vector2 uc,
        Texture2D? texture,
        bool round = false
    )
    {
        var pixels = surfacePixels[target];
        int width = pixels.GetWidth(),
            height = pixels.GetHeight();
        var destination = surfaceData[target];
        if (texture is null && ca == Colors.Black && cb == ca && cc == ca && blend == 0)
        {
            if (triangleCacheRoom != world.CurrentRoom)
            {
                triangleCache.Clear();
                triangleCacheRoom = world.CurrentRoom;
            }
            triangleCache.Fill(destination, width, height, a.X, a.Y, b.X, b.Y, c.X, c.Y);
            DirtySurface(target);
            return;
        }
        float Edge(Vector2 p, Vector2 q, Vector2 point) =>
            (q.X - p.X) * (point.Y - p.Y) - (q.Y - p.Y) * (point.X - p.X);
        float area = Edge(a, b, c);
        if (area == 0)
            return;
        if (area < 0)
        {
            (b, c) = (c, b);
            (cb, cc) = (cc, cb);
            (ub, uc) = (uc, ub);
            area = -area;
        }
        bool TopLeft(Vector2 p, Vector2 q) => p.Y > q.Y || p.Y == q.Y && p.X < q.X;
        bool ab = TopLeft(a, b),
            bc = TopLeft(b, c),
            ac = TopLeft(c, a);
        int left = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))),
            right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int top = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))),
            bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        (byte[] Data, int Width, int Height) source = default;
        if (texture is not null)
            source = TexturePixels(texture);
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            var point = new Vector2(x + 0.5f, y + 0.5f);
            float wa = Edge(b, c, point),
                wb = Edge(c, a, point),
                wc = Edge(a, b, point);
            if (wa < 0 || wa == 0 && !bc || wb < 0 || wb == 0 && !ac || wc < 0 || wc == 0 && !ab)
                continue;
            wa /= area;
            wb /= area;
            wc /= area;
            var tint = ca * wa + cb * wb + cc * wc;
            if (texture is not null)
            {
                var uv = ua * wa + ub * wb + uc * wc;
                int sx =
                        ((int)MathF.Floor(uv.X * source.Width) % source.Width + source.Width)
                        % source.Width,
                    sy =
                        ((int)MathF.Floor(uv.Y * source.Height) % source.Height + source.Height)
                        % source.Height;
                int offset = (sy * source.Width + sx) * 4;
                var texels = source.Data!;
                tint *= new Color(
                    texels[offset] / 255f,
                    texels[offset + 1] / 255f,
                    texels[offset + 2] / 255f,
                    texels[offset + 3] / 255f
                );
            }
            BlendPixel(destination, (y * width + x) * 4, tint.R, tint.G, tint.B, tint.A, round);
        }
        DirtySurface(target);
    }

    private (byte[] Data, int Width, int Height) TexturePixels(Texture2D texture)
    {
        if (!textureData.TryGetValue(texture, out var data))
        {
            using var image = texture.GetImage();
            image.Convert(Image.Format.Rgba8);
            textureData[texture] = data = (image.GetData(), image.GetWidth(), image.GetHeight());
        }
        return data;
    }

    private void Primitive(
        Vector2[] points,
        Color[] colours,
        Vector2[] coords,
        Texture2D? texture,
        int mode
    )
    {
        if (mode == 1)
        {
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                var tint = colours[i];
                if (target >= 0)
                    Textured(White, Vector2.Zero, new(0, 0, 1, 1), point, Vector2.One, 0, tint);
                else
                    Add(new GmDrawCommand(new Rect2(point, Vector2.One), tint));
            }
            return;
        }
        if (mode is 2 or 3)
        {
            for (int i = 0; i + 1 < points.Length; i += mode == 2 ? 2 : 1)
            {
                var a = points[i];
                var b = points[i + 1];
                var tint = colours[i];
                if (target >= 0)
                    Textured(
                        White,
                        new(0, 0.5f),
                        new(0, 0, 1, 1),
                        a,
                        new(a.DistanceTo(b), 1),
                        (b - a).Angle(),
                        tint
                    );
                else
                    Add(new GmDrawCommand(a, b, tint));
            }
            return;
        }
        var indices = GmPixels.Triangles(mode, points.Length);
        if (indices.Length == 0)
            return;
        if (target >= 0)
        {
            if (effects.TryGetValue(target, out var effect))
            {
                for (int n = 0; n < indices.Length; n += 3)
                {
                    int a = indices[n],
                        b = indices[n + 1],
                        c = indices[n + 2];
                    effect.Triangle(
                        texture,
                        blend,
                        points[a],
                        points[b],
                        points[c],
                        colours[a],
                        colours[b],
                        colours[c],
                        coords[a],
                        coords[b],
                        coords[c]
                    );
                }
                return;
            }
            for (int i = 0; i < indices.Length; i += 3)
            {
                int a = indices[i],
                    b = indices[i + 1],
                    c = indices[i + 2];
                bool fast =
                    texture is null
                    && colours[a] == Colors.Black
                    && colours[b] == colours[a]
                    && colours[c] == colours[a]
                    && blend == 0;
                long started = world.Performance.Start();
                try
                {
                    Triangle(
                        points[a],
                        points[b],
                        points[c],
                        colours[a],
                        colours[b],
                        colours[c],
                        coords[a],
                        coords[b],
                        coords[c],
                        texture
                    );
                }
                finally
                {
                    world.Performance.End(
                        fast ? "surface_triangle_opaque" : "surface_triangle_general",
                        started
                    );
                }
            }
        }
        else
            Add(new GmDrawCommand(indices, points, colours, coords, texture));
    }

    public void Sprite(
        int id,
        double frame,
        Vector2 position,
        Vector2 scale,
        float angle,
        Color tint
    )
    {
        if (
            target < 0
            && CharacterRendering?.Replace(characterDrawingInstance, position, scale, angle, tint)
                == true
        )
            return;
        if (!world.Sprites.ContainsKey(id))
            return;
        if (target < 0)
        {
            RecordSpriteAudit(id, frame, tint.A, position, scale);
        }
        if (!SpriteFrame(id, frame, out var t, out var origin))
            return;
        Textured(
            t,
            origin,
            new Rect2(Vector2.Zero, t.GetSize()),
            position,
            scale,
            angle,
            tint
        );
    }

    public void Instance(GmInstance instance)
    {
        var s = instance.Scope;
        Sprite(
            s.ReadOr("sprite_index", -1).Int,
            s.ReadOr("image_index").Number,
            new((float)s.ReadOr("x").Number, (float)s.ReadOr("y").Number),
            new(
                (float)s.ReadOr("image_xscale", 1).Number,
                (float)s.ReadOr("image_yscale", 1).Number
            ),
            -Mathf.DegToRad((float)s.ReadOr("image_angle").Number),
            Colour(s.ReadOr("image_blend", 16777215).Number, s.ReadOr("image_alpha", 1).Number)
        );
    }

    private void Text(
        string text,
        Vector2 position,
        float scaleX = 1,
        float scaleY = 1,
        float rotation = 0,
        float wrapWidth = -1,
        float lineSpacing = -1
    )
    {
        text = NativeLocalization.Text(text);
        var selected = world
            .Manifest.GetProperty("fonts")
            .EnumerateArray()
            .FirstOrDefault(f => f.GetProperty("id").GetInt32() == font);
        if (selected.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Font not selected " + font);
        var texture = Texture(selected.GetProperty("path").GetString()!);
        var map = selected.GetProperty("map");
        int height = selected.GetProperty("size").GetInt32();
        var tint = Tint;
        if (
            NativeLocalization.IsChinese
            && text.Any(ch => ch >= 256)
            && NativeLocalization.Font is { } chinese
        )
        {
            var camera = target < 0 ? Camera : Vector2.Zero;
            IEnumerable<string> Lines()
            {
                foreach (string paragraph in text.Replace('#', '\n').Split('\n'))
                {
                    if (wrapWidth <= 0)
                    {
                        yield return paragraph;
                        continue;
                    }
                    string line = "";
                    foreach (char character in paragraph)
                    {
                        if (
                            line.Length > 0
                            && chinese
                                .GetStringSize(
                                    line + character,
                                    HorizontalAlignment.Left,
                                    -1,
                                    height
                                )
                                .X > wrapWidth
                        )
                        {
                            yield return line;
                            line = "";
                        }
                        line += character;
                    }
                    yield return line;
                }
            }
            foreach (string line in Lines())
            {
                RecordTextAudit(line, position, new(scaleX, scaleY));
                string captured = line;
                Vector2 baseline = position - camera;
                float width = chinese.GetStringSize(line, HorizontalAlignment.Left, -1, height).X;
                float offset =
                    halign == 1 ? -width / 2
                    : halign == 2 ? -width
                    : 0;
                Add(
                    new GmDrawCommand(
                        chinese,
                        captured,
                        height,
                        baseline,
                        offset,
                        new(scaleX, scaleY),
                        rotation,
                        tint
                    )
                );
                position.Y += (lineSpacing < 0 ? height : lineSpacing) * scaleY;
            }
            return;
        }
        foreach (var line in text.Replace('#', '\n').Split('\n'))
        {
            RecordTextAudit(line, position, new(scaleX, scaleY));
            float width = 0;
            foreach (char ch in line)
                if (ch < 256)
                    width += unchecked((int)map[ch * 6 + 4].GetUInt32());
            float x =
                halign == 1 ? -width / 2
                : halign == 2 ? -width
                : 0;
            foreach (char ch in line)
            {
                if (ch >= 256)
                    continue;
                int n = ch * 6;
                var source = new Rect2(
                    map[n].GetSingle(),
                    map[n + 1].GetSingle(),
                    map[n + 2].GetSingle(),
                    map[n + 3].GetSingle()
                );
                var offset = new Vector2(x + unchecked((int)map[n + 5].GetUInt32()), 0);
                if (source.Size.X > 0 && source.Size.Y > 0)
                    Textured(
                        texture,
                        -offset,
                        source,
                        position,
                        new(scaleX, scaleY),
                        rotation,
                        tint
                    );
                x += unchecked((int)map[n + 4].GetUInt32());
            }
            position.Y += height;
        }
    }

    private readonly string? mudExperiment = System.Environment.GetEnvironmentVariable(
        "SLACK_MUD_EXPERIMENT"
    );

    private GmValue[] callArgs = System.Array.Empty<GmValue>();

    public GmValue? Call(string name, GmValue[] a, GmContext context)
    {
        if (spriteDirty.Count > 0 && name != "sprite_add_from_surface")
            FlushSprites();
        var savedArgs = callArgs;
        callArgs = a;
        try
        {
            if (!buildPhases)
                return Dispatch(name, a, context);
            long ticks = (long)Time.GetTicksUsec(),
                alloc = System.GC.GetAllocatedBytesForCurrentThread();
            var result = Dispatch(name, a, context);
            RecordCall(
                name,
                (long)Time.GetTicksUsec() - ticks,
                System.GC.GetAllocatedBytesForCurrentThread() - alloc
            );
            return result;
        }
        finally
        {
            callArgs = savedArgs;
        }
    }

    private double N(int i) => i < callArgs.Length ? callArgs[i].Number : 0;

    private int I(int i) => i < callArgs.Length ? callArgs[i].Int : 0;

    private float F(int i) => (float)N(i);

    private Vector2 P(int i) => new(F(i), F(i + 1));

    private GmValue? Dispatch(string name, GmValue[] a, GmContext context)
    {
        switch (name)
        {
            case "slack_delete_sprite":
            {
                using var deleted = JsonDocument.Parse(a[0].ToString());
                foreach (var frame in deleted.RootElement.GetProperty("frames").EnumerateArray())
                {
                    string path = frame.GetProperty("path").GetString()!;
                    if (path.StartsWith("generated/", StringComparison.Ordinal))
                        ReleaseTexture(path);
                }
                return 0;
            }
            case "part_system_drawit":
                if (world.Particles.Systems.TryGetValue(I(0), out var system))
                    DrawParticles(system);
                return 0;
            case "action_draw_ellipse":
            {
                var p1 = P(0);
                var p2 = P(2);
                if (context.Locals.ReadOr("argument_relative").Truth)
                {
                    var offset = new Vector2(
                        (float)context.Self.Scope.Read("x").Number,
                        (float)context.Self.Scope.Read("y").Number
                    );
                    p1 += offset;
                    p2 += offset;
                }
                return Call(
                    "draw_ellipse_color",
                    [
                        p1.X,
                        p1.Y,
                        p2.X,
                        p2.Y,
                        (int)(colour.R * 255)
                            | ((int)(colour.G * 255) << 8)
                            | ((int)(colour.B * 255) << 16),
                        0,
                        I(4),
                    ],
                    context
                );
            }
            case "slack_prepare_sprite":
            {
                var node = JsonNode.Parse(world.Sprites[I(0)].GetRawText())!;
                var masks = new List<object>();
                foreach (var frame in node["frames"]!.AsArray())
                {
                    var path = frame!["path"]!.GetValue<string>();
                    using var image = Image.LoadFromFile(Path.Combine(world.Root, path));
                    image.Convert(Image.Format.Rgba8);
                    int w = image.GetWidth(),
                        h = image.GetHeight();
                    var pixels = image.GetData();
                    GmPixels.Prepare(pixels, w, h, N(1) >= 0.5 ? 1 : 0, N(2) >= 0.5);
                    using var prepared = Image.CreateFromData(
                        w,
                        h,
                        false,
                        Image.Format.Rgba8,
                        pixels
                    );
                    string generated = "generated/" + Guid.NewGuid().ToString("N");
                    textures[generated] = ImageTexture.CreateFromImage(prepared);
                    frame["path"] = generated;
                    masks.Add(GmPixels.Mask(pixels, w, h));
                }
                node["masks"] = JsonSerializer.SerializeToNode(masks);
                world.Sprites[I(0)] = JsonSerializer.SerializeToElement(node);
                world.InvalidateCollisionSprites();
                return 0;
            }
            case "sprite_create_from_screen":
            {
                int width = (int)world.Engine.Read("view_wport", [0]).Number,
                    height = (int)world.Engine.Read("view_hport", [0]).Number;
                if (width <= 0 || height <= 0)
                {
                    width = world.Engine.Read("room_width").Int;
                    height = world.Engine.Read("room_height").Int;
                }
                using var screen =
                    DisplayServer.GetName() == "headless"
                        ? Image.CreateEmpty(width, height, false, Image.Format.Rgba8)
                        : owner.GetViewport().GetTexture().GetImage();
                if (screen.GetWidth() != width || screen.GetHeight() != height)
                    screen.Resize(width, height, Image.Interpolation.Nearest);
                return CaptureSprite(
                    screen,
                    new(I(0), I(1), I(2), I(3)),
                    I(4),
                    N(5) >= 0.5,
                    I(6),
                    I(7)
                );
            }
            case "sprite_create_from_surface":
            case "sprite_add_from_surface":
            {
                bool append = name == "sprite_add_from_surface";
                int offset = append ? 1 : 0;
                return CaptureSprite(
                    SurfaceImage(I(offset)),
                    new(I(1 + offset), I(2 + offset), I(3 + offset), I(4 + offset)),
                    I(5 + offset),
                    N(6 + offset) >= 0.5,
                    append ? 0 : I(7),
                    append ? 0 : I(8),
                    append ? I(0) : null
                );
            }
            case "action_draw_variable":
            case "action_draw_text":
                Text(a[0].ToString(), P(1));
                return 0;
            case "action_draw_sprite":
                Sprite(I(0), N(3), P(1), Vector2.One, 0, Colors.White);
                return 0;
            case "draw_background_part":
            {
                var b = world
                    .Manifest.GetProperty("backgrounds")
                    .EnumerateArray()
                    .First(b => b.GetProperty("id").GetInt32() == I(0));
                var texture = Texture(b.GetProperty("path").GetString()!);
                Textured(
                    texture,
                    Vector2.Zero,
                    new Rect2(P(1), P(3)),
                    P(5),
                    Vector2.One,
                    0,
                    Colors.White
                );
                return 0;
            }
            case "draw_background_ext":
            case "draw_background_tiled":
            case "draw_background_tiled_ext":
            {
                var b = world
                    .Manifest.GetProperty("backgrounds")
                    .EnumerateArray()
                    .First(b => b.GetProperty("id").GetInt32() == I(0));
                var texture = Texture(b.GetProperty("path").GetString()!);
                var scale = name == "draw_background_tiled" ? Vector2.One : P(3);
                var tint =
                    name == "draw_background_tiled"
                        ? Colors.White
                        : Colour(
                            N(name == "draw_background_ext" ? 6 : 5),
                            N(name == "draw_background_ext" ? 7 : 6)
                        );
                if (name == "draw_background_ext")
                {
                    Textured(
                        texture,
                        Vector2.Zero,
                        new(Vector2.Zero, texture.GetSize()),
                        P(1),
                        scale,
                        -Mathf.DegToRad(F(5)),
                        tint
                    );
                    return 0;
                }
                bool surfaceTarget = target >= 0;
                long started = surfaceTarget ? world.Performance.Start() : 0;
                try
                {
                    Tiled(texture, P(1), scale, tint);
                }
                finally
                {
                    if (surfaceTarget)
                        world.Performance.End("surface_background_tiled", started);
                }
                return 0;
            }
            case "draw_set_blend_mode":
                blend = I(0) switch
                {
                    0 => 0,
                    1 => 1,
                    3 => 3,
                    _ => throw new NotSupportedException("Blend mode " + I(0)),
                };
                return 0;
            case "draw_set_blend_mode_ext":
                blend = (I(0), I(1)) switch
                {
                    (5, 6) => 0,
                    (5, 2) => 1,
                    (1, 3) => 2,
                    (9, 1) => 2,
                    (2, 1) => 4,
                    (7, 8) => 5,
                    (9, 2) => 6,
                    _ => throw new NotSupportedException($"Blend factors {I(0)},{I(1)}"),
                };
                return 0;
            case "draw_set_color":
            case "action_color":
                colour = Colour(N(0));
                return 0;
            case "draw_set_alpha":
                alpha = Math.Clamp(F(0), 0, 1);
                return 0;
            case "draw_set_font":
                font = I(0);
                return 0;
            case "draw_set_halign":
                halign = I(0);
                return 0;
            case "action_font":
                font = I(0);
                halign = I(1);
                return 0;
            case "draw_sprite":
                Sprite(
                    I(0),
                    N(1) < 0 ? context.Self.Scope.ReadOr("image_index").Number : N(1),
                    P(2),
                    Vector2.One,
                    0,
                    Colors.White
                );
                return 0;
            case "draw_sprite_ext":
                if (
                    mudExperiment is "no-sprites" or "no-state"
                    && context.Self.Object == world.Constants["objSwampSurface"].Int
                    && context.EventKind == 8
                )
                    return 0;
                Sprite(
                    I(0),
                    N(1) < 0 ? context.Self.Scope.ReadOr("image_index").Number : N(1),
                    P(2),
                    P(4),
                    -Mathf.DegToRad(F(6)),
                    Colour(N(7), N(8))
                );
                return 0;
            case "draw_sprite_stretched":
            case "draw_sprite_stretched_ext":
            {
                if (!SpriteFrame(I(0), 0, out var texture, out _))
                    return 0;
                Textured(
                    texture,
                    Vector2.Zero,
                    new(Vector2.Zero, texture.GetSize()),
                    P(2),
                    P(4) / texture.GetSize(),
                    0,
                    name.EndsWith("_ext", StringComparison.Ordinal)
                        ? Colour(N(6), N(7))
                        : Colors.White
                );
                return 0;
            }
            case "draw_sprite_part":
            case "draw_sprite_part_ext":
            {
                if (!SpriteFrame(I(0), N(1), out var texture, out _))
                    return 0;
                Textured(
                    texture,
                    Vector2.Zero,
                    new Rect2(P(2), P(4)),
                    P(6),
                    name.EndsWith("_ext", StringComparison.Ordinal) ? P(8) : Vector2.One,
                    0,
                    name.EndsWith("_ext", StringComparison.Ordinal)
                        ? Colour(N(10), N(11))
                        : Colors.White
                );
                return 0;
            }
            case "draw_sprite_general":
            {
                if (!SpriteFrame(I(0), N(1), out var texture, out _))
                    return 0;
                Textured(
                    texture,
                    Vector2.Zero,
                    new Rect2(P(2), P(4)),
                    P(6),
                    P(8),
                    -Mathf.DegToRad(F(10)),
                    Colour(N(11), N(15))
                );
                return 0;
            }
            case "draw_text":
                Text(a[2].ToString(), P(0));
                return 0;
            case "draw_text_ext":
                Text(a[2].ToString(), P(0), wrapWidth: F(4), lineSpacing: F(3));
                return 0;
            case "draw_text_color":
            {
                var previousColour = colour;
                var opacity = alpha;
                colour = Colour(N(3));
                alpha = F(7);
                Text(a[2].ToString(), P(0));
                colour = previousColour;
                alpha = opacity;
                return 0;
            }
            case "draw_text_transformed_color":
            {
                var previousColour = colour;
                var opacity = alpha;
                colour = Colour(N(6));
                alpha = F(10);
                Text(a[2].ToString(), P(0), F(3), F(4), -Mathf.DegToRad(F(5)));
                colour = previousColour;
                alpha = opacity;
                return 0;
            }
            case "draw_rectangle":
            case "draw_rectangle_color":
            {
                var camera = target < 0 ? Camera : Vector2.Zero;
                var p1 = P(0) - camera;
                var p2 = P(2) - camera;
                var tint = name == "draw_rectangle" ? Tint : Colour(N(4), alpha);
                bool filled = N(name == "draw_rectangle" ? 4 : 8) < 0.5;
                if (target >= 0)
                {
                    var bounds = new Rect2(p1, p2 - p1).Abs();
                    if (filled)
                        Textured(
                            White,
                            Vector2.Zero,
                            new(0, 0, 1, 1),
                            bounds.Position,
                            bounds.Size,
                            0,
                            tint
                        );
                    else
                    {
                        Textured(
                            White,
                            Vector2.Zero,
                            new(0, 0, 1, 1),
                            bounds.Position,
                            new(bounds.Size.X + 1, 1),
                            0,
                            tint
                        );
                        Textured(
                            White,
                            Vector2.Zero,
                            new(0, 0, 1, 1),
                            new(bounds.Position.X, bounds.End.Y),
                            new(bounds.Size.X + 1, 1),
                            0,
                            tint
                        );
                        Textured(
                            White,
                            Vector2.Zero,
                            new(0, 0, 1, 1),
                            bounds.Position,
                            new(1, bounds.Size.Y + 1),
                            0,
                            tint
                        );
                        Textured(
                            White,
                            Vector2.Zero,
                            new(0, 0, 1, 1),
                            new(bounds.End.X, bounds.Position.Y),
                            new(1, bounds.Size.Y + 1),
                            0,
                            tint
                        );
                    }
                    return 0;
                }
                Add(new GmDrawCommand(new Rect2(p1, p2 - p1).Abs(), tint, filled));
                return 0;
            }
            case "draw_line_width":
            case "draw_line_width_color":
            {
                var offset = target < 0 ? Camera : Vector2.Zero;
                var p1 = P(0) - offset;
                var p2 = P(2) - offset;
                float width = F(4);
                var tint = name == "draw_line_width" ? Tint : Colour(N(5), alpha);
                Add(new GmDrawCommand(p1, p2, tint, width));
                return 0;
            }
            case "draw_point":
            case "draw_point_color":
            {
                var p = P(0) - (target < 0 ? Camera : Vector2.Zero);
                var tint = name == "draw_point" ? Tint : Colour(N(2), alpha);
                if (target >= 0)
                {
                    SoftwareEffect(target);
                    var image = surfacePixels[target];
                    if (p.X >= 0 && p.Y >= 0 && p.X < image.GetWidth() && p.Y < image.GetHeight())
                    {
                        int offset = ((int)p.Y * image.GetWidth() + (int)p.X) * 4;
                        BlendPixel(surfaceData[target], offset, tint.R, tint.G, tint.B, tint.A);
                        DirtySurface(target);
                    }
                }
                else
                    Add(new GmDrawCommand(new Rect2(p, Vector2.One), tint));
                return 0;
            }
            case "draw_circle":
            case "draw_circle_color":
            {
                var p = P(0) - (target < 0 ? Camera : Vector2.Zero);
                float radius = F(2);
                var tint = name == "draw_circle" ? Tint : Colour(N(3), alpha);
                bool filled = N(name == "draw_circle" ? 3 : 5) < 0.5;
                Add(new GmDrawCommand(p, radius, tint, filled));
                return 0;
            }
            case "draw_ellipse_color":
            {
                var p1 = P(0) - (target < 0 ? Camera : Vector2.Zero);
                var p2 = P(2) - (target < 0 ? Camera : Vector2.Zero);
                var centre = (p1 + p2) / 2;
                var radius = (p2 - p1).Abs() / 2;
                var tint = Colour(N(4), alpha);
                Add(new GmDrawCommand(centre, radius, tint, a));
                return 0;
            }
            case "surface_create":
            {
                int id = nextSurface++;
                if (CreateEffect(id, Math.Max(1, I(0)), Math.Max(1, I(1)), context))
                    return id;
                var image = Image.CreateEmpty(
                    Math.Max(1, I(0)),
                    Math.Max(1, I(1)),
                    false,
                    Image.Format.Rgba8
                );
                image.Fill(Colors.Black);
                surfacePixels[id] = image;
                surfaceData[id] = image.GetData();
                var view = new SubViewport
                {
                    Size = new(Math.Max(1, I(0)), Math.Max(1, I(1))),
                    TransparentBg = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                };
                var canvas = new GmCanvas();
                view.AddChild(canvas);
                owner.AddChild(view);
                surfaces[id] = (view, canvas);
                return id;
            }
            case "surface_exists":
                return surfaces.ContainsKey(I(0));
            case "surface_free":
                effects.Remove(I(0));
                effectInfo.Remove(I(0));
                triangleCache.Clear();
                if (surfaces.Remove(I(0), out var old))
                    RetireSurface(old.View);
                if (surfacePixels.Remove(I(0), out var removed))
                    removed.Dispose();
                surfaceData.Remove(I(0));
                dirtySurfaces.Remove(I(0));
                dirtySurfaceTextures.Remove(I(0));
                if (surfaceTextures.Remove(I(0), out var freed))
                    textureData.Remove(freed);
                if (target == I(0))
                    target = -1;
                return 0;
            case "surface_set_target":
                if (!surfaces.ContainsKey(I(0)))
                    return false;
                target = I(0);
                return true;
            case "surface_reset_target":
                target = -1;
                return 0;
            case "surface_get_texture":
                return I(0);
            case "draw_surface":
            case "draw_surface_ext":
            {
                if (!surfaces.TryGetValue(I(0), out var s))
                    return 0;
                if (target >= 0 && !effects.ContainsKey(target))
                    SoftwareEffect(I(0));
                var texture = SurfaceTexture(I(0));
                var logical = SurfaceSize(I(0));
                var actual = texture.GetSize();
                var surfaceScale =
                    (name == "draw_surface_ext" ? P(3) : Vector2.One) * (
                        effects.TryGetValue(I(0), out var sourceEffect)
                            ? new Vector2(sourceEffect.Divisor, sourceEffect.Divisor)
                            : Vector2.One
                    );
                var sourceSize = effects.ContainsKey(I(0))
                    ? logical / (float)effects[I(0)].Divisor
                    : actual;
                int savedFilter = drawFilter;
                drawFilter =
                    effects.TryGetValue(I(0), out sourceEffect) && sourceEffect.Divisor == 4
                        ? 2
                        : 1;
                Textured(
                    texture,
                    Vector2.Zero,
                    new(Vector2.Zero, sourceSize),
                    P(1),
                    surfaceScale,
                    name == "draw_surface_ext" ? -Mathf.DegToRad(F(5)) : 0,
                    name == "draw_surface_ext" ? Colour(N(6), N(7)) : Colors.White
                );
                drawFilter = savedFilter;
                return 0;
            }
            case "draw_clear_alpha":
                if (effects.TryGetValue(target, out var cleared))
                {
                    cleared.Reset();
                    var saved = blend;
                    blend = 4;
                    Textured(
                        White,
                        Vector2.Zero,
                        new(0, 0, 1, 1),
                        Vector2.Zero,
                        cleared.Logical,
                        0,
                        Colour(N(0), N(1))
                    );
                    blend = saved;
                    return 0;
                }
                if (target >= 0 && surfaces.TryGetValue(target, out var surface))
                {
                    surfacePixels[target].Fill(Colour(N(0), N(1)));
                    surfaceData[target] = surfacePixels[target].GetData();
                    DirtySurface(target);
                    surface.Canvas.Commands.Clear();
                    surface.Canvas.StartBatches();
                    surface.View.TransparentBg = N(1) < 0.5;
                    surface.Canvas.QueueRedraw();
                }
                return 0;
            case "sprite_set_alpha_from_sprite":
            {
                var destination = world.Sprites[I(0)].GetProperty("frames");
                var source = world.Sprites[I(1)].GetProperty("frames");
                for (int f = 0; f < destination.GetArrayLength(); f++)
                {
                    var path = destination[f].GetProperty("path").GetString()!;
                    var previous = Texture(path);
                    using var image = previous.GetImage();
                    using var mask = Texture(
                            source[f % source.GetArrayLength()].GetProperty("path").GetString()!
                        )
                        .GetImage();
                    for (int y = 0; y < image.GetHeight(); y++)
                    for (int x = 0; x < image.GetWidth(); x++)
                    {
                        var pixel = image.GetPixel(x, y);
                        var m = mask.GetPixel(x % mask.GetWidth(), y % mask.GetHeight());
                        pixel.A = (m.R + m.G + m.B) / 3;
                        image.SetPixel(x, y, pixel);
                    }
                    textures[path] = ImageTexture.CreateFromImage(image);
                    textureData.Remove(previous);
                }
                return 0;
            }
            case "draw_primitive_begin":
            case "draw_primitive_begin_texture":
                primitive = I(0);
                vertices.Clear();
                uvs.Clear();
                vertexColours.Clear();
                if (
                    target >= 0
                    && !effects.ContainsKey(target)
                    && name.EndsWith("_texture", StringComparison.Ordinal)
                )
                    SoftwareEffect(I(1));
                primitiveTexture =
                    name.EndsWith("_texture", StringComparison.Ordinal)
                    && surfaces.ContainsKey(I(1))
                        ? SurfaceTexture(I(1))
                        : null;
                return 0;
            case "draw_vertex":
            case "draw_vertex_texture_color":
                vertices.Add(P(0) - (target < 0 ? Camera : Vector2.Zero));
                uvs.Add(name == "draw_vertex" ? Vector2.Zero : P(2));
                vertexColours.Add(name == "draw_vertex" ? Tint : Colour(N(4), N(5)));
                return 0;
            case "draw_primitive_end":
            {
                if (effects.TryGetValue(target, out var effect) && primitive is 4 or 5 or 6)
                {
                    void Emit(int a, int b, int c) =>
                        effect.Triangle(
                            primitiveTexture,
                            blend,
                            vertices[a],
                            vertices[b],
                            vertices[c],
                            vertexColours[a],
                            vertexColours[b],
                            vertexColours[c],
                            uvs[a],
                            uvs[b],
                            uvs[c]
                        );
                    if (primitive == 4)
                    {
                        for (int n = 0; n + 2 < vertices.Count; n += 3)
                            Emit(n, n + 1, n + 2);
                    }
                    else if (primitive == 5)
                    {
                        for (int n = 0; n + 2 < vertices.Count; n++)
                            if (n % 2 == 0)
                                Emit(n, n + 1, n + 2);
                            else
                                Emit(n + 1, n, n + 2);
                    }
                    else
                    {
                        for (int n = 1; n + 1 < vertices.Count; n++)
                            Emit(0, n, n + 1);
                    }
                }
                else
                    Primitive(
                        vertices.ToArray(),
                        vertexColours.ToArray(),
                        uvs.ToArray(),
                        primitiveTexture,
                        primitive
                    );
                return 0;
            }
        }
        return null;
    }
}

using System.Text.Json;

namespace SlackOfDescent;

public sealed class GmContext
{
    public GmInstance Self;
    public GmInstance Other;
    public GmScope Locals = new();
    public GmValue[] Arguments;
    public int EventObject = -1;
    public int EventKind;
    public int EventSub;
    public string? Flow;
    public GmValue Result;
    public GmContext(GmInstance self, GmInstance? other = null, GmValue[]? args = null)
    {
        Self = self;
        Other = other ?? self;
        Arguments = args ?? [];
    }
}

public sealed class GmFlow : Exception
{
    public readonly string Kind;
    public readonly GmValue Value;
    public GmFlow(string kind, GmValue value = default) { Kind = kind; Value = value; }
}

public sealed partial class GmEvaluator
{
    private readonly GmWorld world;
    private long remaining;
    private long reportTime=Environment.TickCount64;
    private readonly bool profiling=Environment.GetEnvironmentVariable("SLACK_PROFILE")=="1";
    public GmEvaluator(GmWorld world) { this.world = world; }
    public void ResetBudget(long budget = 20_000_000) => remaining = budget;
    private static string Tag(JsonElement e) => e[0].GetString()!;
    public GmValue Run(string key, GmContext context)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        if (!world.Code.TryGetValue(key, out var code)) throw new InvalidOperationException("Missing compiled code " + key);
        if(profiling&&Environment.TickCount64-reportTime>=5000){reportTime=Environment.TickCount64;Console.WriteLine("GML_PROFILE "+key+" frame="+world.Frame+" budget="+remaining);}
        long started=world.Performance.ScriptsEnabled?System.Diagnostics.Stopwatch.GetTimestamp():0;
        try
        {
            var result=Eval(code,context);
            if(context.Flow is not null){result=context.Result;context.Flow=null;context.Result=default;}
            return result;
        }
        catch (GmFlow f) { return f.Value; }
        catch (Exception e) when (e is not GmFlow) { throw new InvalidOperationException(key + ": " + e.Message, e); }
        finally{world.Performance.Script(key,started);}
    }

    public bool CompiledExecution=Environment.GetEnvironmentVariable("SLACK_INTERPRET")!="1";
    public GmValue Eval(JsonElement e,GmContext c)=>CompiledExecution?Executable(e)(c):Interpret(e,c);

    private GmValue Interpret(JsonElement e, GmContext c)
    {
        if (--remaining < 0) throw new InvalidOperationException("GML instruction budget exceeded");
        var tag = Tag(e);
        switch (tag)
        {
            case "num": return e[1].GetDouble();
            case "str": return e[1].GetString()!;
            case "id": return Read(c, e[1].GetString()!, null);
            case "group":
                foreach (var node in e[1].EnumerateArray()) Eval(node, c);
                return 0;
            case "var":
                foreach (var name in e[1].EnumerateArray()) c.Locals.Write(name.GetString()!, 0);
                return 0;
            case "globalvar":
                foreach (var name in e[1].EnumerateArray()) world.GlobalNames.Add(name.GetString()!);
                return 0;
            case "unary":
                var value = Eval(e[2], c);
                return e[1].GetString() switch
                {
                    "Add" => value,
                    "Subtract" => -value.Number,
                    "Not" => !value.Truth,
                    "Complement" => ~value.Int,
                    _ => throw new NotSupportedException("Unary " + e[1])
                };
            case "binary": return Binary(e, c);
            case "call": return world.Call(e[1].GetString()!, e[2].EnumerateArray().Select(p => Eval(p, c)).ToArray(), c);
            case "if":
                if (Eval(e[1], c).Truth) Eval(e[2], c);
                else if (e[3].ValueKind != JsonValueKind.Null) Eval(e[3], c);
                return 0;
            case "for":
                Eval(e[1], c);
                while (Eval(e[2], c).Truth)
                {
                    if (!Loop(e[4], c)) break;
                    Eval(e[3], c);
                }
                return 0;
            case "while":
                while (Eval(e[1], c).Truth) if (!Loop(e[2], c)) break;
                return 0;
            case "until":
                do { if (!Loop(e[2], c)) break; } while (!Eval(e[1], c).Truth);
                return 0;
            case "repeat":
                var count = Eval(e[1], c).Int;
                for (int i = 0; i < count; i++) if (!Loop(e[2], c)) break;
                return 0;
            case "with":
                foreach (var instance in world.Select(Eval(e[1], c).Int, c).ToArray())
                {
                    var next = new GmContext(instance, c.Self, c.Arguments) { Locals = c.Locals, EventObject = c.EventObject, EventKind = c.EventKind, EventSub = c.EventSub };
                    if (!Loop(e[2], next)) break;
                }
                return 0;
            case "switch":
                var input = Eval(e[1], c);
                var body = e[2];
                if (Tag(body) != "group") return 0;
                var nodes = body[1].EnumerateArray().ToArray();
                int start = -1, fallback = -1;
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (Tag(nodes[i]) == "case" && input.Equals(Eval(nodes[i][1], c))) { start = i + 1; break; }
                    if (Tag(nodes[i]) == "default") fallback = i + 1;
                }
                if (start < 0) start = fallback;
                if (start >= 0)
                {
                    try { for (int i = start; i < nodes.Length; i++) if (Tag(nodes[i]) is not "case" and not "default") Eval(nodes[i], c); }
                    catch (GmFlow f) when (f.Kind == "break") { }
                }
                return 0;
            case "return": throw new GmFlow("return", Eval(e[1], c));
            case "break": case "continue": case "exit": throw new GmFlow(tag);
            case "case": case "default": return 0;
            default: throw new NotSupportedException("AST " + tag);
        }
    }

    private bool Loop(JsonElement body, GmContext c)
    {
        try { Eval(body, c); return true; }
        catch (GmFlow f) when (f.Kind is "break" or "continue") { return f.Kind == "continue"; }
    }

    private GmValue Binary(JsonElement e, GmContext c)
    {
        var op = e[1].GetString()!;
        if (op is "Deref" or "Index") return Reference(e, c).Get();
        if (op.StartsWith("Assign", StringComparison.Ordinal))
        {
            var reference = Reference(e[2], c);
            var right = Eval(e[3], c);
            var result = op == "Assign" ? right : Apply(op[6..], reference.Get(), right);
            reference.Set(result);
            return result;
        }
        return Apply(op, Eval(e[2], c), Eval(e[3], c));
    }

    public static GmValue Apply(string op, GmValue a, GmValue b) => op switch
    {
        "Add" => a + b, "Subtract" => a - b, "Multiply" => a * b, "Divide" => a / b,
        "IntDivide" => Math.Truncate(a.Number / b.Number), "Modulo" => a.Number % b.Number,
        "BitwiseAnd" => a.Int & b.Int, "BitwiseOr" => a.Int | b.Int, "BitwiseXor" => a.Int ^ b.Int,
        "BinaryShiftLeft" => a.Int << b.Int, "BinaryShiftRight" => a.Int >> b.Int,
        "Equal" => a.Equals(b), "NotEqual" => !a.Equals(b),
        "LessThan" => a.CompareTo(b) < 0, "GreaterThan" => a.CompareTo(b) > 0,
        "LessThanOrEqual" => a.CompareTo(b) <= 0, "GreaterThanOrEqual" => a.CompareTo(b) >= 0,
        "And" => a.Truth && b.Truth, "Or" => a.Truth || b.Truth, "Xor" => a.Truth ^ b.Truth,
        _ => throw new NotSupportedException("Binary " + op)
    };

    public GmValue Read(GmContext c, string name, IReadOnlyList<int>? indices)
    {
        if (indices is null && world.Constants.TryGetValue(name, out var constant)) return constant;
        if (name == "argument" || name.StartsWith("argument", StringComparison.Ordinal) && int.TryParse(name[8..], out _))
        {
            var index = indices?[0] ?? (name == "argument" ? 0 : int.Parse(name[8..]));
            return index >= 0 && index < c.Arguments.Length ? c.Arguments[index] : 0;
        }
        if (name == "argument_count") return c.Arguments.Length;
        if (name == "self") return c.Self.Id;
        if (name == "other") return c.Other.Id;
        if (name == "global") return -5;
        if (c.Locals.Values.ContainsKey(name)) return c.Locals.Read(name, indices);
        if (world.GlobalNames.Contains(name)) return world.Globals.Read(name, indices);
        return world.Read(c.Self, name, indices);
    }

    private (Func<GmValue> Get, Action<GmValue> Set) Reference(JsonElement e, GmContext c, IReadOnlyList<int>? indices = null)
    {
        if (Tag(e) == "binary" && e[1].GetString() == "Index")
        {
            var node = e[3];
            var items = Tag(node) == "group" ? node[1].EnumerateArray().ToArray() : [node];
            return Reference(e[2], c, items.Select(i => Eval(i, c).Int).ToArray());
        }
        if (Tag(e) == "binary" && e[1].GetString() == "Deref")
        {
            var target = Eval(e[2], c).Int;
            var name = e[3][1].GetString()!;
            if (target == -5) return (() => world.Globals.Read(name, indices), v => world.Globals.Write(name, v, indices));
            GmInstance Instance() => world.Select(target, c).FirstOrDefault() ?? throw new InvalidOperationException("No instance " + target);
            return (() => world.Read(Instance(), name, indices), v => { foreach (var i in world.Select(target, c).ToArray()) world.Write(i, name, v, indices); });
        }
        if (Tag(e) == "id")
        {
            var name = e[1].GetString()!;
            if(name=="argument"||name.StartsWith("argument",StringComparison.Ordinal)&&int.TryParse(name[8..],out _))
            {
                int argumentIndex=indices?[0]??(name=="argument"?0:int.Parse(name[8..]));
                return (()=>argumentIndex>=0&&argumentIndex<c.Arguments.Length?c.Arguments[argumentIndex]:0,v=>
                {
                    if(argumentIndex<0||argumentIndex>=16)throw new IndexOutOfRangeException("Argument index");
                    if(argumentIndex>=c.Arguments.Length)Array.Resize(ref c.Arguments,argumentIndex+1);
                    c.Arguments[argumentIndex]=v;
                });
            }
            if (c.Locals.Values.ContainsKey(name)) return (() => c.Locals.Read(name, indices), v => c.Locals.Write(name, v, indices));
            if (world.GlobalNames.Contains(name)) return (() => world.Globals.Read(name, indices), v => world.Globals.Write(name, v, indices));
            return (() => world.Read(c.Self, name, indices), v => world.Write(c.Self, name, v, indices));
        }
        throw new InvalidOperationException("Invalid variable reference " + e);
    }
}

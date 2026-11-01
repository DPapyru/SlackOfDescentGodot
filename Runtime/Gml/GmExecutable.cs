using System.Text.Json;

namespace SlackOfDescent;

public sealed partial class GmEvaluator
{
    private readonly struct Location
    {
        private readonly GmWorld? world;
        private readonly GmContext? context;
        private readonly GmScope? scope;
        private readonly string name;
        private readonly GmIndices indices;
        private readonly int target;
        private readonly int argument;
        private readonly int arraySlot;
        public Location(GmScope scope,string name,GmIndices indices){this.scope=scope;this.name=name;this.indices=indices;world=null;context=null;target=0;argument=-1;arraySlot=-1;}
        public Location(GmScope scope,string name,GmIndices indices,int arraySlot){this.scope=scope;this.name=name;this.indices=indices;this.arraySlot=arraySlot;world=null;context=null;target=0;argument=-1;}
        public Location(GmWorld world,GmContext context,string name,GmIndices indices,int target=int.MinValue){this.world=world;this.context=context;this.name=name;this.indices=indices;this.target=target;scope=null;argument=-1;}
        public Location(GmContext context,int argument){this.context=context;this.argument=argument;world=null;scope=null;name="";indices=default;target=0;}
        public GmValue Get()
        {
            if(scope is not null)
            {
                if(arraySlot>=0)
                {
                    SlotArrayReads++;
                    if(scope.TryReadArraySlot(arraySlot,indices,out var slotValue))return slotValue;
                }
                return scope.Read(name,indices);
            }
            if(world is null)return argument>=0&&argument<context!.Arguments.Length?context.Arguments[argument]:0;
            if(target==int.MinValue)return world.Read(context!.Self,name,indices);
            var instance=world.ReferenceInstances(target,context!).FirstOrDefault();
            if(instance is not null)return world.Read(instance,name,indices);
            world.NoteEmptyInstanceRead(target,name);
            return 0;
        }
        public void Set(GmValue value)
        {
            if(scope is not null)
            {
                if(arraySlot>=0)
                {
                    SlotArrayWrites++;
                    if(scope.WriteArraySlot(arraySlot,name,indices,value))scope.Changed?.Invoke(name);
                    return;
                }
                scope.Write(name,value,indices);return;
            }
            if(world is null)
            {
                if(argument<0||argument>=16)throw new IndexOutOfRangeException("Argument index");
                if(argument>=context!.Arguments.Length)Array.Resize(ref context.Arguments,argument+1);
                context.Arguments[argument]=value;return;
            }
            if(target==int.MinValue){world.Write(context!.Self,name,value,indices);return;}
            var targets=world.ReferenceInstances(target,context!).ToArray();
            if(targets.Length==0){world.NoteEmptyInstanceWrite(target,name);return;}
            foreach(var instance in targets)world.Write(instance,name,value,indices);
        }
    }
    // One argument buffer per call site and nesting depth, so a call never allocates its argument array
    // twice and a recursive call still gets its own values.
    private sealed class CallArguments
    {
        private GmValue[]?[]buffers=new GmValue[4][];
        private int depth;
        private readonly int count;
        public CallArguments(int count)=>this.count=count;
        public GmValue[] Rent()
        {
            if(depth==buffers.Length)Array.Resize(ref buffers,buffers.Length*2);
            var buffer=buffers[depth];
            if(buffer is null||buffer.Length!=count)buffer=buffers[depth]=new GmValue[count];
            depth++;
            return buffer;
        }
        public void Return()=>depth--;
    }
    // One compiled cache per variable layout: the slot fast path is only valid for the layout it was
    // compiled against, so the plain cache and each slot layout own their own trees.
    private sealed class CompileContext
    {
        public readonly Dictionary<string,int>? Layout;
        public readonly Dictionary<string,int>? ArrayLayout;
        public readonly Dictionary<JsonElement,Func<GmContext,GmValue>> Executables=new(GmJsonIdentity.Instance);
        public readonly Dictionary<JsonElement,Func<GmContext,GmIndices,Location>> References=new(GmJsonIdentity.Instance);
        public CompileContext(Dictionary<string,int>? layout,Dictionary<string,int>? arrayLayout){Layout=layout;ArrayLayout=arrayLayout;}
    }
    private readonly CompileContext plainContext=new(null,null);
    private readonly Dictionary<GmWorld.SlotPlan,CompileContext> slotContexts=new(ReferenceEqualityComparer.Instance);
    private CompileContext compiling=null!;
    private CompileContext SlotContext(GmWorld.SlotPlan plan)
    {
        if(!slotContexts.TryGetValue(plan,out var context))slotContexts[plan]=context=new(plan.Scalars,plan.Arrays);
        return context;
    }
    private Func<GmContext,GmIndices,Location> Reference(JsonElement node)
    {
        var table=compiling.References;
        if(table.TryGetValue(node,out var result))return result;
        return table[node]=CompileReference(node);
    }
    // Counters for GmSlotLayoutTests: prove the array slot path actually runs instead of falling back.
    internal static long SlotArrayReads;
    internal static long SlotArrayWrites;
    private readonly List<Func<GmContext,GmValue>> compiled=new();
    private bool collectCompiled;
    private readonly HashSet<string> entryLocals=new(StringComparer.Ordinal);

    private static void CollectVarNames(JsonElement node, HashSet<string> names)
    {
        if(node.ValueKind!=JsonValueKind.Array||node.GetArrayLength()==0)return;
        if(node[0].ValueKind==JsonValueKind.String&&node[0].GetString() is "var" or "globalvar")
        {
            foreach(var declared in node[1].EnumerateArray())names.Add(declared.GetString()!);
            return;
        }
        for(int index=0;index<node.GetArrayLength();index++)CollectVarNames(node[index],names);
    }

    private Func<GmContext,GmValue> Executable(JsonElement node)
    {
        var table=compiling.Executables;
        if(table.TryGetValue(node,out var result))return result;
        var body=CompileNode(node);
        var compiledNode=table[node]=c=>
        {
            if(--remaining<0)throw new InvalidOperationException("GML instruction budget exceeded");
            return body(c);
        };
        if(collectCompiled)compiled.Add(compiledNode);
        return compiledNode;
    }
    private bool ExecuteLoop(Func<GmContext,GmValue> body,GmContext context)
    {
        body(context);
        if(context.Flow=="continue"){context.Flow=null;return true;}
        if(context.Flow=="break"){context.Flow=null;return false;}
        return context.Flow is null;
    }
    private Func<GmContext,GmValue> CompileNode(JsonElement node)
    {
        string tag=Tag(node);
        switch(tag)
        {
            case "num":{GmValue value=node[1].GetDouble();return _=>value;}
            case "str":{GmValue value=node[1].GetString()!;return _=>value;}
            case "id":
            {
                string name=node[1].GetString()!;
                if(compiling.Layout is { } layout&&!entryLocals.Contains(name)&&layout.TryGetValue(name,out int slot))
                    return c=>
                    {
                        var scope=c.Self.Scope;
                        if(ReferenceEquals(scope.Layout,layout))
                        {
                            if(scope.SlotDefined![slot])return scope.SlotValues![slot];
                            throw new KeyNotFoundException("Uninitialized variable "+name);
                        }
                        return Read(c,name,null);
                    };
                return c=>Read(c,name,null);
            }
            case "group":
            {
                var statements=node[1].EnumerateArray().Select(Executable).ToArray();
                return c=>{foreach(var statement in statements){statement(c);if(c.Flow is not null)break;}return 0;};
            }
            case "var":case "globalvar":
            {
                var names=node[1].EnumerateArray().Select(n=>n.GetString()!).ToArray();
                return c=>{foreach(string name in names)if(tag=="var")c.Locals.Write(name,0);else world.GlobalNames.Add(name);return 0;};
            }
            case "unary":
            {
                string op=node[1].GetString()!;var operand=Executable(node[2]);
                return c=>{var v=operand(c);return op switch{"Add"=>v,"Subtract"=>-v.Number,"Not"=>!v.Truth,"Complement"=>~v.Int,_=>throw new NotSupportedException("Unary "+op)};};
            }
            case "binary":
            {
                string op=node[1].GetString()!;
                if(op is "Deref" or "Index"){var reference=CompileReference(node);return c=>reference(c,default).Get();}
                if(op.StartsWith("Assign",StringComparison.Ordinal))
                {
                    var reference=CompileReference(node[2]);var right=Executable(node[3]);string operation=op[6..];
                    return c=>{var location=reference(c,default);var value=right(c);if(operation.Length>0)value=Apply(operation,location.Get(),value);location.Set(value);return value;};
                }
                var left=Executable(node[2]);var rhs=Executable(node[3]);return c=>Apply(op,left(c),rhs(c));
            }
            case "call":
            {
                string name=node[1].GetString()!;var arguments=node[2].EnumerateArray().Select(Executable).ToArray();
                if(world.CallSite(name,arguments.Length) is { } site)
                {
                    if(arguments.Length==0)return c=>site(Array.Empty<GmValue>(),c);
                    var siteBuffer=new CallArguments(arguments.Length);
                    return c=>{var values=siteBuffer.Rent();try{for(int i=0;i<values.Length;i++)values[i]=arguments[i](c);return site(values,c);}finally{siteBuffer.Return();}};
                }
                if(GmWorld.HasFixedArguments(name,arguments.Length))
                {
                    if(arguments.Length==1)return c=>world.CallFixed(name,arguments[0](c),default,1,c);
                    if(arguments.Length==2)return c=>world.CallFixed(name,arguments[0](c),arguments[1](c),2,c);
                    return c=>world.CallFixed(name,arguments[0](c),arguments[1](c),arguments[2](c),c);
                }
                if(arguments.Length==0)return c=>world.Call(name,Array.Empty<GmValue>(),c);
                var buffer=new CallArguments(arguments.Length);
                return c=>{var values=buffer.Rent();try{for(int i=0;i<values.Length;i++)values[i]=arguments[i](c);return world.Call(name,values,c);}finally{buffer.Return();}};
            }
            case "if":
            {
                var condition=Executable(node[1]);var yes=Executable(node[2]);var no=node[3].ValueKind==JsonValueKind.Null?null:Executable(node[3]);
                return c=>{if(condition(c).Truth)yes(c);else no?.Invoke(c);return 0;};
            }
            case "for":
            {
                var initialize=Executable(node[1]);var condition=Executable(node[2]);var update=Executable(node[3]);var body=Executable(node[4]);
                return c=>{initialize(c);while(condition(c).Truth){if(!ExecuteLoop(body,c))break;update(c);}return 0;};
            }
            case "while":case "until":
            {
                var condition=Executable(node[1]);var body=Executable(node[2]);
                if(tag=="while")return c=>{while(condition(c).Truth)if(!ExecuteLoop(body,c))break;return 0;};
                return c=>{do{if(!ExecuteLoop(body,c))break;}while(!condition(c).Truth);return 0;};
            }
            case "repeat":
            {
                var count=Executable(node[1]);var body=Executable(node[2]);
                return c=>{int n=count(c).Int;for(int i=0;i<n;i++)if(!ExecuteLoop(body,c))break;return 0;};
            }
            case "with":
            {
                var target=Executable(node[1]);var body=Executable(node[2]);
                return c=>
                {
                    foreach(var instance in world.Select(target(c).Int,c).ToArray())
                    {
                        var next=new GmContext(instance,c.Self,c.Arguments,c.Locals){EventObject=c.EventObject,EventKind=c.EventKind,EventSub=c.EventSub};
                        if(!ExecuteLoop(body,next)){c.Flow=next.Flow;c.Result=next.Result;break;}
                    }
                    return 0;
                };
            }
            case "switch":
            {
                var value=Executable(node[1]);
                if(Tag(node[2])!="group")return c=>{value(c);return 0;};
                var nodes=node[2][1].EnumerateArray().ToArray();
                var tags=nodes.Select(Tag).ToArray();
                var statements=nodes.Select((n,i)=>tags[i]=="case"?Executable(n[1]):Executable(n)).ToArray();
                return c=>
                {
                    var input=value(c);int start=-1,fallback=-1;
                    for(int i=0;i<nodes.Length;i++)
                    {
                        if(tags[i]=="case"&&input.Equals(statements[i](c))){start=i+1;break;}
                        if(tags[i]=="default")fallback=i+1;
                    }
                    if(start<0)start=fallback;
                    if(start>=0)for(int i=start;i<nodes.Length;i++)if(tags[i] is not "case" and not "default")
                    {
                        statements[i](c);
                        if(c.Flow is not null){if(c.Flow=="break")c.Flow=null;break;}
                    }
                    return 0;
                };
            }
            case "return":{var value=Executable(node[1]);return c=>{c.Result=value(c);c.Flow="return";return 0;};}
            case "break":case "continue":case "exit":return c=>{c.Flow=tag;c.Result=default;return 0;};
            case "case":case "default":return _=>0;
            default:throw new NotSupportedException("AST "+tag);
        }
    }
    private Func<GmContext,GmIndices,Location> CompileReference(JsonElement node)
    {
        string tag=Tag(node);
        if(tag=="binary"&&node[1].GetString()=="Index")
        {
            var index=node[3];var items=(Tag(index)=="group"?index[1].EnumerateArray().ToArray():[index]).Select(Executable).ToArray();
            var reference=CompileReference(node[2]);
            var baseNode=node[2];
            if(compiling.ArrayLayout is { } arrayLayout&&(items.Length==1||items.Length==2)
                &&baseNode.ValueKind==JsonValueKind.Array&&baseNode[0].GetString()=="id")
            {
                string arrayName=baseNode[1].GetString()!;
                if(!entryLocals.Contains(arrayName)&&arrayLayout.TryGetValue(arrayName,out int arraySlot))
                {
                    if(items.Length==1)return (c,_)=>ArrayReference(c,arrayLayout,arraySlot,arrayName,new GmIndices(items[0](c).Int),reference);
                    return (c,_)=>ArrayReference(c,arrayLayout,arraySlot,arrayName,new GmIndices(items[0](c).Int,items[1](c).Int),reference);
                }
            }
            if(items.Length==1)return (c,_)=>reference(c,new GmIndices(items[0](c).Int));
            if(items.Length==2)return (c,_)=>reference(c,new GmIndices(items[0](c).Int,items[1](c).Int));
            return (c,_)=>{var indices=new int[items.Length];for(int i=0;i<indices.Length;i++)indices[i]=items[i](c).Int;return reference(c,new GmIndices(indices));};
        }
        if(tag=="binary"&&node[1].GetString()=="Deref")
        {
            var owner=Executable(node[2]);string name=node[3][1].GetString()!;
            return (c,indices)=>
            {
                int target=owner(c).Int;
                if(target==-5)return new(world.Globals,name,indices);
                return new(world,c,name,indices,target);
            };
        }
        if(tag=="id")
        {
            string name=node[1].GetString()!;
            if(name=="argument"||name.StartsWith("argument",StringComparison.Ordinal)&&int.TryParse(name[8..],out _))return (c,indices)=>
            {
                int index=indices.FirstOr(name=="argument"?0:int.Parse(name[8..]));
                return new(c,index);
            };
            return (c,indices)=>
            {
                if(c.Locals.Values.ContainsKey(name))return new(c.Locals,name,indices);
                if(world.GlobalNames.Contains(name))return new(world.Globals,name,indices);
                return new(world,c,name,indices);
            };
        }
        throw new InvalidOperationException("Invalid variable reference "+node);
    }
    private static Location ArrayReference(
        GmContext c,
        Dictionary<string,int> arrayLayout,
        int arraySlot,
        string name,
        GmIndices indices,
        Func<GmContext,GmIndices,Location> fallback
    )
    {
        var scope=c.Self.Scope;
        if(ReferenceEquals(scope.ArrayLayout,arrayLayout))return new(scope,name,indices,arraySlot);
        return fallback(c,indices);
    }
}

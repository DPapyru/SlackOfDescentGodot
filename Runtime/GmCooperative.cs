using System.Text.Json;

namespace SlackOfDescent;

public sealed partial class GmEvaluator
{
    private readonly Dictionary<JsonElement,bool> waitingNodes=new(GmJsonIdentity.Instance);
    private readonly HashSet<string> checkingScripts=new();
    private bool CanWait(JsonElement node)
    {
        if(node.ValueKind!=JsonValueKind.Array)return false;
        if(waitingNodes.TryGetValue(node,out bool waiting))return waiting;
        if(Tag(node)=="call")
        {
            string name=node[1].GetString()!;
            if(name is "show_menu" or "get_string" or "show_message" or "show_message_ext" or "show_question" or "sleep" or "io_handle" or "keyboard_wait" or "event_user" or "event_perform" or "event_inherited" or "action_inherited" or "execute_string" or "execute_file" or "action_execute_script" or "instance_create" or "instance_destroy" or "instance_change" or "action_kill_object")return waitingNodes[node]=true;
            if(world.Scripts.TryGetValue(name,out var key))
            {
                if(!checkingScripts.Add(key))return true;
                bool result=CanWait(world.Code[key]);checkingScripts.Remove(key);
                if(result)return waitingNodes[node]=true;
            }
        }
        foreach(var child in node.EnumerateArray())if(child.ValueKind==JsonValueKind.Array)
        {
            if(child.GetArrayLength()>0&&child[0].ValueKind==JsonValueKind.String)
            {if(CanWait(child))return waitingNodes[node]=true;}
            else foreach(var item in child.EnumerateArray())if(CanWait(item))return waitingNodes[node]=true;
        }
        return waitingNodes[node]=false;
    }
    public async ValueTask<GmValue> RunAsync(string key,GmContext context)
    {
        if(string.IsNullOrEmpty(key))return 0;
        if(world.SuppressCode?.Invoke(key,context)==true)return 0;
        var code=world.Code[key];if(!CanWait(code))return Run(key,context);
        long started=world.Performance.ScriptsEnabled?System.Diagnostics.Stopwatch.GetTimestamp():0;
        try
        {
            var result=await WaitNode(code,context);
            if(context.Flow is not null){result=context.Result;context.Flow=null;context.Result=default;}
            return result;
        }
        catch(GmFlow flow){return flow.Value;}
        catch(Exception e){throw new InvalidOperationException(key+": "+e.Message,e);}
        finally{world.Performance.Script(key,started);}
    }
    private async ValueTask<bool> WaitLoop(JsonElement body,GmContext context)
    {
        try{await WaitNode(body,context);}
        catch(GmFlow flow)when(flow.Kind is "break" or "continue"){return flow.Kind=="continue";}
        if(context.Flow=="continue"){context.Flow=null;return true;}
        if(context.Flow=="break"){context.Flow=null;return false;}
        return context.Flow is null;
    }
    public async ValueTask<GmValue> CallAsync(string name,GmValue[] arguments,GmContext context)
    {
        if(name is "show_menu" or "get_string" or "show_message" or "show_message_ext" or "show_question" or "sleep" or "io_handle" or "keyboard_wait")
        {
            if(world.Wait is null)throw new InvalidOperationException("Input wait requires interactive host: "+name);
            var result=await world.Wait(name,arguments,context);ResetBudget();return result;
        }
        if(name is "event_user" or "event_perform" or "event_inherited" or "action_inherited")
        {
            int kind=name=="event_user"?7:name=="event_perform"?arguments[0].Int:context.EventKind;
            int sub=name=="event_user"?10+arguments[0].Int:name=="event_perform"?arguments[1].Int:context.EventSub;
            int? start=name is "event_inherited" or "action_inherited"?world.Objects[context.EventObject].GetProperty("parent").GetInt32():null;
            await world.EventAsync(context.Self,kind,sub,context.Other,start);return 0;
        }
        if(name=="instance_create")
        {
            var instance=world.Create(arguments[2].Int,arguments[0].Number,arguments[1].Number,run:false);
            await world.EventAsync(instance,0,0);return instance.Id;
        }
        if(name is "instance_destroy" or "action_kill_object")
        {
            await world.EventAsync(context.Self,1,0);context.Self.Destroyed=true;return 0;
        }
        if(name=="instance_change")
        {
            if(arguments[1].Truth)await world.EventAsync(context.Self,1,0);
            world.ChangeObject(context.Self,arguments[0].Int);
            if(arguments[1].Truth)await world.EventAsync(context.Self,0,0);
            return 0;
        }
        if(name=="execute_string")return await RunAsync(world.Compile(arguments[0].ToString()),context);
        if(name=="execute_file")return await RunAsync(world.FileCode(arguments[0].ToString()),context);
        if(name=="action_execute_script")
        {
            var script=world.Manifest.GetProperty("scripts").EnumerateArray().First(s=>s.GetProperty("id").GetInt32()==arguments[0].Int);
            return await CallAsync(script.GetProperty("name").GetString()!,arguments.Skip(1).ToArray(),context);
        }
        if(name is not ("draw_sprite_blur" or "MotionInitialize" or "DoubleMotionInitialize" or "setMotionForPlayer")&&world.Scripts.TryGetValue(name,out var key)&&CanWait(world.Code[key]))
            return await RunAsync(key,new(context.Self,context.Other,arguments){EventObject=context.EventObject,EventKind=context.EventKind,EventSub=context.EventSub});
        return world.Call(name,arguments,context);
    }
    private async ValueTask<Location> WaitReference(JsonElement node,GmContext context,int[]? indices=null)
    {
        if(!CanWait(node))return CompileReference(node)(context,indices);
        if(Tag(node)=="binary"&&node[1].GetString()=="Index")
        {
            var items=Tag(node[3])=="group"?node[3][1].EnumerateArray().ToArray():[node[3]];
            var values=new int[items.Length];for(int i=0;i<items.Length;i++)values[i]=(await WaitNode(items[i],context)).Int;
            return await WaitReference(node[2],context,values);
        }
        if(Tag(node)=="binary"&&node[1].GetString()=="Deref")
        {
            int target=(await WaitNode(node[2],context)).Int;string name=node[3][1].GetString()!;
            return target==-5?new(world.Globals,name,indices):new(world,context,name,indices,target);
        }
        return CompileReference(node)(context,indices);
    }
    private async ValueTask<GmValue> WaitNode(JsonElement node,GmContext context)
    {
        if(!CanWait(node))return Eval(node,context);
        if(--remaining<0)throw new InvalidOperationException("GML instruction budget exceeded");
        switch(Tag(node))
        {
            case "group":
                foreach(var statement in node[1].EnumerateArray()){await WaitNode(statement,context);if(context.Flow is not null)break;}
                return 0;
            case "call":
            {
                string name=node[1].GetString()!;var arguments=new GmValue[node[2].GetArrayLength()];
                int index=0;foreach(var argument in node[2].EnumerateArray())arguments[index++]=await WaitNode(argument,context);
                return await CallAsync(name,arguments,context);
            }
            case "unary":
            {
                var value=await WaitNode(node[2],context);
                return node[1].GetString() switch{"Add"=>value,"Subtract"=>-value.Number,"Not"=>!value.Truth,"Complement"=>~value.Int,_=>throw new NotSupportedException("Unary "+node[1])};
            }
            case "binary":
            {
                string operation=node[1].GetString()!;
                if(operation is "Deref" or "Index")return (await WaitReference(node,context)).Get();
                if(operation.StartsWith("Assign",StringComparison.Ordinal))
                {
                    var location=await WaitReference(node[2],context);var value=await WaitNode(node[3],context);
                    if(operation.Length>6)value=Apply(operation[6..],location.Get(),value);
                    location.Set(value);return value;
                }
                return Apply(operation,await WaitNode(node[2],context),await WaitNode(node[3],context));
            }
            case "if":
                if((await WaitNode(node[1],context)).Truth)await WaitNode(node[2],context);
                else if(node[3].ValueKind!=JsonValueKind.Null)await WaitNode(node[3],context);
                return 0;
            case "for":
                await WaitNode(node[1],context);
                while((await WaitNode(node[2],context)).Truth){if(!await WaitLoop(node[4],context))break;await WaitNode(node[3],context);}
                return 0;
            case "while":
                while((await WaitNode(node[1],context)).Truth)if(!await WaitLoop(node[2],context))break;
                return 0;
            case "until":
                do{if(!await WaitLoop(node[2],context))break;}while(!(await WaitNode(node[1],context)).Truth);
                return 0;
            case "repeat":
                int count=(await WaitNode(node[1],context)).Int;
                for(int i=0;i<count;i++)if(!await WaitLoop(node[2],context))break;
                return 0;
            case "with":
                foreach(var instance in world.Select((await WaitNode(node[1],context)).Int,context).ToArray())
                {
                    var next=new GmContext(instance,context.Self,context.Arguments){Locals=context.Locals,EventObject=context.EventObject,EventKind=context.EventKind,EventSub=context.EventSub};
                    if(!await WaitLoop(node[2],next)){context.Flow=next.Flow;context.Result=next.Result;break;}
                }
                return 0;
            case "switch":
            {
                var value=await WaitNode(node[1],context);var statements=node[2][1].EnumerateArray().ToArray();int start=-1,fallback=-1;
                for(int i=0;i<statements.Length;i++)
                {
                    string tag=Tag(statements[i]);
                    if(tag=="case"&&value.Equals(await WaitNode(statements[i][1],context))){start=i+1;break;}
                    if(tag=="default")fallback=i+1;
                }
                if(start<0)start=fallback;
                try{if(start>=0)for(int i=start;i<statements.Length;i++)if(Tag(statements[i]) is not ("case" or "default"))
                {
                    await WaitNode(statements[i],context);
                    if(context.Flow is not null){if(context.Flow=="break")context.Flow=null;break;}
                }}catch(GmFlow flow)when(flow.Kind=="break"){}
                return 0;
            }
            case "return":context.Result=await WaitNode(node[1],context);context.Flow="return";return 0;
            default:throw new NotSupportedException("Waiting AST "+Tag(node));
        }
    }
}

public sealed partial class GmWorld
{
    public Func<string,GmValue[],GmContext,ValueTask<GmValue>>? Wait;
    public async ValueTask<bool> EventAsync(GmInstance instance,int kind,int sub,GmInstance? other=null,int? start=null)
    {
        int obj=EventOwner(start??instance.Object,kind,sub);
        if(obj<0)return false;
        var context=new GmContext(instance,other){EventObject=obj,EventKind=kind,EventSub=sub};
        try
        {
            if(!clearedEvents.Contains((obj,kind,sub))&&staticEvents.TryGetValue((obj,kind,sub),out var actions))await ActionsAsync(actions,context);
            if(addedEvents.TryGetValue((obj,kind,sub),out var added))foreach(string key in added.ToArray())await Evaluator.RunAsync(key,context);
        }
        catch(GmFlow flow)when(flow.Kind is "exit" or "return"){}
        return true;
    }
    private async ValueTask ActionsAsync(JsonElement actions,GmContext context)
    {
        if(!actionEntries.TryGetValue(actions,out var entries))actionEntries[actions]=entries=actions.EnumerateArray().Where(a=>a.GetProperty("kind").GetInt32()!=0||a.GetProperty("execution").GetInt32()!=0).ToArray();
        int index=0;while(index<entries.Length)index=await ActionBlockAsync(entries,index,context,true);
    }
    private async ValueTask<int> ActionBlockAsync(JsonElement[] entries,int index,GmContext context,bool execute)
    {
        if(index>=entries.Length)return index;
        var action=entries[index++];int kind=action.GetProperty("kind").GetInt32();
        if(kind==1)
        {
            while(index<entries.Length&&entries[index].GetProperty("kind").GetInt32()!=2)index=await ActionBlockAsync(entries,index,context,execute);
            return index<entries.Length?index+1:index;
        }
        if(kind==4){if(execute)throw new GmFlow("exit");return index;}
        if(kind is 2 or 3)return index;
        GmValue result=0;
        if(execute)
        {
            var targets=action.GetProperty("target").GetInt32()==-1?[context.Self]:Select(action.GetProperty("target").GetInt32(),context).ToArray();
            foreach(var target in targets)
            {
                var c=new GmContext(target,context.Other,context.Arguments){EventObject=context.EventObject,EventKind=context.EventKind,EventSub=context.EventSub,Locals=context.Locals};
                if(action.GetProperty("code").ValueKind==JsonValueKind.String)result=await Evaluator.RunAsync(action.GetProperty("code").GetString()!,c);
                else if(action.GetProperty("execution").GetInt32()==1)
                {
                    var args=action.GetProperty("parameters").EnumerateArray().Select(p=>Evaluator.Eval(p,c)).ToArray();
                    c.Locals.Write("argument_relative",action.GetProperty("relative").GetBoolean());result=await Evaluator.CallAsync(action.GetProperty("function").GetString()!,args,c);
                }
            }
        }
        if(action.GetProperty("condition").GetBoolean())
        {
            bool passed=result.Truth^action.GetProperty("invert").GetBoolean();
            index=await ActionBlockAsync(entries,index,context,execute&&passed);
            if(index<entries.Length&&entries[index].GetProperty("kind").GetInt32()==3)index=await ActionBlockAsync(entries,index+1,context,execute&&!passed);
        }
        return index;
    }
    private async ValueTask RunObjectEventAsync(int kind,int sub)
    {
        foreach(var instance in EventInstances(kind,sub))if(!instance.Destroyed&&instance.Active)await EventAsync(instance,kind,sub);
    }
}

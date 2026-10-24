using System.Text.Json;
using SlackOfDescent;

public static class GmlCallAllocationTests
{
    public static int Run(GmWorld source)
    {
        var world=new GmWorld(source.Root,source.SourceRoot);
        int assertions=0;
        void Equal(GmValue actual,GmValue expected,string name)
        {
            if(!actual.Equals(expected))throw new Exception(name+": expected "+expected.ToString()+", got "+actual.ToString());
            assertions++;
        }
        int sprite=world.RegisterSprite(JsonSerializer.SerializeToElement(new
        {
            originX=1,originY=2,
            frames=new[]{new{width=2,height=3,path="unused"}},
            masks=new[]{new{width=2,height=3,left=0,right=1,top=0,bottom=2,data="111111"}}
        }));
        var outer=new GmContext(new GmInstance());
        int obj=world.Call("object_add",[],outer).Int;
        world.Call("object_set_sprite",[obj,sprite],outer);
        var self=world.Create(obj,10,10);
        var first=world.Create(obj,20,10);
        var second=world.Create(obj,20,10);
        var context=new GmContext(self,args:[812,"outer"]);
        self.Scope.Write("probe",1);
        self.Scope.Write("",1);
        void Run(string code)
        {
            world.Evaluator.ResetBudget();
            world.Evaluator.Run(world.Compile(code),context);
        }
        foreach(bool compiled in new[]{false,true})
        {
            world.Evaluator.CompiledExecution=compiled;
            Run($"w=sprite_get_width({sprite});h=sprite_get_height({sprite});ox=sprite_get_xoffset({sprite});oy=sprite_get_yoffset({sprite});n=sprite_get_number({sprite});missing=sprite_get_width(-1);exists=variable_local_exists(\"probe\");empty=variable_local_exists();count=instance_number({obj});present=instance_exists({first.Id});");
            Equal(self.Scope.Read("w"),2,"fixed sprite width "+compiled);
            Equal(self.Scope.Read("h"),3,"fixed sprite height "+compiled);
            Equal(self.Scope.Read("ox"),1,"fixed sprite origin x "+compiled);
            Equal(self.Scope.Read("oy"),2,"fixed sprite origin y "+compiled);
            Equal(self.Scope.Read("n"),1,"fixed sprite frame count "+compiled);
            Equal(self.Scope.Read("missing"),0,"fixed sprite lookup preserves missing resource "+compiled);
            Equal(self.Scope.Read("exists"),true,"fixed variable existence "+compiled);
            Equal(self.Scope.Read("empty"),true,"missing variable existence argument stays empty string "+compiled);
            Equal(self.Scope.Read("count"),3,"fixed instance count "+compiled);
            Equal(self.Scope.Read("present"),true,"fixed instance existence "+compiled);
            Run($"hit=place_meeting(20,10,{obj});found=instance_place(20,10,{obj});miss=place_meeting(50,10,{obj});");
            Equal(self.Scope.Read("hit"),true,"fixed place query hits "+compiled);
            Equal(self.Scope.Read("found"),first.Id,"fixed place query preserves first instance "+compiled);
            Equal(self.Scope.Read("miss"),false,"fixed place query misses "+compiled);
            first.Active=false;
            Run($"found=instance_place(20,10,{obj});count=instance_number({obj});present=instance_exists({first.Id});");
            Equal(self.Scope.Read("found"),second.Id,"fixed place query respects activation "+compiled);
            Equal(self.Scope.Read("count"),2,"fixed count updates after deactivation "+compiled);
            Equal(self.Scope.Read("present"),false,"fixed existence updates after deactivation "+compiled);
            first.Active=true;
            int platformCalls=0;
            GmValue[]? intercepted=null;
            GmContext? interceptedContext=null;
            world.Platform=(name,args,c)=>
            {
                if(name is not ("place_meeting" or "instance_place"))return null;
                platformCalls++;intercepted=args;interceptedContext=c;
                return name=="place_meeting"?new GmValue(17):new GmValue(812345);
            };
            int meetingCalls=world.Calls["place_meeting"],placeCalls=world.Calls["instance_place"];
            Run($"hit=place_meeting(20,10,{obj});found=instance_place(20,10,{obj});");
            Equal(self.Scope.Read("hit"),17,"fixed place query preserves platform override "+compiled);
            Equal(self.Scope.Read("found"),812345,"fixed instance query preserves platform override "+compiled);
            Equal(platformCalls,2,"fixed place queries invoke platform once "+compiled);
            Equal(world.Calls["place_meeting"]-meetingCalls,1,"platform place query counted once "+compiled);
            Equal(world.Calls["instance_place"]-placeCalls,1,"platform instance query counted once "+compiled);
            Equal(intercepted!.Length,3,"platform receives all fixed arguments "+compiled);
            Equal(intercepted[2],obj,"platform receives fixed target argument "+compiled);
            Equal(ReferenceEquals(interceptedContext,context),true,"platform receives caller context "+compiled);
            platformCalls=0;
            world.Platform=(name,args,c)=>{if(name is "place_meeting" or "instance_place")platformCalls++;return null;};
            Run($"hit=place_meeting(20,10,{obj});found=instance_place(20,10,{obj});");
            Equal(self.Scope.Read("hit"),true,"null platform result continues place collision "+compiled);
            Equal(self.Scope.Read("found"),first.Id,"null platform result continues ordered collision "+compiled);
            Equal(platformCalls,2,"null platform result still invokes interceptor "+compiled);
            world.Platform=null;
            string getter=$"overridden=sprite_get_width({sprite});";
            Run(getter);
            world.Scripts["sprite_get_width"]=world.Compile("var saved;saved=argument0;argument0=77;return saved+argument_count;");
            Run(getter);
            Equal(self.Scope.Read("overridden"),sprite+1,"cached fixed call respects script override "+compiled);
            Equal(context.Arguments[0],812,"script override has independent arguments "+compiled);
            world.Scripts.Remove("sprite_get_width");
            Run(getter);
            Equal(self.Scope.Read("overridden"),2,"cached fixed call respects removed override "+compiled);
            world.Scripts["fixed_argument_mark"]=world.Compile("value_order=value_order*10+argument0;return argument0;");
            world.Scripts["place_meeting"]=world.Compile("return argument_count*1000+argument0*100+argument1*10+argument2;");
            platformCalls=0;
            world.Platform=(name,args,c)=>{if(name!="place_meeting")return null;platformCalls++;return new GmValue(-1);};
            Run("value_order=0;ordered=place_meeting(fixed_argument_mark(1),fixed_argument_mark(2),fixed_argument_mark(3));");
            Equal(self.Scope.Read("ordered"),3123,"fixed three argument script override "+compiled);
            Equal(self.Scope.Read("value_order"),123,"fixed arguments evaluate left to right "+compiled);
            Run("value_order=0;ordered=place_meeting(fixed_argument_mark(1),fixed_argument_mark(2),fixed_argument_mark(3),fixed_argument_mark(4));");
            Equal(self.Scope.Read("ordered"),4123,"extra arguments preserve argument count "+compiled);
            Equal(self.Scope.Read("value_order"),1234,"extra arguments preserve side effects "+compiled);
            Equal(platformCalls,0,"script override precedes platform interception "+compiled);
            world.Platform=null;
            world.Scripts.Remove("place_meeting");
        }
        world.Evaluator.CompiledExecution=true;
        world.Scripts["place_meeting"]=world.Compile("var saved;saved=argument0;argument1=9;return saved*100+argument1*10+real(get_string(\"value\",\"0\"));");
        var completion=new TaskCompletionSource<GmValue>();
        world.Wait=(name,args,c)=>new(completion.Task);
        world.Evaluator.ResetBudget();
        var suspended=world.Evaluator.RunAsync(world.Compile("async_value=place_meeting(4,5,6);"),context);
        Equal(suspended.IsCompleted,false,"overridden three argument script can suspend");
        Run($"found=instance_place(20,10,{obj});");
        completion.SetResult("7");
        suspended.GetAwaiter().GetResult();
        Equal(self.Scope.Read("async_value"),497,"suspended script retains argument mutations and locals");
        Equal(context.Arguments[0],812,"suspended script preserves caller arguments");
        world.Scripts.Remove("place_meeting");
        world.Wait=null;
        string allocationKey=world.Compile($"repeat(2000){{native_total=sprite_get_width({sprite})+sprite_get_height({sprite})+sprite_get_xoffset({sprite})+sprite_get_yoffset({sprite})+sprite_get_number({sprite});}}");
        for(int warmup=0;warmup<3;warmup++){world.Evaluator.ResetBudget();world.Evaluator.Run(allocationKey,context);}
        long before=GC.GetAllocatedBytesForCurrentThread();
        world.Evaluator.ResetBudget();world.Evaluator.Run(allocationKey,context);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Equal(allocated<1024,true,"ten thousand fixed native getter calls avoid argument arrays: "+allocated);
        Equal(self.Scope.Read("native_total"),9,"allocation benchmark preserves getter values");
        return assertions;
    }
}

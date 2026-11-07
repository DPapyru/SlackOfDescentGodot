using SlackOfDescent;

public static class WaitingPathTests
{
    public static int Run(GmWorld source)
    {
        var world=new GmWorld(source.Root,source.SourceRoot);
        world.Evaluator.ResetBudget();
        int assertions=0;
        void Check(bool condition,string name){if(!condition)throw new Exception(name);assertions++;}
        void Equal(GmValue actual,GmValue expected,string name)
        {
            if(!actual.Equals(expected))throw new Exception(name+": expected "+expected.ToString()+", got "+actual.ToString());
            assertions++;
        }
        bool Waits(string code)=>world.Evaluator.CanWait(world.Code[world.Compile(code)]);

        Check(!Waits("instance_create(0,0,objWaterBubble)"),"instance_create of a non-waiting create event stays compiled");
        Check(!Waits("instance_create(0,0,objBlock)"),"instance_create of an object without create event stays compiled");
        Check(Waits("instance_create(0,0,objPlayerDoll)"),"instance_create of a waiting create event keeps the waiting path");
        Check(Waits("instance_create(0,0,argument0)"),"dynamic instance_create keeps the waiting path");
        Check(Waits("event_user(0)"),"event_user keeps the waiting path");
        Check(Waits("instance_destroy()"),"instance_destroy keeps the waiting path");
        Check(!world.Evaluator.CanWait(world.Code["objects/objSwampSurface/3/0/0"]),"swamp surface step leaves the waiting path");
        Check(!world.Evaluator.CanWait(world.Code["objects/objQuicksand/3/0/0"]),"quicksand step leaves the waiting path");

        int bubble=world.Constants["objWaterBubble"].Int;
        var self=new GmInstance{Id=999998,Object=-1};
        world.Instances.Add(self);
        var context=new GmContext(self);
        string spawn=world.Compile("instance_create(100,200,objWaterBubble)");
        long created=world.Created.GetValueOrDefault(bubble);
        int instancesBefore=world.Instances.Count;
        world.Evaluator.Run(spawn,context);
        Equal(world.Instances.Count-instancesBefore,1,"compiled spawn adds one instance");
        var byCompiled=world.Instances[^1];
        Equal(world.Created.GetValueOrDefault(bubble)-created,1,"compiled spawn runs the create event once");
        Equal(byCompiled.Object,bubble,"compiled spawn creates the requested object");
        Equal(byCompiled.Scope.ReadOr("x").Number,100,"compiled spawn keeps x");
        Equal(byCompiled.Scope.ReadOr("y").Number,200,"compiled spawn keeps y");
        Equal(byCompiled.Scope.ReadOr("alarm",0).Int,60,"compiled spawn runs create event body");
        world.Evaluator.ResetBudget();
        world.Evaluator.RunAsync(spawn,context).GetAwaiter().GetResult();
        var byWaiting=world.Instances[^1];
        Equal(world.Created.GetValueOrDefault(bubble)-created,2,"waiting spawn runs the create event once");
        Equal(byWaiting.Object,bubble,"waiting spawn creates the requested object");
        Equal(byWaiting.Scope.ReadOr("alarm",0).Int,60,"waiting spawn runs create event body");
        Equal(byWaiting.Scope.ReadOr("gravity_direction").Number,byCompiled.Scope.ReadOr("gravity_direction").Number,"both paths derive the same create state");

        string refs=world.Compile(
            "done=0;store[2]=5;store[2]=store[2]+7;self_target.hp=9;self_target.hp=self_target.hp+3;if(0)get_string(\"x\",\"0\");done=1;");
        var target=new GmInstance{Id=999997,Object=-1};
        target.Scope.Write("hp",0);
        world.Instances.Add(target);
        self.Scope.Write("self_target",target.Id);
        self.Scope.Write("store",3,[2]);
        world.Evaluator.ResetBudget();
        world.Evaluator.RunAsync(refs,new GmContext(self)).GetAwaiter().GetResult();
        Equal(self.Scope.ReadOr("done"),1,"waiting path runs reference writes");
        Equal(self.Scope.Read("store",2),12,"waiting path writes array elements");
        Equal(target.Scope.ReadOr("hp"),12,"waiting path writes through dereferenced targets");

        world.Platform=(name,args,c)=>name.StartsWith("draw_",StringComparison.Ordinal)?new GmValue(0):null;
        var drawing=world.Create(world.Constants["objQuicksand"].Int,2320,1440,run:false);
        for(int index=0;index<50;index++)world.Event(drawing,8,0);
        long dispatchBytes=GC.GetAllocatedBytesForCurrentThread();
        for(int index=0;index<1000;index++)world.Event(drawing,8,0);
        long perDispatch=(GC.GetAllocatedBytesForCurrentThread()-dispatchBytes)/1000;
        Check(perDispatch<=160,"draw dispatch reuses contexts and arguments: "+perDispatch+" bytes");
        long PerDispatch(string objectName)
        {
            var probe=world.Create(world.Constants[objectName].Int,2320,1440);
            for(int index=0;index<20;index++)world.Event(probe,8,0);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int index=0;index<200;index++)world.Event(probe,8,0);
            return (GC.GetAllocatedBytesForCurrentThread()-before)/200;
        }
        long surfaceDispatch=PerDispatch("objSwampSurface");
        Check(surfaceDispatch<=1500,"moss surface draw dispatch avoids key strings: "+surfaceDispatch+" bytes");
        var plainInstance=world.Create(world.Constants["objBlock"].Int,2320,1440,run:false);
        long idleBytes=GC.GetAllocatedBytesForCurrentThread();
        for(int index=0;index<1000;index++)world.Event(plainInstance,8,0);
        Check((GC.GetAllocatedBytesForCurrentThread()-idleBytes)/1000==0,"event dispatch without actions does not allocate");

        int pooledObject=world.Constants["objBlock"].Int;
        world.Call("object_event_add",[pooledObject,7,10,"var pooled_local;pooled_local=7;leak=0;"],context);
        world.Call("object_event_add",[pooledObject,7,11,"leak=0;if(pooled_local==7)leak=1;"],context);
        var pooledFirst=world.Create(pooledObject,2320,1440,run:false);
        var pooledSecond=world.Create(pooledObject,2352,1440,run:false);
        world.Event(pooledFirst,7,10);
        bool isolated=false;
        try{world.Event(pooledSecond,7,11);}catch(InvalidOperationException){isolated=true;}
        Check(isolated,"pooled event contexts start with fresh locals");
        world.Platform=null;
        return assertions;
    }
}

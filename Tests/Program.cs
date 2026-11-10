using SlackOfDescent;

var project=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../"));
var source=Environment.GetEnvironmentVariable("SLACK_SOURCE")??"/home/dpapyru/桌面/.game/BaiduNetdiskDownload/SlackOfDescent_Dev";
var world=new GmWorld(Path.Combine(project,"GameData"),source);
world.Evaluator.ResetBudget();
var self=new GmInstance{Id=999999,Object=-1};
world.Instances.Add(self);
var context=new GmContext(self);
int assertions=0;
assertions+=MotionGraphTests.Run(world);
assertions+=MotionPatchParserTests.Run(Path.Combine(project,"Examples","Mods","SinkKnee","sink-pull-knee.json"));
assertions+=ObjectPerformanceTests.Run(world);
assertions+=FixedClockTests.Run();
assertions+=CollisionOptimizationTests.Run(project,source);
assertions+=ScopeArrayAllocationTests.Run();
assertions+=GmlCallAllocationTests.Run(world);
assertions+=WaitingPathTests.Run(world);
assertions+=GmSlotLayoutTests.Run(world);
assertions+=GmCallSiteTests.Run(world);
assertions+=GmInstanceSdkTests.Run(world);
assertions+=GmActorSdkTests.Run(world);
assertions+=GmEventSdkTests.Run(world);
assertions+=StepSnapshotTests.Run(world);
assertions+=StepSkipTests.Run(world);
assertions+=RegionActivationTests.Run(world);
var engineRouting=new GmInstance();
world.Engine.Write("routing_array",99,[3]);
engineRouting.Scope.Write("routing_array",7,[1]);
engineRouting.Scope.Write("routing_array",11,[0]);
engineRouting.Scope.Write("routing_array",9,[3]);
Equal(world.Read(engineRouting,"routing_array",[1]),7,"undefined engine array gaps resolve to instance values");
Equal(world.Read(engineRouting,"routing_array",[3]),99,"defined engine array entries override instance values");
Equal(world.Read(engineRouting,"routing_array",[0]),11,"undefined engine array scalar alias resolves to instance value");
world.Write(engineRouting,"routing_array",17,[1]);
Equal(engineRouting.Scope.Read("routing_array",1),17,"engine array gaps route writes to instance");
Equal(world.Engine.Read("routing_array",1),0,"instance writes preserve implicit engine array gaps");
world.Write(engineRouting,"routing_array",19,[3]);
Equal(world.Engine.Read("routing_array",3),19,"defined engine array entries receive writes");
Equal(engineRouting.Scope.Read("routing_array",3),9,"engine writes preserve shadowed instance array values");
void Equal(GmValue actual,GmValue expected,string name)
{
    if(!actual.Equals(expected))throw new Exception($"{name}: expected {expected}, got {actual}");
    assertions++;
}
void Run(string code)=>world.Evaluator.Run(world.Compile(code),context);
var monitor=new GmFrameMonitor();
Equal(monitor.Update(0,0,1,30,true) is null,true,"frame monitor starts a room window");
for(int second=1;second<=9;second++)monitor.Update(second,second*30,1,30,true);
var measured=monitor.Update(10,295,1,30,true)!;
Equal(measured.LogicFps,25,"measured FPS reflects completed logic frames");
Equal(measured.Dropped,true,"five FPS drop triggers inclusively");
Equal(monitor.Update(11,320,1,30,true)!.Dropped,false,"continuous drop rate limited");
Equal(monitor.Update(15,420,1,30,true)!.Dropped,true,"continuous drop repeats after five seconds");
Equal(monitor.Update(16,450,2,30,true) is null,true,"room change resets baseline");
measured=monitor.Update(17,480,2,30,false)!;
Equal(measured.ProcessFps is null,true,"headless sample has no rendering FPS");
Equal(measured.LogicBaseline is null,true,"new room has no stale baseline");
Equal(monitor.Update(18,510,2,60,false) is null,true,"target change resets baseline");
monitor.Reset();Equal(monitor.Update(19,510,2,60,false) is null,true,"modal pause resets measurement");
var renderingMonitor=new GmFrameMonitor();long monitorFrame=0;double monitorTime=0;
renderingMonitor.Update(0,0,1,30,true);
GmFrameSample? renderingSample=null;
for(int second=0;second<10;second++)for(int tick=0;tick<60;tick++)
{
    monitorTime+=1.0/60;if(tick%2==0)monitorFrame++;
    renderingSample=renderingMonitor.Update(monitorTime,monitorFrame,1,30,true)??renderingSample;
}
Equal(renderingSample!.LogicBaseline is >29 and <31,true,"logic baseline uses actual thirty FPS cadence");
Equal(renderingSample.ProcessBaseline is >59 and <61,true,"process baseline tracks sixty FPS independently");
bool renderDropped=false;
for(int tick=0;tick<80;tick++){monitorTime+=1.0/40;if(tick%4!=0)monitorFrame++;renderingSample=renderingMonitor.Update(monitorTime,monitorFrame,1,30,true);renderDropped|=renderingSample?.Dropped==true;}
Equal(renderDropped,true,"render-only slowdown triggers a drop");
var layoutRoot=Path.Combine(Path.GetTempPath(),"slack-layout-test-"+Guid.NewGuid().ToString("N"));
try
{
    var managed=Path.Combine(layoutRoot,"data_SlackOfDescent_windows_x86_64");
    var assets=Path.Combine(managed,"GameData");
    Directory.CreateDirectory(assets);
    File.WriteAllText(Path.Combine(assets,"manifest.json"),"{}");
    Equal(GmPaths.FindData(layoutRoot,managed),assets,"export finds data beside managed assembly");
    var parser=Path.Combine(layoutRoot,"Tools",OperatingSystem.IsWindows()?"slack-extract.exe":"slack-extract");
    Directory.CreateDirectory(Path.GetDirectoryName(parser)!);File.WriteAllText(parser,"");
    Equal(GmPaths.FindCompiler(assets),parser,"nested data finds parser beside executable");
    var adjacent=Path.Combine(layoutRoot,"GameData");Directory.CreateDirectory(adjacent);
    File.WriteAllText(Path.Combine(adjacent,"manifest.json"),"{}");
    Equal(GmPaths.FindData(layoutRoot,managed),adjacent,"export prefers data beside executable");
    Equal(GmPaths.FindCompiler(adjacent),parser,"adjacent data finds packaged parser");
    bool missing=false;
    try{GmPaths.FindData(Path.Combine(layoutRoot,"missing"),Path.Combine(managed,"missing"));}catch(DirectoryNotFoundException){missing=true;}
    Equal(missing,true,"missing exported data reports failure");
}
finally{Directory.Delete(layoutRoot,true);}
Run("unicode_text=\"\u65e5\u672c\u8a9e\";");
Equal(self.Scope.Read("unicode_text"),"\u65e5\u672c\u8a9e","dynamic parser preserves UTF-8 strings");
var waits=new Queue<TaskCompletionSource<GmValue>>();
world.Wait=(name,args,c)=>
{
    var completion=new TaskCompletionSource<GmValue>();waits.Enqueue(completion);return new(completion.Task);
};
var suspended=world.Evaluator.RunAsync(world.Compile("async_stage=1; async_value=show_menu(\"one|two\",-1); async_stage=2; async_text=get_string(\"name\",\"Nana\"); async_stage=3;"),context);
Equal(suspended.IsCompleted,false,"menu yields execution");
Equal(self.Scope.Read("async_stage"),1,"menu retains execution position");
waits.Dequeue().SetResult(1);
Equal(self.Scope.Read("async_stage"),2,"menu resumes into next prompt");
Equal(self.Scope.Read("async_value"),1,"menu preserves selected index");
waits.Dequeue().SetResult("Nana");await suspended;
Equal(self.Scope.Read("async_stage"),3,"prompt resumes execution");
Equal(self.Scope.Read("async_text"),"Nana","prompt preserves response");
var nestedKey=world.Compile("return show_menu(\"one|two\",-1);");world.Scripts["test_waiting_script"]=nestedKey;
suspended=world.Evaluator.RunAsync(world.Compile("async_nested=test_waiting_script(); async_array[test_waiting_script()]=7; async_unary=!test_waiting_script();"),context);
waits.Dequeue().SetResult(-1);waits.Dequeue().SetResult(2);waits.Dequeue().SetResult(0);await suspended;
Equal(self.Scope.Read("async_nested"),-1,"nested script preserves cancellation");
Equal(self.Scope.Read("async_array",[2]),7,"waiting array reference retains result");
Equal(self.Scope.Read("async_unary"),1,"waiting unary expression resumes");
suspended=world.Evaluator.RunAsync(world.Compile("async_count=0; repeat(3){sleep(1); if(async_count==1)break; async_count+=1;} async_after=1;"),context);
waits.Dequeue().SetResult(0);waits.Dequeue().SetResult(0);await suspended;
Equal(self.Scope.Read("async_count"),1,"break exits waiting loop");
Equal(self.Scope.Read("async_after"),1,"waiting break preserves following statement");
world.Wait=null;
world.Platform=(name,args,c)=>name=="sound_play"?new GmValue(0):(GmValue?)null;
foreach(bool menuCompiled in new[]{false,true})
{
    world.Evaluator.CompiledExecution=menuCompiled;
    foreach(int cursor in new[]{1,2})
    {
        var menu=new GmInstance{Object=world.Constants["objTitle"].Int};
        world.Evaluator.ResetBudget();
        world.Evaluator.Run(world.Compile("up=0;down=0;left=0;right=0;decide=2;cancel=0;b1=0;b2=0;keyAvailable=1;View=8;VIEW_TITLE=0;VIEW_GAMESTART=1;VIEW_GAMESTART_CC=8;VIEW_STAGESELECT=3;NEWGAME=0;STAGESELECT=1;GAMESTART_CC_BACK=2;"),new(menu));
        menu.Scope.Write("Cursor",cursor);
        await world.Evaluator.RunAsync("objects/objTitle/3/0/1",new(menu));
        Equal(menu.Scope.Read("View"),cursor==1?3:1,"Crackle Cradle menu handles selection without undefined legacy case "+menuCompiled);
        Equal(menu.Scope.Read("Cursor"),0,"Crackle Cradle submenu resets cursor "+menuCompiled);
    }
}
world.Evaluator.CompiledExecution=true;
world.Platform=null;
var cooperative=new GmWorld(Path.Combine(project,"GameData"),source);
var cooperativeContext=new GmContext(new GmInstance());
int cooperativeParent=cooperative.Call("object_add",[],cooperativeContext).Int;
int cooperativeChild=cooperative.Call("object_add",[],cooperativeContext).Int;
cooperative.Call("object_set_parent",[cooperativeChild,cooperativeParent],cooperativeContext);
cooperative.Call("object_event_add",[cooperativeParent,7,10,"parent_started=1;sleep(1);parent_finished=1;"],cooperativeContext);
cooperative.Call("object_event_add",[cooperativeChild,7,10,"event_inherited();child_finished=1;"],cooperativeContext);
cooperative.Call("object_event_add",[cooperativeChild,0,0,"created_before=1;sleep(1);created_after=1;"],cooperativeContext);
cooperative.Call("object_event_add",[cooperativeChild,1,0,"destroy_before=1;sleep(1);destroy_after=1;"],cooperativeContext);
cooperative.Wait=(name,args,c)=>{var completion=new TaskCompletionSource<GmValue>();waits.Enqueue(completion);return new(completion.Task);};
cooperative.Evaluator.ResetBudget();
var actor=cooperative.Create(cooperativeChild,0,0,run:false);
var inherited=cooperative.EventAsync(actor,7,10);
Equal(inherited.IsCompleted,false,"inherited event yields to host");
Equal(actor.Scope.Read("parent_started"),1,"inherited event starts before suspension");
Equal(actor.Scope.Values.ContainsKey("child_finished"),false,"caller waits for inherited event");
waits.Dequeue().SetResult(0);await inherited;
Equal(actor.Scope.Read("child_finished"),1,"caller resumes after inherited event");
var createTask=cooperative.Evaluator.CallAsync("instance_create",[0,0,cooperativeChild],cooperativeContext);
Equal(createTask.IsCompleted,false,"create event yields to host");
var createdActor=cooperative.Instances.Last();
Equal(createdActor.Scope.Read("created_before"),1,"create event starts before suspension");
waits.Dequeue().SetResult(0);Equal(await createTask,createdActor.Id,"create resumes with original instance id");
Equal(createdActor.Scope.Read("created_after"),1,"create event completes after wait");
var destroyTask=cooperative.Evaluator.CallAsync("instance_destroy",[],new(createdActor));
Equal(createdActor.Destroyed,false,"destroy waits for destroy event");
waits.Dequeue().SetResult(0);await destroyTask;
Equal(createdActor.Destroyed,true,"destroy marks instance after event");
Equal(createdActor.Scope.Read("destroy_after"),1,"destroy event completes after wait");
Run("var i; sum=0; for(i=0;i<5;i+=1){if(i==2)continue;sum+=i;} result=sum;");
Equal(self.Scope.Read("result"),8,"for continue");
Run("result=0; switch(2){case 1:result=1;break;case 2:result=2;case 3:result+=3;break;default:result=99;}");
Equal(self.Scope.Read("result"),5,"switch fallthrough");
Run("global.values[3,2]=17; result=global.values[3,2];");
Equal(self.Scope.Read("result"),17,"global 2d array");
Run("value[0]=11; result=value;");
Equal(self.Scope.Read("result"),11,"scalar and array zero");
Run("value[4]=9; result=value[2]; alias=value[0,4];");
Equal(self.Scope.Read("result"),0,"sparse array gap");
Equal(self.Scope.Read("alias"),9,"one and two dimensional row zero");
Run("result=7 div 2; mask=(1<<4)|3;");
Equal(self.Scope.Read("result"),3,"integer division");
Equal(self.Scope.Read("mask"),19,"bitwise shift");
Run("global.test=0; with(noone){global.test=1;}");
Equal(world.Globals.Read("test"),0,"with noone");
bool failed=false;
try{Run("result=missing_variable;");}catch(InvalidOperationException){failed=true;}
if(!failed)throw new Exception("Uninitialized variables must fail");
assertions++;
foreach(bool compiledExecution in new[]{false,true})
{
    world.Evaluator.CompiledExecution=compiledExecution;
    Run("result=(0.1+0.2==0.3); less=(0.1+0.2>0.3); matched=0; switch(0.3+0.00000000000001){case 0.3:matched=1;break;}");
    Equal(self.Scope.Read("result"),true,"GML approximate equality "+compiledExecution);
    Equal(self.Scope.Read("less"),false,"GML comparison tolerance "+compiledExecution);
    Equal(self.Scope.Read("matched"),0,"GML switch retains exact numeric matching "+compiledExecution);
    Run("result=debug_mode;");
    Equal(self.Scope.Read("result"),false,"release debug mode "+compiledExecution);
    bool readOnly=false;
    try{Run("debug_mode=1;");}catch(InvalidOperationException){readOnly=true;}
    Equal(readOnly,true,"debug mode is read only "+compiledExecution);
}
world.Evaluator.CompiledExecution=true;
var cleanup=new GmWorld(Path.Combine(project,"GameData"),source);
var cleanupContext=new GmContext(new GmInstance());
int cleanupObject=cleanup.Call("object_add",[],cleanupContext).Int;
cleanup.Globals.Write("EnemyName","RuntimeTest",[0]);
cleanup.Globals.Write("EnemyObject",cleanupObject,[0]);
cleanup.Evaluator.ResetBudget();
await cleanup.Evaluator.CallAsync("deleteEnemy",[0],cleanupContext);
Equal(cleanup.Call("object_exists",[cleanupObject],cleanupContext),false,"enemy cleanup deletes loaded object");
Equal(cleanup.Globals.Read("EnemyName",[0]),"","enemy cleanup resets name");
Equal(cleanupContext.Self.Scope.Values.ContainsKey("debug_mode"),false,"enemy cleanup uses engine debug mode");
bool outOfRange=false;
try{Run("result=value[5];");}catch(InvalidOperationException){outOfRange=true;}
Equal(outOfRange,true,"sparse array bounds");
Run("result=0; repeat(4){result+=1;} do {result-=1;} until(result==1);");
Equal(self.Scope.Read("result"),1,"repeat and until");
var before=File.GetLastWriteTimeUtc(Path.Combine(source,"Cfg/GameSetting"));
var output=world.Resolve("Cfg/GameSetting",true);
if(output.StartsWith(source,StringComparison.Ordinal))throw new Exception("Writes must not target source assets");
Equal(File.GetLastWriteTimeUtc(Path.Combine(source,"Cfg/GameSetting"))==before,true,"source immutable");
var packaged=new GmWorld(Path.Combine(project,"GameData"),Path.Combine(project,"GameData/assets"));
var packagedContext=new GmContext(new GmInstance());
var characterFiles=new List<string>();
string characterFile=packaged.Call("file_find_first",["chara/doll/head/*.GIF",16],packagedContext).ToString();
while(characterFile.Length>0){characterFiles.Add(characterFile);characterFile=packaged.Call("file_find_next",[],packagedContext).ToString();}
Equal(characterFiles.Count>10,true,"packaged character scan finds converted GIF resources");
Equal(characterFiles.Any(n=>n.StartsWith("Character-Nana_",StringComparison.OrdinalIgnoreCase)),true,"packaged character scan includes Nana");
Equal(characterFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count(),characterFiles.Count,"packaged character scan has no duplicates");
Equal(packaged.Call("file_find_first",["Chara/Doll/Head/NoSuchCharacter*.gif",16],packagedContext),"","packaged character scan filters pattern");
Equal(packaged.Call("file_find_first",[Path.Combine(packaged.SourceRoot,"Chara/Doll/Head/Character-Nana_*.gif"),16],packagedContext).ToString().StartsWith("Character-Nana_",StringComparison.OrdinalIgnoreCase),true,"packaged character scan accepts absolute source path");
var caution=world.Constants["objCaution"].Int;
var instance=world.Create(caution,50,100);
Equal(instance.Scope.Read("alarm",[0]),90,"original DnD alarm action");
var variableAction=world.Manifest.GetProperty("objects").EnumerateArray().SelectMany(o=>o.GetProperty("events").EnumerateArray()).SelectMany(e=>e.GetProperty("actions").EnumerateArray()).First(a=>a.GetProperty("kind").GetInt32()==6);
var compiled=world.Code[variableAction.GetProperty("code").GetString()!];
Equal(compiled[1].GetArrayLength()>0,true,"variable action contains assignment");
var actionRoot=Path.Combine(Path.GetTempPath(),"slack-actions-"+Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(actionRoot);
    var actionManifest=System.Text.Json.Nodes.JsonNode.Parse(world.Manifest.GetRawText())!;
    var actionObject=actionManifest["objects"]!.AsArray().First(o=>o!["id"]!.GetValue<int>()==caution)!;
    object Action(int id,int execution,string function,object[] parameters,bool condition=false,int kind=0)=>new{id,kind,execution,target=-1,condition,invert=false,relative=false,function,code=(string?)null,parameters};
    var actions=new[]{
        Action(408,1,"action_if",[new object[]{"id","pass"}],true),
        Action(605,0,"",[]),
        Action(0,1,"action_set_hspeed",[new object[]{"num",17}]),
        Action(0,0,"",[],kind:3),
        Action(605,0,"",[]),
        Action(0,1,"action_set_hspeed",[new object[]{"num",23}]),
        Action(0,1,"action_set_vspeed",[new object[]{"num",5}])};
    actionObject["events"]=System.Text.Json.JsonSerializer.SerializeToNode(new[]{new{kind=7,sub=10,actions}});
    File.WriteAllText(Path.Combine(actionRoot,"manifest.json"),actionManifest.ToJsonString());
    File.Copy(Path.Combine(project,"GameData/code.json"),Path.Combine(actionRoot,"code.json"));
    var actionWorld=new GmWorld(actionRoot,source);
    var actionInstance=actionWorld.Create(caution,0,0,run:false);
    foreach(bool asynchronous in new[]{false,true})foreach(bool passed in new[]{false,true})
    {
        actionInstance.Scope.Write("pass",passed);
        actionWorld.Evaluator.ResetBudget();
        if(asynchronous)await actionWorld.EventAsync(actionInstance,7,10);else actionWorld.Event(actionInstance,7,10);
        Equal(actionInstance.Scope.Read("hspeed"),passed?17:23,"comments do not consume conditional or else body "+asynchronous);
        Equal(actionInstance.Scope.Read("vspeed"),5,"statement after conditional remains unconditional "+asynchronous);
    }
}
finally{Directory.Delete(actionRoot,true);}
var colliderA=world.Create(caution,0,0,run:false);
var colliderB=world.Create(caution,0,0,run:false);
Equal(world.Call("place_meeting",[0,0,caution],new(colliderA)),true,"precise overlap");
Equal(world.Call("place_meeting",[100000,100000,caution],new(colliderA)),false,"separated masks");
var sparseMask=new GmMask(System.Text.Json.JsonSerializer.SerializeToElement(new{width=3,height=2,left=0,right=2,top=0,bottom=1,data="100011"}),1,1);
var sparseInstance=new GmInstance();sparseInstance.Scope.Write("x",100);sparseInstance.Scope.Write("y",200);
var sampler=new GmMaskSampler(sparseMask,sparseInstance);
Equal(sampler.Opaque(99.5,199.5),true,"transformed mask top left pixel");
Equal(sampler.Opaque(100.5,199.5),false,"transformed mask transparent pixel");
Equal(sampler.Opaque(101.5,200.5),true,"transformed mask bottom right pixel");
Equal(sampler.Opaque(102,200),false,"transformed mask excludes right boundary");
sparseInstance.Scope.Write("image_xscale",-2);sparseInstance.Scope.Write("image_yscale",2);
sampler=new GmMaskSampler(sparseMask,sparseInstance);
Equal(sampler.Opaque(101,199),true,"mirrored scaled mask foreground");
Equal(sampler.Opaque(99,199),false,"mirrored scaled mask hole");
sparseInstance.Scope.Write("image_angle",90);
sampler=new GmMaskSampler(sparseMask,sparseInstance);
Equal(sampler.Opaque(99,199),true,"rotated mirrored mask foreground");
Equal(sampler.Opaque(99,201),false,"rotated mirrored mask hole");
sparseInstance.Scope.Write("image_xscale",0);
sampler=new GmMaskSampler(sparseMask,sparseInstance);
Equal(sampler.Opaque(100,200),false,"zero scale mask is empty");
int dynamicObject=world.Call("object_add",[],context).Int;
world.Call("object_set_parent",[dynamicObject,caution],context);
world.Call("object_set_mask",[dynamicObject,instance.Scope.Read("mask_index")],context);
world.Call("object_event_add",[dynamicObject,0,0,"event_inherited(); created=1;"],context);
world.Call("object_event_add",[dynamicObject,0,0,"created+=2;"],context);
var dynamicInstance=world.Create(dynamicObject,400,400);
Equal(dynamicInstance.Scope.Read("created"),3,"dynamic events append");
Equal(dynamicInstance.Scope.Read("alarm",[0]),90,"dynamic event inheritance");
world.Call("object_event_clear",[dynamicObject,0,0],context);
world.Call("object_event_add",[dynamicObject,0,0,"replacement=7;"],context);
var replacement=world.Create(dynamicObject,450,450);
Equal(replacement.Scope.Read("replacement"),7,"cleared event replacement");
Equal(replacement.Scope.Read("alarm",[0]),-1,"cleared event suppresses parent");
int collisionObject=world.Call("object_add",[],context).Int;
world.Call("object_set_sprite",[collisionObject,instance.Scope.Read("sprite_index")],context);
world.Call("object_event_add",[collisionObject,4,caution,"contact=other.id;"],context);
var collisionInstance=world.Create(collisionObject,0,0);
world.Instances.Remove(self);
world.Step();
Equal(collisionInstance.Scope.Read("contact"),colliderB.Id,"collision event other context");
var dispatch=new GmWorld(Path.Combine(project,"GameData"),source);
var dispatchContext=new GmContext(new GmInstance());
int dispatchParent=dispatch.Call("object_add",[],dispatchContext).Int;
int dispatchChild=dispatch.Call("object_add",[],dispatchContext).Int;
int dispatchActor=dispatch.Call("object_add",[],dispatchContext).Int;
int dispatchSprite=dispatch.RegisterSprite(System.Text.Json.JsonSerializer.SerializeToElement(new{originX=0,originY=0,frames=new[]{new{width=2,height=2,path="unused"}},masks=new[]{new{width=2,height=2,left=0,right=1,top=0,bottom=1,data="1111"}}}));
dispatch.Call("object_set_parent",[dispatchChild,dispatchParent],dispatchContext);
foreach(int obj in new[]{dispatchParent,dispatchChild,dispatchActor})dispatch.Call("object_set_sprite",[obj,dispatchSprite],dispatchContext);
dispatch.Call("object_event_add",[dispatchActor,4,dispatchParent,"global.contacts+=string(other.id)+\",\"; if(other.id==global.first){x=-1024; collision_point(x,y,all,1,1);}"],dispatchContext);
var dispatchSelf=dispatch.Create(dispatchActor,0,-256,run:false);
var dispatchFirst=dispatch.Create(dispatchChild,0,-256,run:false);
var dispatchSecond=dispatch.Create(dispatchChild,-1024,-256,run:false);
dispatch.Globals.Write("first",dispatchFirst.Id);dispatch.Globals.Write("contacts","");
dispatch.Step();
Equal(dispatch.Globals.Read("contacts"),$"{dispatchFirst.Id},{dispatchSecond.Id},","collision callback nested query cannot consume outer geometry change");
foreach(string mutation in new[]{"create","destroy","deactivate","type","parent","move","large","solid"})
{
    var w=new GmWorld(Path.Combine(project,"GameData"),source);
    var c=new GmContext(new GmInstance());
    int parent=w.Call("object_add",[],c).Int,child=w.Call("object_add",[],c).Int,actorType=w.Call("object_add",[],c).Int,unrelated=w.Call("object_add",[],c).Int;
    int sprite=w.RegisterSprite(System.Text.Json.JsonSerializer.SerializeToElement(new{originX=0,originY=0,frames=new[]{new{width=2,height=2,path="unused"}},masks=new[]{new{width=2,height=2,left=0,right=1,top=0,bottom=1,data="1111"}}}));
    w.Call("object_set_parent",[child,parent],c);
    foreach(int obj in new[]{parent,child,actorType,unrelated})w.Call("object_set_sprite",[obj,sprite],c);
    var collisionActor=w.Create(actorType,-512,-256,run:false);
    var first=w.Create(child,-512,-256,run:false);
    var second=w.Create(child,-512,-256,run:false);
    var outsider=w.Create(unrelated,-512,-256,run:false);
    w.Globals.Write("contacts","");w.Globals.Write("first",first.Id);w.Globals.Write("second",second.Id);w.Globals.Write("outsider",outsider.Id);
    string change=mutation switch
    {
        "create"=>$"instance_create(x,y,{child});",
        "destroy"=>"with(global.second) instance_destroy();",
        "deactivate"=>"instance_deactivate_object(global.second);",
        "type"=>$"with(global.second) instance_change({unrelated},false);with(global.outsider) instance_change({child},false);",
        "parent"=>$"object_set_parent({child},noone);",
        "move"=>"x=-2048;with(global.second) x=-2048;collision_point(x,y,all,1,1);",
        "large"=>"with(global.second){x=-4096;image_xscale=4096;}collision_point(x,y,all,1,1);",
        "solid"=>"collision_point(x,y,all,1,1);",
        _=>""
    };
    if(mutation=="solid")
    {
        collisionActor.Scope.Write("hspeed",128);first.Scope.Write("x",-384);first.Scope.Write("solid",true);
    }
    w.Call("object_event_add",[actorType,4,parent,"global.contacts+=string(other.id)+\",\";if(other.id==global.first){"+change+"}"],c);
    w.Step();
    Equal(w.Globals.Read("contacts"),mutation=="destroy"?$"{first.Id},":$"{first.Id},{second.Id},","collision snapshot survives callback "+mutation);
    long rebuilt=w.CollisionSnapshotRebuilds;
    w.Globals.Write("contacts","");w.Step();
    if(mutation is "destroy" or "deactivate" or "type" or "parent" or "create")
        Equal(w.CollisionSnapshotRebuilds>rebuilt,true,"collision snapshot invalidated after "+mutation);
    if(mutation is "destroy" or "deactivate" or "parent")
        Equal(w.Globals.Read("contacts").ToString().Contains(second.Id.ToString()+","),false,"later collision snapshot excludes "+mutation);
}
assertions+=MotionArrayTests.Run(world,dynamicObject);
assertions+=DoubleDashTests.Run();
assertions+=FrameSeparationTests.Run();
assertions+=FrameBenchmarkTests.Run();
assertions+=FrameProfileTests.Run();
assertions+=DrawCullTests.Run();
assertions+=GmValueAdditionTests.Run();
assertions+=CharacterBaseTests.Run();
assertions+=ActionEditorTests.Run();
var originalMotion=world.Create(dynamicObject,500,500,run:false);
var nativeMotion=world.Create(dynamicObject,500,500,run:false);
var originalPart=world.Create(dynamicObject,500,500,run:false);
var nativePart=world.Create(dynamicObject,500,500,run:false);
originalMotion.Scope.Write("Process",originalPart.Id,[0]);nativeMotion.Scope.Write("Process",nativePart.Id,[0]);
GmValue[] motionArgs=[2,3,1,2,2];
world.Evaluator.ResetBudget();world.Evaluator.Run(world.Scripts["MotionInitialize"],new(originalMotion,args:motionArgs));
world.Call("MotionInitialize",motionArgs,new(nativeMotion));
foreach(var pair in originalMotion.Scope.Values.Where(p=>p.Key!="Process"))Equal(nativeMotion.Scope.Read(pair.Key),pair.Value,"motion state "+pair.Key);
foreach(var pair in originalPart.Scope.Values)Equal(nativePart.Scope.Read(pair.Key),pair.Value,"motion part "+pair.Key);
var motionPath="Chara/Motion/RuntimeTest.txt";
world.External[motionPath]=world.Compile("data_motLong=2;data_motStep[0]=3;with(Process[0]){data_motPartAngle[1]=45;}");
GmValue[] loadArgs=[1,"RuntimeTest"];
world.Evaluator.ResetBudget();world.Evaluator.Run(world.Scripts["setMotionForPlayer"],new(originalMotion,args:loadArgs));
world.Call("setMotionForPlayer",loadArgs,new(nativeMotion));
foreach(var pair in originalMotion.Scope.Values.Where(p=>p.Key!="Process"))Equal(nativeMotion.Scope.Read(pair.Key),pair.Value,"motion load state "+pair.Key);
foreach(var pair in originalPart.Scope.Values)Equal(nativePart.Scope.Read(pair.Key),pair.Value,"motion load part "+pair.Key);
var argumentContext=new GmContext(nativeMotion,args:[12,"filename"]);
world.Evaluator.Run(world.Compile("indexed=argument[0];filename=argument[1];missing=argument[4];argument[0]=22;"),argumentContext);
Equal(nativeMotion.Scope.Read("indexed"),12,"indexed argument number");
Equal(nativeMotion.Scope.Read("filename"),"filename","indexed argument string");
Equal(nativeMotion.Scope.Read("missing"),0,"missing optional argument");
Equal(argumentContext.Arguments[0],22,"argument array assignment");
world.Evaluator.Run(world.Compile("break; unreachable=1;"),argumentContext);
Equal(nativeMotion.Scope.Values.ContainsKey("unreachable"),false,"script boundary consumes break");
Equal(world.Call("object_get_parent",[nativeMotion.Id],context),-1,"invalid object parent returns self selector");
int deactivateObject=world.Call("object_add",[],context).Int;
world.Call("object_event_add",[deactivateObject,3,0,"instance_deactivate_object(id);"],context);
world.Call("object_event_add",[deactivateObject,3,2,"unexpected_end=1;"],context);
var deactivated=world.Create(deactivateObject,0,0);world.Step();
Equal(deactivated.Scope.Values.ContainsKey("unexpected_end"),false,"deactivation suppresses later events");
var input=new GmInput();
var bindingRoot=Path.Combine(Path.GetTempPath(),"slack-binding-test-"+Guid.NewGuid().ToString("N"));
try
{
    var bindingWorld=new GmWorld(Path.Combine(project,"GameData"),Path.Combine(project,"GameData/assets"),bindingRoot);
    input.LoadBindings(bindingWorld);Equal(input.SecondaryKey,71,"old configs default secondary fire to G");
    input.Key(bindingWorld,71,true);Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,true,"G triggers secondary fire");
    input.EndStep();Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,false,"held G does not repeat");
    input.Clear(bindingWorld);input.Mouse(bindingWorld,3,true);
    Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,true,"middle triggers keyboard mouse secondary fire");
    bindingWorld.Globals.Write("controllMode",1);Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,false,"middle disabled in keyboard only mode");
    input.SaveBinding(bindingWorld,72);var restored=new GmInput();restored.LoadBindings(bindingWorld);
    Equal(restored.SecondaryKey,72,"secondary binding persists");input.Clear(bindingWorld);
    input.Key(bindingWorld,71,true);Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,false,"previous secondary binding no longer fires");
    input.Key(bindingWorld,72,true);Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,true,"custom secondary binding fires");
    input.Clear(bindingWorld);Equal(input.Call("slack_secondary_fire",[],bindingWorld)!.Value,false,"modal clear discards secondary press");
}
finally{Directory.Delete(bindingRoot,true);input=new GmInput();}
GmValue Query(string name,int code)=>input.Call(name,[code],world)!.Value;
Equal(Query("mouse_check_button",0),true,"mouse no button initially");
Equal(Query("mouse_check_button",-1),false,"mouse any button initially");
input.Mouse(world,2,true);
Equal(Query("mouse_check_button",-1),true,"mouse any right button");
Equal(Query("mouse_check_button",1),false,"right button does not imply left");
Equal(Query("mouse_check_button_pressed",0),false,"mouse no pressed button");
input.EndStep();
Equal(Query("mouse_check_button_pressed",0),true,"mouse press cleared at step boundary");
Equal(Query("mouse_check_button",2),true,"mouse held survives step boundary");
input.Mouse(world,2,false);
Equal(Query("mouse_check_button_released",-1),true,"mouse release any");
input.Key(world,65,true);input.Key(world,66,true);input.Key(world,65,false);
Equal(world.Engine.Read("keyboard_key"),66,"release other key preserves current key");
Equal(world.Engine.Read("keyboard_lastkey"),66,"release preserves last pressed key");
input.Call("io_clear",[],world);
Equal(Query("keyboard_check",0),true,"clear releases keyboard");
Equal(world.Engine.Read("mouse_lastbutton"),0,"clear resets last mouse button");
input.Poll(world);
Equal(Query("keyboard_check",66),true,"poll restores physically held key after io clear");
Equal(Query("keyboard_check_pressed",66),false,"poll does not repeat held press");
input.Key(world,66,true);
Equal(Query("keyboard_check_pressed",66),false,"duplicate down does not repeat press");
input.QueueKey(83,true);input.Call("io_clear",[],world);
Equal(Query("keyboard_check",83),false,"queued input waits for polling");
input.Poll(world);
Equal(Query("keyboard_check_pressed",83),true,"pending press survives script io clear");
input.EndStep();input.QueueKey(83,false);input.Poll(world);
Equal(Query("keyboard_check_released",83),true,"pending release reaches next poll");
input.QueueMouse(1,true);input.Poll(world);input.Call("io_clear",[],world);input.Poll(world);
Equal(Query("mouse_check_button",1),true,"poll restores physically held mouse");
Equal(Query("mouse_check_button_pressed",1),false,"held mouse does not repeat press");
input.Clear(world);input.Poll(world);
Equal(Query("keyboard_check",1),false,"modal clear releases physical keyboard state");
Equal(Query("mouse_check_button",-1),false,"modal clear releases physical mouse state");
var inputWorld=new GmWorld(Path.Combine(project,"GameData"),source);
int inputObject=inputWorld.Call("object_add",[],context).Int;
inputWorld.Call("object_set_sprite",[inputObject,instance.Scope.Read("sprite_index")],context);
foreach(var ev in new[]{(5,65,"key"),(9,65,"press"),(10,65,"release"),(6,0,"local"),(6,4,"localpress"),(6,50,"globalheld"),(6,53,"globalpress"),(6,60,"scroll")})
    inputWorld.Call("object_event_add",[inputObject,ev.Item1,ev.Item2,ev.Item3+"+=1;"],context);
var inputInstance=inputWorld.Create(inputObject,0,0);
foreach(string field in new[]{"key","press","release","local","localpress","globalheld","globalpress","scroll"})inputInstance.Scope.Write(field,0);
var inputBounds=inputWorld.Bounds(inputInstance);
var hit=(from y in Enumerable.Range((int)inputBounds.Top,(int)(inputBounds.Bottom-inputBounds.Top+1))
         from x in Enumerable.Range((int)inputBounds.Left,(int)(inputBounds.Right-inputBounds.Left+1))
         where inputWorld.MouseOver(inputInstance,x,y) select (x,y)).First();
inputWorld.Engine.Write("mouse_x",hit.x);inputWorld.Engine.Write("mouse_y",hit.y);
input.Key(inputWorld,65,true);input.Mouse(inputWorld,1,true);input.Wheel=1;
inputWorld.Evaluator.ResetBudget();
input.Dispatch(inputWorld);
foreach(string field in new[]{"key","press","local","localpress","globalheld","globalpress","scroll"})Equal(inputInstance.Scope.Read(field),1,"input event "+field);
input.EndStep();inputWorld.Engine.Write("mouse_x",100000);input.Dispatch(inputWorld);
Equal(inputInstance.Scope.Read("local"),1,"local mouse requires collision");
Equal(inputInstance.Scope.Read("globalheld"),2,"global mouse ignores collision");
Equal(inputInstance.Scope.Read("press"),1,"key press fires once");
input.Key(inputWorld,65,false);input.Dispatch(inputWorld);
Equal(inputInstance.Scope.Read("release"),1,"key release event");
var frameWorld=new GmWorld(Path.Combine(project,"GameData"),source);
var orderedWorld=new GmWorld(Path.Combine(project,"GameData"),source);
int actorObject=orderedWorld.Call("object_add",[],context).Int;
int controlObject=orderedWorld.Call("object_add",[],context).Int;
orderedWorld.Call("object_event_add",[actorObject,3,0,"global.motion_end=1;"],context);
orderedWorld.Call("object_event_add",[actorObject,3,2,"global.motion_end=0;"],context);
orderedWorld.Call("object_event_add",[controlObject,3,0,"observed=global.motion_end;"],context);
orderedWorld.Globals.Write("motion_end",0);
var orderedController=orderedWorld.Create(controlObject,0,0);
orderedWorld.Create(actorObject,0,0);
orderedWorld.Step();
Equal(orderedController.Scope.Read("observed"),1,"controller observes same-frame motion end in object order");
Equal(orderedWorld.Globals.Read("motion_end"),0,"motion end reset after controller");
orderedWorld.Call("object_event_add",[actorObject,3,1,"instance_activate_object(global.controller);"],context);
orderedWorld.Call("object_event_add",[controlObject,3,1,"activated_begin=1;"],context);
orderedWorld.Globals.Write("controller",orderedController.Id);
orderedController.Active=false;
orderedWorld.Step();
Equal(orderedController.Scope.Read("activated_begin"),1,"begin step activation includes later object in same phase");
int spawnedObject=orderedWorld.Call("object_add",[],context).Int;
orderedWorld.Call("object_event_add",[spawnedObject,3,1,"initialized=1; if(!global.spawned_same){global.spawned_same=1;instance_create(x,y,object_index);}"],context);
orderedWorld.Globals.Write("spawned_object",spawnedObject);orderedWorld.Globals.Write("spawned_same",0);
orderedWorld.Call("object_event_add",[actorObject,3,1,"instance_create(0,0,global.spawned_object);"],context);
orderedWorld.Step();
var spawned=orderedWorld.Select(spawnedObject,context).ToArray();
Equal(spawned.Length,2,"begin step can create later object and same-object instance");
Equal(spawned[0].Scope.Read("initialized"),1,"new later object receives begin step in same phase");
Equal(spawned[1].Scope.Values.ContainsKey("initialized"),false,"same-object creation waits for next begin step");
int frameObject=frameWorld.Call("object_add",[],context).Int;
frameWorld.Call("object_set_sprite",[frameObject,instance.Scope.Read("sprite_index")],context);
frameWorld.Call("object_event_add",[frameObject,3,2,"end_index=image_index;"],context);
frameWorld.Call("object_event_add",[frameObject,8,0,"draw_index=image_index; draw_end=end_index; draws+=1;"],context);
frameWorld.Call("object_event_add",[frameObject,7,7,"animation_index=image_index; ends+=1;"],context);
var frameInstance=frameWorld.Create(frameObject,0,0);
frameInstance.Scope.Write("draws",0);frameInstance.Scope.Write("ends",0);
int frameCount=frameWorld.Sprites[frameInstance.Scope.Read("sprite_index").Int].GetProperty("frames").GetArrayLength();
frameInstance.Scope.Write("image_speed",frameCount*2.25);
frameWorld.Redraw=()=>frameWorld.Event(frameInstance,8,0);
frameWorld.Step();
Equal(frameInstance.Scope.Read("draws"),1,"draw once per game step");
Equal(frameInstance.Scope.Read("draw_index"),0,"draw before animation advance");
Equal(frameInstance.Scope.Read("draw_end"),0,"end step before draw");
Equal(frameInstance.Scope.Read("image_index"),frameCount*1.25,"animation subtracts frame count once");
Equal(frameInstance.Scope.Read("animation_index"),frameCount*1.25,"animation end sees advanced index");
Equal(frameInstance.Scope.Read("ends"),1,"animation end once on overrun");
frameInstance.Scope.Write("image_index",0);frameInstance.Scope.Write("image_speed",-1);
frameWorld.Step();
Equal(frameInstance.Scope.Read("image_index"),-1,"negative animation index is preserved");
Equal(frameInstance.Scope.Read("ends"),1,"negative animation does not fire end event");
var drawPlan=new GmFramePlan();
frameWorld.FramePlan=drawPlan;
frameInstance.Scope.Write("image_speed",0.125);
frameInstance.Scope.Write("image_index",0);
frameInstance.Scope.Write("draws",0);
var drawIndices=new List<double>();
int drawChildObject=frameWorld.Call("object_add",[],context).Int;
frameWorld.Call("object_event_add",[drawChildObject,0,0,"global.draw_children+=1;"],context);
frameWorld.Call("object_event_add",[drawChildObject,1,0,"global.draw_destroyed+=1;"],context);
frameWorld.Globals.Write("draw_children",0);frameWorld.Globals.Write("draw_destroyed",0);
frameWorld.Redraw=()=>
{
    frameWorld.Event(frameInstance,8,0);
    drawIndices.Add(frameInstance.Scope.Read("draw_index").Number);
    var child=frameWorld.Create(drawChildObject,0,0);
    frameWorld.Call("instance_destroy",[],new(child));
};
for(int step=0;step<3;step++)frameWorld.Step();
Equal(frameInstance.Scope.Read("draws"),3,"pending presentation preserves every logic draw");
Equal(drawIndices.SequenceEqual(new double[]{0,0.125,0.25}),true,"each draw observes its own pre-animation pose");
Equal(frameWorld.Globals.Read("draw_children"),3,"draw creates instances every logic step");
Equal(frameWorld.Globals.Read("draw_destroyed"),3,"draw destroys instances every logic step");
Equal(frameWorld.Instances.Any(i=>i.Object==drawChildObject),false,"draw destruction is cleaned up before the step ends");
Equal(drawPlan.Consume() is not null,true,"multiple logic draws leave one presentation pending");
Equal(drawPlan.Consume() is null,true,"zero logic steps leave no new presentation");
Equal(frameInstance.Scope.Read("draws"),3,"presentation consumes never advance draw events");
int drawRoom=900778;
frameWorld.Rooms[drawRoom]=System.Text.Json.JsonSerializer.SerializeToElement(new
{
    name="draw-order-room",width=64,height=64,speed=60,colour=0,viewsEnabled=false,clearScreen=false,
    tiles=Array.Empty<object>(),backgrounds=Array.Empty<object>(),views=Array.Empty<object>(),
    instances=Array.Empty<object>(),code=frameWorld.Compile("return 0;")
});
frameWorld.Call("object_event_add",[frameObject,7,5,"global.room_exit_draws=draws; global.room_exit_index=image_index;"],context);
int beforeDrawRoom=frameWorld.CurrentRoom;
frameWorld.Redraw=()=>
{
    frameWorld.Event(frameInstance,8,0);
    Equal(frameWorld.CurrentRoom,beforeDrawRoom,"draw runs before its requested room change");
    frameWorld.PendingRoom=drawRoom;
};
frameWorld.Step();
Equal(frameWorld.Globals.Read("room_exit_draws"),4,"room exit follows the final draw event");
Equal(frameWorld.Globals.Read("room_exit_index"),0.5,"animation advances before room exit");
Equal(frameWorld.CurrentRoom,drawRoom,"draw-requested room change completes in the same step");
Equal(frameWorld.Instances.Contains(frameInstance),false,"room change removes the drawn transient actor");
var boundaryWorld=new GmWorld(Path.Combine(project,"GameData"),source);
int boundaryObject=boundaryWorld.Call("object_add",[],context).Int;
foreach(int sub in new[]{0,1,40,50})boundaryWorld.Call("object_event_add",[boundaryObject,7,sub,"hit["+sub+"]+=1;"],context);
var boundaryInstance=boundaryWorld.Create(boundaryObject,100,100);
foreach(int sub in new[]{0,1,40,50})boundaryInstance.Scope.Write("hit",0,[sub]);
boundaryWorld.Engine.Write("view_wview",200,[0]);boundaryWorld.Engine.Write("view_hview",200,[0]);
boundaryWorld.Step();
foreach(int sub in new[]{0,1,40,50})Equal(boundaryInstance.Scope.Read("hit",[sub]),0,"inside boundary "+sub);
boundaryWorld.Write(boundaryInstance,"x",640);boundaryWorld.Step();
Equal(boundaryInstance.Scope.Read("hit",[0]),0,"point on room edge is inside");
Equal(boundaryInstance.Scope.Read("hit",[40]),1,"point outside view");
Equal(boundaryInstance.Scope.Read("hit",[50]),1,"point intersects view boundary");
boundaryWorld.Write(boundaryInstance,"hspeed",0.25);boundaryWorld.Step();
Equal(boundaryInstance.Scope.Read("hit",[0]),1,"fractional point outside after motion");
Equal(boundaryInstance.Scope.Read("hit",[1]),1,"fractional point intersects room edge");
boundaryWorld.Write(boundaryInstance,"hspeed",0);boundaryWorld.Write(boundaryInstance,"sprite_index",instance.Scope.Read("sprite_index"));
boundaryWorld.Write(boundaryInstance,"x",0);boundaryWorld.Write(boundaryInstance,"y",100);
var spriteBounds=boundaryWorld.Bounds(boundaryInstance);
boundaryWorld.Write(boundaryInstance,"x",-spriteBounds.Right);
boundaryWorld.Step();
Equal(boundaryInstance.Scope.Read("hit",[0]),1,"sprite touching room edge is inside");
Equal(boundaryInstance.Scope.Read("hit",[1]),2,"sprite crossing room edge intersects");
boundaryWorld.Write(boundaryInstance,"x",boundaryInstance.Scope.Read("x").Number-1);boundaryWorld.Step();
Equal(boundaryInstance.Scope.Read("hit",[0]),2,"sprite fully outside room");
int[] randomSeeds=[1,134775814,-596792289,870078620,1172187917,-1410233534,1368768587,694906232];
var random=new GmRandom(0);
foreach(int seed in randomSeeds)
{
    Equal(random.Next(1),unchecked((uint)seed)/4294967296.0,"GameMaker floating random sequence");
    Equal(random.Seed,seed,"GameMaker random seed progression");
}
random.Seed=0;
foreach(int expected in new[]{0,0,9,2,3,7,3,1})Equal(random.NextInt(10),expected,"GameMaker integer random sequence");
random.Seed=-1;
foreach(int expected in new[]{97,20,52,13})Equal(random.NextInt(100),expected,"negative seed wraps unsigned");
random.Seed=0;Equal(random.NextInt(uint.MaxValue),0,"negative one integer bound wraps");
world.Call("random_set_seed",[0],context);
Equal(world.Call("random_range",[10,-10],context),-10+20.0/4294967296.0,"reversed floating random range");
Equal(world.Call("random_get_seed",[],context),1,"random seed API");
world.Call("random_set_seed",[0],context);
Equal(world.Call("choose",[],context),0,"empty choose result");
Equal(world.Call("random_get_seed",[],context),0,"empty choose leaves seed unchanged");
Equal(world.Call("choose",["first","second"],context),"first","choose uses integer random");
world.Call("random_set_seed",[0],context);
Equal(world.Call("action_if_dice",[0.5],context),true,"dice accepts fractional bound");
self.Scope.Write("hspeed",3);self.Scope.Write("vspeed",4);self.Scope.Write("speed",5);
context.Locals.Write("argument_relative",1);
world.Call("action_move",["000001000",2],context);
Equal(self.Scope.Read("hspeed"),5,"relative direction action adds horizontal velocity");
Equal(self.Scope.Read("vspeed"),4,"relative direction action preserves vertical velocity");
var bounceWorld=new GmWorld(Path.Combine(project,"GameData"),source);
int bounceObject=bounceWorld.Call("object_add",[],context).Int;
int bounceSprite=bounceWorld.RegisterSprite(System.Text.Json.JsonSerializer.SerializeToElement(new
{
    originX=0,originY=0,
    frames=new[]{new{width=1,height=1,path="unused"}},
    masks=new[]{new{width=1,height=1,left=0,right=0,top=0,bottom=0,data="1"}}
}));
bounceWorld.Call("object_set_sprite",[bounceObject,bounceSprite],context);
var ball=bounceWorld.Create(bounceObject,0,0);
var wall=bounceWorld.Create(bounceObject,2,0);wall.Scope.Write("solid",true);
bounceWorld.Write(ball,"hspeed",2);bounceWorld.Write(ball,"vspeed",0);
bounceWorld.Call("move_bounce_solid",[0],new(ball));
Equal(ball.Scope.Read("hspeed"),-2,"horizontal solid bounce");
Equal(ball.Scope.Read("x"),0,"bounce keeps previous position");
bounceWorld.Write(wall,"x",0);bounceWorld.Write(wall,"y",2);
bounceWorld.Write(ball,"hspeed",0);bounceWorld.Write(ball,"vspeed",2);
bounceWorld.Call("move_bounce_solid",[0],new(ball));
Equal(ball.Scope.Read("vspeed"),-2,"vertical solid bounce");
bounceWorld.Write(wall,"x",2);bounceWorld.Write(wall,"y",2);
bounceWorld.Write(ball,"hspeed",2);bounceWorld.Write(ball,"vspeed",2);
bounceWorld.Call("move_bounce_solid",[0],new(ball));
Equal(ball.Scope.Read("hspeed"),-2,"corner bounce horizontal");
Equal(ball.Scope.Read("vspeed"),-2,"corner bounce vertical");
bounceWorld.Write(wall,"solid",false);bounceWorld.Write(ball,"hspeed",2);bounceWorld.Write(ball,"vspeed",2);
bounceWorld.Call("move_bounce_solid",[0],new(ball));
Equal(ball.Scope.Read("hspeed"),2,"solid bounce ignores nonsolid");
bounceWorld.Call("move_bounce_all",[0],new(ball));
Equal(ball.Scope.Read("hspeed"),-2,"all bounce includes nonsolid");
bounceWorld.Write(ball,"x",2);bounceWorld.Write(ball,"y",2);bounceWorld.Write(ball,"hspeed",2);bounceWorld.Write(ball,"vspeed",2);
bounceWorld.Call("move_bounce_all",[0],new(ball));
Equal(ball.Scope.Read("x"),0,"overlap bounce restores xprevious");
Equal(ball.Scope.Read("y"),0,"overlap bounce restores yprevious");
bounceWorld.Write(ball,"direction",-90);bounceWorld.Write(ball,"speed",3);
Equal(ball.Scope.Read("direction"),270,"negative direction normalized");
Equal(ball.Scope.Read("hspeed"),0,"cardinal horizontal velocity snaps to zero");
Equal(ball.Scope.Read("vspeed"),3,"cardinal vertical velocity");
bounceWorld.Write(ball,"direction",720);
Equal(ball.Scope.Read("direction"),0,"multiple turns normalized");
bounceWorld.Write(ball,"direction",89.99999);bounceWorld.Write(ball,"speed",1);
Equal(ball.Scope.Read("hspeed"),0,"near integer horizontal velocity snaps");
Equal(ball.Scope.Read("vspeed"),-1,"near integer vertical velocity snaps");
bounceWorld.Write(ball,"speed",0);bounceWorld.Write(ball,"hspeed",0);
Equal(ball.Scope.Read("direction"),89.99999,"unchanged component preserves stopped direction");
bounceWorld.Write(ball,"direction",90);bounceWorld.Write(ball,"speed",1);bounceWorld.Write(ball,"vspeed",0);
Equal(ball.Scope.Read("direction"),0,"changed component to rest recomputes direction");
bounceWorld.Write(ball,"hspeed",2);bounceWorld.Write(ball,"vspeed",0.0000001);
Equal(ball.Scope.Read("direction"),360,"component direction snaps near full turn");
bounceWorld.Write(wall,"x",2048);bounceWorld.Write(wall,"y",2048);
Equal(bounceWorld.Call("collision_point",[2048,2048,bounceObject,1,1],new(ball)),wall.Id,"collision index follows moved wall");
Equal(bounceWorld.Call("collision_point",[2,2,bounceObject,1,1],new(ball)),-4,"collision index removes old cell");
wall.Scope.Write("x",-2048);wall.Scope.Write("y",-1024);
Equal(bounceWorld.Call("collision_point",[-2048,-1024,bounceObject,1,1],new(ball)),wall.Id,"collision index follows direct scope writes into negative cells");
wall.Active=false;
Equal(bounceWorld.Call("collision_point",[-2048,-1024,bounceObject,1,1],new(ball)),-4,"collision index excludes inactive instance");
wall.Active=true;wall.Scope.Write("image_xscale",40000);
Equal(bounceWorld.Call("collision_point",[12000,-1024,bounceObject,1,1],new(ball)),wall.Id,"collision index supports very large scaled collider");
wall.Scope.Write("image_xscale",2);wall.Scope.Write("image_angle",90);wall.Scope.Write("image_yscale",2);
Equal(bounceWorld.Call("collision_point",[-2047,-1025,bounceObject,1,1],new(ball)),wall.Id,"collision index follows rotation and scaling");
wall.Scope.Write("mask_index",bounceWorld.Constants["sprEmpty"]);
Equal(bounceWorld.Call("collision_point",[-2047,-1025,bounceObject,1,1],new(ball)),-4,"collision index follows empty mask change");
wall.Scope.Write("mask_index",-1);wall.Scope.Write("image_angle",0);wall.Scope.Write("image_xscale",1);wall.Scope.Write("image_yscale",1);wall.Scope.Write("x",2048);wall.Scope.Write("y",2048);
bounceWorld.Call("sprite_set_offset",[bounceSprite,-500,0],new(ball));
Equal(bounceWorld.Call("collision_point",[2548,2048,bounceObject,1,1],new(ball)),wall.Id,"collision index refreshes sprite origin changes");
bounceWorld.Call("sprite_set_offset",[bounceSprite,0,0],new(ball));
Equal(bounceWorld.Call("instance_number",[bounceObject],new(ball)),2,"cached instance count initial");
wall.Active=false;
Equal(bounceWorld.Call("instance_number",[bounceObject],new(ball)),1,"cached instance count tracks deactivation");
wall.Active=true;
Equal(bounceWorld.Call("instance_number",[bounceObject],new(ball)),2,"cached instance count tracks activation");
wall.Destroyed=true;
Equal(bounceWorld.Call("instance_number",[bounceObject],new(ball)),1,"cached instance count tracks destruction");
wall.Destroyed=false;
Equal(bounceWorld.Call("instance_number",[bounceObject],new(ball)),2,"cached instance count tracks restoration");
var drawingCalls=new List<GmValue[]>();
bounceWorld.Platform=(name,args,c)=>{if(name!="draw_sprite_ext")throw new Exception("Unexpected drawing API "+name);drawingCalls.Add(args.ToArray());return 0;};
foreach(var (mode,full,length,rotation) in new (int,int,double,double)[]{(0,0,20,0),(1,0,0,0),(1,0,10,0),(1,0,20,-30),(2,0,125,40),(3,0,25,10),(1,1,5,30),(1,0,151,30),(1,0,-2,30),(1,0,0,50)})
{
    bounceWorld.Globals.Write("config_MotionBlur",mode);bounceWorld.Globals.Write("fullQuality",full);bounceWorld.Globals.Write("BlurQual",0);ball.Scope.Write("qtimes",-1);
    GmValue[] blurArgs=[bounceSprite,0,20,40,1,-1,35,0x112233,0.8,length,123,rotation];
    bounceWorld.Evaluator.ResetBudget();bounceWorld.Evaluator.Run(bounceWorld.Scripts["draw_sprite_blur"],new(ball,args:blurArgs));
    var referenceCalls=drawingCalls.ToArray();drawingCalls.Clear();
    var referenceQuality=bounceWorld.Globals.Read("BlurQual");var referenceHalf=ball.Scope.Read("qtimes");
    bounceWorld.Globals.Write("BlurQual",0);ball.Scope.Write("qtimes",-1);
    bounceWorld.Call("draw_sprite_blur",blurArgs,new(ball));
    Equal(drawingCalls.Count,referenceCalls.Length,"native blur preserves draw count");
    for(int call=0;call<drawingCalls.Count;call++)for(int argument=0;argument<drawingCalls[call].Length;argument++)
        Equal(Math.Abs(drawingCalls[call][argument].Number-referenceCalls[call][argument].Number)<1e-10,true,"native blur preserves draw argument");
    Equal(bounceWorld.Globals.Read("BlurQual"),referenceQuality,"native blur preserves quality accumulation");
    Equal(ball.Scope.Read("qtimes"),referenceHalf,"native blur preserves instance side effect");
    drawingCalls.Clear();
}
var saveRoot=Path.Combine(Path.GetTempPath(),"slack-save-test-"+Guid.NewGuid().ToString("N"));
try
{
    var savedWorld=new GmWorld(Path.Combine(project,"GameData"),source,saveRoot);
    var saveContext=new GmContext(new GmInstance{Id=999999,Object=-1});
    string config="Cfg/MigrationRuntimeTest";
    savedWorld.External[config]=savedWorld.Compile("saved_value=1;");
    int savedHandle=savedWorld.Call("file_text_open_write",[config],saveContext).Int;
    savedWorld.Call("file_text_write_string",[savedHandle,"saved_value=42;"],saveContext);
    savedWorld.Call("file_text_close",[savedHandle],saveContext);
    savedWorld.Evaluator.ResetBudget();savedWorld.Call("execute_file",["cfg/migrationruntimetest"],saveContext);
    Equal(saveContext.Self.Scope.Read("saved_value"),42,"saved script overrides extracted original");
    Equal(savedWorld.Resolve(config),savedWorld.Resolve("cfg/MIGRATIONRUNTIMETEST"),"saved path case insensitive");
    Equal(savedWorld.Resolve("cfg/MIGRATIONRUNTIMETEST",true),savedWorld.Resolve(config),"write reuses saved path casing");
    savedHandle=savedWorld.Call("file_text_open_read",[config],saveContext).Int;
    Equal(savedWorld.Call("file_text_read_string",[savedHandle],saveContext),"saved_value=42;","text reads saved script before extracted source");
    savedWorld.Call("file_text_close",[savedHandle],saveContext);
    bool rejected=false;
    try{savedWorld.Resolve("../../outside",true);}catch(InvalidOperationException){rejected=true;}
    Equal(rejected,true,"save path traversal rejected");
}
finally{if(Directory.Exists(saveRoot))Directory.Delete(saveRoot,true);}
byte[] transparentPixels=[10,20,30,0,40,50,60,0];
Equal(string.Join(",",GmPixels.Triangles(4,7)),"0,1,2,3,4,5","triangle list omits incomplete triangle");
Equal(string.Join(",",GmPixels.Triangles(5,5)),"0,1,2,2,1,3,2,3,4","triangle strip alternates winding");
Equal(string.Join(",",GmPixels.Triangles(6,5)),"0,1,2,0,2,3,0,3,4","triangle fan shares first vertex");
Equal(GmPixels.Triangles(6,2).Length,0,"incomplete primitive draws nothing");
GmPixels.Prepare(transparentPixels,2,1,0,false);
Equal(transparentPixels[3],255,"empty captured image becomes opaque");
Equal(transparentPixels[7],255,"empty capture fills every alpha");
byte[] retainedPixels=[10,20,30,0,40,50,60,0];
GmPixels.Prepare(retainedPixels,2,1,2,false);
Equal(retainedPixels[3],0,"transparency two preserves empty alpha");
byte[] keyedPixels=[255,0,0,255,0,0,255,255,0,0,255,255,0,0,255,255];
GmPixels.Prepare(keyedPixels,2,2,1,false);
Equal(keyedPixels[3],255,"capture retains foreground alpha");
Equal(keyedPixels[7],0,"capture removes matching bottom left color");
Equal(keyedPixels[4],255,"transparent border inherits adjacent red");
Equal(keyedPixels[6],0,"transparent border removes original blue");
byte[] alphaBackground=[0,0,0,255,255,120,90,255,0,0,0,0,0,0,0,0];
GmPixels.Prepare(alphaBackground,2,2,1,false);
Equal(alphaBackground[3],255,"transparent GIF background preserves opaque black outlines");
Equal(alphaBackground[7],255,"transparent GIF background preserves colored foreground");
Equal(alphaBackground[11],0,"transparent GIF background remains transparent");
Equal(alphaBackground[15],0,"transparent GIF corner remains transparent");
var rectanglePixels=new byte[4*3*4];
GmPixels.FillRectangle(rectanglePixels,4,3,-2,1,3,8,10,20,30,255);
Equal(rectanglePixels[3],0,"surface rectangle preserves pixels above bounds");
Equal(rectanglePixels[16],10,"surface rectangle clips left edge");
Equal(rectanglePixels[21],20,"surface rectangle fills green channel");
Equal(rectanglePixels[42],30,"surface rectangle copies subsequent rows");
Equal(rectanglePixels[43],255,"surface rectangle copies alpha");
Equal(rectanglePixels[47],0,"surface rectangle excludes right boundary");
void CheckOpaqueTriangle(float ax,float ay,float bx,float by,float cx,float cy,string name)
{
    const int width=19,height=13;
    var actual=Enumerable.Repeat((byte)73,width*height*4).ToArray();
    var expected=(byte[])actual.Clone();
    GmPixels.FillOpaqueTriangle(actual,width,height,ax,ay,bx,by,cx,cy,10,20,30);
    float Edge(float px,float py,float qx,float qy,float x,float y)=>(qx-px)*(y-py)-(qy-py)*(x-px);
    float area=Edge(ax,ay,bx,by,cx,cy);
    if(area<0){(bx,cx)=(cx,bx);(by,cy)=(cy,by);area=-area;}
    bool TopLeft(float px,float py,float qx,float qy)=>py>qy||py==qy&&px<qx;
    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
    {
        float wa=Edge(bx,by,cx,cy,x+0.5f,y+0.5f),wb=Edge(cx,cy,ax,ay,x+0.5f,y+0.5f),wc=Edge(ax,ay,bx,by,x+0.5f,y+0.5f);
        if(area==0||wa<0||wa==0&&!TopLeft(bx,by,cx,cy)||wb<0||wb==0&&!TopLeft(cx,cy,ax,ay)||wc<0||wc==0&&!TopLeft(ax,ay,bx,by))continue;
        int offset=(y*width+x)*4;expected[offset]=10;expected[offset+1]=20;expected[offset+2]=30;expected[offset+3]=255;
    }
    Equal(actual.SequenceEqual(expected),true,"opaque triangle matches reference coverage: "+name);
}
foreach(var triangle in new (float Ax,float Ay,float Bx,float By,float Cx,float Cy)[]{
    (1,1,17,1,1,11),(1,1,1,11,17,1),(1,1,17,11,1,11),
    (-20,-10,40,3,3,30),(30,30,50,30,30,50),(1,1,1,1,1,1),
    (0,0,4,4,8,8),(0.5f,0.5f,17.5f,0.5f,0.5f,11.5f),
    (8.25f,-4000,8.75f,5000,9.25f,12.75f),(-1300,-4000,40,1000,73,1033)})
{
    CheckOpaqueTriangle(triangle.Ax,triangle.Ay,triangle.Bx,triangle.By,triangle.Cx,triangle.Cy,"fixture");
    CheckOpaqueTriangle(triangle.Bx,triangle.By,triangle.Cx,triangle.Cy,triangle.Ax,triangle.Ay,"cyclic vertices");
}
var triangleRandom=new Random(7341);
var triangleCache=new GmTriangleCache();
foreach(var dimensions in new[]{(Width:19,Height:13),(Width:23,Height:17)})
foreach(float offset in new[]{0f,0.25f,20f})
{
    int width=dimensions.Width,height=dimensions.Height;
    var expected=Enumerable.Repeat((byte)73,width*height*4).ToArray();
    GmPixels.FillOpaqueTriangle(expected,width,height,1+offset,-20,4+offset,50,15+offset,8,0,0,0);
    for(int repeat=0;repeat<2;repeat++)
    {
        var actual=Enumerable.Repeat((byte)73,width*height*4).ToArray();
        triangleCache.Fill(actual,width,height,1+offset,-20,4+offset,50,15+offset,8);
        Equal(actual.SequenceEqual(expected),true,"cached triangle preserves coverage after geometry or size change");
    }
}
Equal(triangleCache.Hits,6,"triangle cache reuses exact geometry");
Equal(triangleCache.Misses,6,"triangle cache distinguishes dimensions and fractional positions");
triangleCache.Clear();Equal(triangleCache.Count,0,"triangle cache releases coverage on reset");
for(int index=0;index<8300;index++)triangleCache.Fill(new byte[16],2,2,index,0,index+1,0,index,1);
Equal(triangleCache.Count,8192,"triangle cache bounds entries during camera movement");
triangleCache.Clear();
var rainCoverage=new byte[32*480*4];
for(int pass=0;pass<2;pass++)for(int index=0;index<574;index++)
    triangleCache.Fill(rainCoverage,32,480,index*0.001f,-1,32,481,0,481);
Equal(triangleCache.Misses,574,"rain working set fits coverage storage budget");
Equal(triangleCache.Hits,574,"full rain working set survives across frames");
triangleCache.Clear();
for(int pass=0;pass<2;pass++)for(int index=0;index<2400;index++)
    triangleCache.Fill(rainCoverage,32,480,index*0.001f,-1,32,481,0,481);
Equal(triangleCache.Bytes>8*1024*1024,true,"expanded triangle cache retains more than eight MiB");
Equal(triangleCache.Bytes<=32*1024*1024,true,"triangle coverage stays within thirty-two MiB");
Equal(triangleCache.Hits,2400,"expanded working set survives across frames");
triangleCache.Clear();Equal(triangleCache.Bytes,0,"clearing triangle cache resets byte accounting");
for(int sample=0;sample<40;sample++)
{
    const int width=17,height=11,sourceWidth=9,sourceHeight=7;
    var texels=new byte[sourceWidth*sourceHeight*4];triangleRandom.NextBytes(texels);
    for(int i=3;i<texels.Length;i+=4)texels[i]=sample%3==0?(byte)0:sample%3==1?(byte)255:texels[i];
    var actual=new byte[width*height*4];triangleRandom.NextBytes(actual);var expected=(byte[])actual.Clone();
    float xx=sample%2==0?1/0.75f:0.5f,yy=xx,tx=sample*0.37f-10,ty=sample*0.19f-6;
    GmPixels.CompositeNormal(actual,width,height,texels,sourceWidth,sourceHeight,-1,-3,width+2,height+1,xx,yy,tx,ty);
    for(int y=0;y<height;y++)for(int x=0;x<width;x++)
    {
        float localX=xx*(x+0.5f)+tx,localY=yy*(y+0.5f)+ty;
        if(localX<0||localY<0||localX>=sourceWidth||localY>=sourceHeight)continue;
        int si=((int)MathF.Floor(localY)*sourceWidth+(int)MathF.Floor(localX))*4,di=(y*width+x)*4;
        float alpha=texels[si+3]/255f;
        for(int channel=0;channel<4;channel++)expected[di+channel]=(byte)(Math.Clamp(texels[si+channel]/255f*alpha+expected[di+channel]/255f*(1-alpha),0,1)*255);
    }
    Equal(actual.SequenceEqual(expected),true,"normal composite matches reference RGBA: "+sample);
}
for(int sample=0;sample<500;sample++)
{
    float Coordinate()=>(float)(triangleRandom.NextDouble()*100-40);
    CheckOpaqueTriangle(Coordinate(),Coordinate(),Coordinate(),Coordinate(),Coordinate(),Coordinate(),"fractional "+sample);
}
byte[] smoothPixels=[0,0,0,0,255,255,255,255,0,0,0,0];
GmPixels.Prepare(smoothPixels,3,1,2,true);
Equal(smoothPixels[7],191,"two transparent neighbors each reduce alpha");
GmPixels.Prepare(smoothPixels,3,1,-1,false);
Equal(smoothPixels[3],255,"negative transparency forces every pixel opaque");
var captureMask=GmPixels.Mask(keyedPixels,2,2);
Equal(captureMask.GetProperty("data").GetString()!,"1000","capture mask matches prepared alpha");
Equal(captureMask.GetProperty("left").GetInt32(),0,"capture mask left edge");
Equal(captureMask.GetProperty("right").GetInt32(),0,"capture mask right edge");
var emptyMask=GmPixels.Mask(retainedPixels,2,1);
Equal(emptyMask.GetProperty("left").GetInt32(),1,"empty mask original left default");
Equal(emptyMask.GetProperty("right").GetInt32(),0,"empty mask original right default");
foreach(bool compiledExecution in new[]{false,true})
{
    world.Evaluator.CompiledExecution=compiledExecution;
    world.Evaluator.ResetBudget();
    Run("result=0; repeat(8){result+=1;if(result==2)continue;if(result==5)break;} return result; result=99;");
    Equal(self.Scope.Read("result"),5,"loop flow "+compiledExecution);
    Run("result=0; repeat(4){switch(result){case 0:result=1;continue;case 1:result=2;break;default:return 23;} result+=1;} result=99;");
    Equal(self.Scope.Read("result"),3,"switch return flow "+compiledExecution);
    var result=world.Evaluator.Run(world.Compile("repeat(4){with(self){return 37;}} return 99;"),context);
    Equal(result,37,"with return propagates "+compiledExecution);
    Run("result=0; with(self){repeat(4){result+=1;break;} exit;} result=99;");
    Equal(self.Scope.Read("result"),1,"with exit propagates "+compiledExecution);
    Run("result=11;");
    Equal(self.Scope.Read("result"),11,"flow reset between scripts "+compiledExecution);
    var parameterContext=new GmContext(self,args:[3,7]);
    world.Evaluator.Run(world.Compile("argument0=9; argument1+=2; named=argument0; indexed=argument[0]; argument[1]+=4; aliased=argument1;"),parameterContext);
    Equal(self.Scope.Read("named"),9,"numbered argument write "+compiledExecution);
    Equal(self.Scope.Read("indexed"),9,"numbered argument aliases indexed argument "+compiledExecution);
    Equal(self.Scope.Read("aliased"),13,"compound arguments share storage "+compiledExecution);
}
world.Evaluator.CompiledExecution=true;
var selected=world.Create(caution,600,600,run:false);
Equal(world.Select(selected.Id,context).Single()==selected,true,"indexed instance lookup");
Equal(world.Select(caution,context).Contains(selected),true,"indexed object lookup");
var selectedLater=world.Create(caution,610,610,run:false);
Equal(world.Select(caution,context).Contains(selectedLater),true,"cached selection includes creation");
selectedLater.Active=false;
Equal(world.Select(caution,context).Contains(selectedLater),false,"cached selection respects deactivation");
selectedLater.Active=true;selectedLater.Destroyed=true;
Equal(world.Select(selectedLater.Id,context).Any(),false,"indexed lookup respects destruction");
Equal(world.Select(caution,context).Contains(selectedLater),false,"cached selection respects destruction");
world.Call("instance_change",[dynamicObject,0],new(selected));
Equal(world.Select(caution,context).Contains(selected),true,"changed object remains in inherited selection");
world.Call("object_set_parent",[dynamicObject,-1],context);
Equal(world.Select(caution,context).Contains(selected),false,"parent change invalidates selection");
Equal(world.HasEvent(dynamicObject,7,63),false,"missing event cached");
world.Call("object_event_add",[dynamicObject,7,63,"cache_event=19;"],context);
Equal(world.HasEvent(dynamicObject,7,63),true,"event addition invalidates missing lookup");
world.Evaluator.ResetBudget();world.Event(selected,7,63);
Equal(selected.Scope.Read("cache_event"),19,"cached dynamic event executes");
var particleWorld=new GmWorld(Path.Combine(project,"GameData"),source);
var particleContext=new GmContext(new GmInstance());
Equal(particleWorld.Constants["pt_shape_snow"],13,"snow atlas index");
Equal(particleWorld.Constants["ef_smokeup"],5,"upward smoke effect index");
particleContext.Self.Scope.Write("x",100);particleContext.Self.Scope.Write("y",200);particleContext.Locals.Write("argument_relative",true);
particleWorld.Random.Seed=0;
particleWorld.Call("action_effect",[5,0,0,0,0xffffff,0],particleContext);
var smokeSystem=particleWorld.Particles.Systems.Values.Single(s=>s.Depth==100000);
Equal(smokeSystem.Particles.Count,6,"relative shooting effect creates six smoke particles");
Equal(smokeSystem.Particles[0].X,95,"relative smoke initial x");
Equal(smokeSystem.Particles[0].Y,195,"relative smoke initial y");
var expectedRandom=new GmRandom(0);
for(int i=0;i<6;i++){expectedRandom.NextInt(9);expectedRandom.NextInt(9);expectedRandom.Next(1);expectedRandom.Next(0.2);expectedRandom.NextInt(100000);}
Equal(particleWorld.Random.Seed,expectedRandom.Seed,"upward smoke preserves original RNG order");
var smokeParticle=smokeSystem.Particles[0];double smokeY=smokeParticle.Y;
particleWorld.Particles.Update(particleWorld.Random);
Equal(smokeParticle.Y,smokeY-smokeParticle.Speed,"smoke moves upwards");
Equal(Math.Abs(smokeParticle.Alpha-0.384)<1e-12,true,"smoke alpha interpolates by lifetime");
for(int frame=1;frame<25;frame++)particleWorld.Particles.Update(particleWorld.Random);
Equal(smokeSystem.Particles.Count,0,"expired smoke is removed");
particleWorld.Call("effect_create_above",[1,10,20,0,255],particleContext);
var ringSystem=particleWorld.Particles.Systems.Values.Single(s=>s.Depth==-100000);
Equal(ringSystem.Particles.Count,1,"enemy hit creates ring above objects");
particleWorld.Particles.Update(particleWorld.Random);
Equal(ringSystem.Particles[0].Size,0.15,"hit ring expands");
particleWorld.Call("effect_clear",[],particleContext);
Equal(ringSystem.Particles.Count,0,"clear effects removes particles");
int ps=particleWorld.Call("part_system_create",[],particleContext).Int;
int pt=particleWorld.Call("part_type_create",[],particleContext).Int;
int em=particleWorld.Call("part_emitter_create",[ps],particleContext).Int;
particleWorld.Call("part_emitter_region",[ps,em,10,10,20,20,3,0],particleContext);
particleWorld.Call("part_type_speed",[pt,2,2,0,0],particleContext);
particleWorld.Call("part_type_direction",[pt,90,90,0,0],particleContext);
particleWorld.Call("part_type_life",[pt,2,2],particleContext);
particleWorld.Call("part_type_alpha2",[pt,1,0],particleContext);
particleWorld.Call("part_emitter_burst",[ps,em,pt,3],particleContext);
Equal(particleWorld.Call("part_particles_count",[ps],particleContext),3,"emitter bursts requested count");
particleWorld.Call("part_system_update",[ps],particleContext);
Equal(particleWorld.Particles.Systems[ps].Particles[0].Y,18,"emitter particles advance with system");
Equal(particleWorld.Particles.Systems[ps].Particles[0].Alpha,0.5,"two-alpha particle interpolation");
particleWorld.Call("part_system_update",[ps],particleContext);
Equal(particleWorld.Call("part_particles_count",[ps],particleContext),0,"emitter particle lifetime");
particleWorld.Call("part_system_destroy",[ps],particleContext);
particleWorld.Call("part_emitter_destroy",[ps,em],particleContext);
Equal(particleWorld.Call("part_system_exists",[ps],particleContext),false,"destroy system before emitter is valid");
Equal(particleWorld.Call("part_system_create",[],particleContext),ps,"particle system ids reuse first free slot");
var mappedView=new GmView(1200,300,800,450,20,30,1600,900,0);
var mapped=mappedView.ToRoom(820,480);
Equal(mapped.X,1600,"mouse includes camera and port scale x");
Equal(mapped.Y,525,"mouse includes camera and port scale y");
var rotatedView=mappedView with{Angle=90};mapped=rotatedView.ToRoom(1220,480);
Equal(mapped.X,1600,"rotated mouse x");
Equal(mapped.Y,725,"rotated mouse y");
var onScreen=rotatedView.ToScreen(mapped.X,mapped.Y);
Equal(Math.Abs(onScreen.X-1220)<1e-9,true,"rotated rendering matches input x");
Equal(Math.Abs(onScreen.Y-480)<1e-9,true,"rotated rendering matches input y");
particleWorld.Engine.Write("view_enabled",1);particleWorld.Engine.Write("view_visible",1,[0]);
particleWorld.Engine.Write("view_xview",1200,[0]);particleWorld.Engine.Write("view_yview",300,[0]);
particleWorld.Engine.Write("view_wview",848,[0]);particleWorld.Engine.Write("view_hview",477,[0]);
particleWorld.Engine.Write("view_wport",848,[0]);particleWorld.Engine.Write("view_hport",477,[0]);
Equal(particleWorld.OutputSize().Width,848,"wide viewport output width");
Equal(particleWorld.OutputSize().Height,477,"wide viewport output height");
mapped=particleWorld.MouseToRoom(424,238.5);
Equal(mapped.X,1624,"wide mouse world x");
Equal(mapped.Y,538,"wide mouse world y uses original rounding");
int activationParent=particleWorld.Call("object_add",[],particleContext).Int;
int activationChild=particleWorld.Call("object_add",[],particleContext).Int;
particleWorld.Call("object_set_parent",[activationChild,activationParent],particleContext);
particleWorld.Call("object_set_sprite",[activationParent,particleWorld.Constants["sprMaskDot"]],particleContext);
particleWorld.Call("object_set_sprite",[activationChild,particleWorld.Constants["sprMaskDot"]],particleContext);
var nearActivation=particleWorld.Create(activationChild,100,100);
var farActivation=particleWorld.Create(activationParent,1000,1000);
particleWorld.Call("instance_deactivate_object",[activationParent],particleContext);
Equal(nearActivation.Active||farActivation.Active,false,"parent deactivation includes children");
particleWorld.Call("instance_activate_region",[90,90,20,20,1],particleContext);
Equal(nearActivation.Active,true,"inside activation finds inactive colliders");
Equal(farActivation.Active,false,"inside activation keeps distant objects inactive");
particleWorld.Write(farActivation,"x",105);particleWorld.Write(farActivation,"y",105);
particleWorld.Call("instance_activate_region",[90,90,20,20,1],particleContext);
Equal(farActivation.Active,true,"region activation updates moved inactive colliders");
particleWorld.Call("instance_deactivate_region",[90,90,20,20,1,1],new(nearActivation));
Equal(nearActivation.Active,true,"region deactivation respects notme");
Equal(farActivation.Active,false,"region deactivation affects other colliders");
particleWorld.Call("instance_activate_object",[activationParent],particleContext);
Equal(farActivation.Active,true,"cached parent activation includes inactive objects");
int spriteLessObject=particleWorld.Call("object_add",[],particleContext).Int;
var nearSpriteLess=particleWorld.Create(spriteLessObject,100,100);
var farSpriteLess=particleWorld.Create(spriteLessObject,1000,1000);
particleWorld.Call("instance_deactivate_object",[spriteLessObject],particleContext);
particleWorld.Call("instance_activate_region",[90,90,20,20,1],particleContext);
Equal(nearSpriteLess.Active,true,"region activation restores nearby instances without sprites");
Equal(farSpriteLess.Active,false,"region activation keeps distant sprite-less instances inactive");
Equal(particleWorld.Bounds(nearSpriteLess).Empty,true,"sprite-less activation preserves empty collision bounds");
particleWorld.Call("instance_activate_region",[90,90,20,20,0],particleContext);
Equal(farSpriteLess.Active,true,"outside activation restores distant sprite-less instances");
particleWorld.Call("instance_deactivate_region",[90,90,20,20,1,1],new(nearSpriteLess));
Equal(nearSpriteLess.Active,true,"sprite-less region deactivation respects notme");
particleWorld.Call("instance_deactivate_region",[90,90,20,20,0,0],particleContext);
Equal(nearSpriteLess.Active,true,"outside deactivation preserves nearby sprite-less instances");
Equal(farSpriteLess.Active,false,"outside deactivation stops distant sprite-less instances");
var inactiveEffects=new List<GmInstance>();
foreach(string kind in new[]{"objFluid","objFluidInWater"})
{
    var effect=particleWorld.Create(particleWorld.Constants[kind].Int,0,0,run:false);
    effect.Scope.Write("alarm",3,[0]);effect.Active=false;inactiveEffects.Add(effect);
}
farActivation.Active=false;
particleWorld.Step();particleWorld.Step();
Equal(inactiveEffects.All(particleWorld.Instances.Contains),true,"inactive effects keep their remaining lifetime");
particleWorld.Step();
Equal(inactiveEffects.Any(particleWorld.Instances.Contains),false,"inactive fluids expire offscreen");
Equal(particleWorld.Instances.Contains(farActivation),true,"inactive gameplay objects do not expire");
foreach(bool compiledReference in new[]{false,true})
{
    particleWorld.Evaluator.CompiledExecution=compiledReference;
    var dying=particleWorld.Create(activationParent,0,0);
    var surviving=particleWorld.Create(activationChild,0,0);surviving.Scope.Write("owner",dying.Id);
    particleWorld.Globals.Write("survivor",surviving.Id);
    particleWorld.Evaluator.ResetBudget();
    particleWorld.Evaluator.Run(particleWorld.Compile("instance_destroy();self.last_value=7;with(global.survivor){owner_value=other.last_value;other.last_value=8;}"),new(dying));
    Equal(surviving.Scope.Read("owner_value"),7,"other can read destroyed caller within event "+compiledReference);
    Equal(dying.Scope.Read("last_value"),8,"other can write destroyed caller within event "+compiledReference);
    Equal(particleWorld.Call("instance_exists",[dying.Id],particleContext),false,"destroyed caller is absent from instance queries");
}
particleWorld.Evaluator.CompiledExecution=true;
var loadingOwner=particleWorld.Create(activationParent,0,0);
particleWorld.Globals.Write("PartCreator",loadingOwner.Id);particleWorld.Globals.Write("ForceLoadOnDoll",-1);
loadingOwner.Scope.Write("chara","Character-Nana");loadingOwner.Scope.Write("category","Body");loadingOwner.Scope.Write("loadPlayer",1);
particleWorld.Call("LoadContext",[],new(loadingOwner));
Equal(loadingOwner.Scope.Read("ctrlTarget"),loadingOwner.Id,"clothing loading selects exact creating player");
loadingOwner.Scope.Write("chara","Empty");loadingOwner.Scope.Write("loadPlayer",0);
particleWorld.Call("LoadContext",[],new(loadingOwner));
Equal(loadingOwner.Scope.Read("ctrlTarget"),loadingOwner.Id,"empty clothing initializes its owning doll");
var loggingRoot=Path.Combine(Path.GetTempPath(),"slack-performance-test-"+Guid.NewGuid().ToString("N"));
string? previousPerf=Environment.GetEnvironmentVariable("SLACK_PERF");
try
{
    Environment.SetEnvironmentVariable("SLACK_PERF",null);
    var perf=new GmPerformance();perf.Configure(loggingRoot,false);
    Equal(perf.LogPath is null,true,"release does not create debug performance log");
    perf.Configure(loggingRoot,true);
    particleWorld.CurrentRoom=particleWorld.Rooms.Keys.First();
    var noDrop=new GmFrameSample(1,30,null,30,null,false);
    perf.Record(noDrop,particleWorld,()=>new{surfaces=0});
    Equal(File.ReadAllLines(perf.LogPath!).Length,1,"stable FPS only writes session metadata");
    perf.Record(noDrop with{LogicFps=25,Dropped=true},particleWorld,()=>new{surfaces=0});
    using var record=System.Text.Json.JsonDocument.Parse(File.ReadAllLines(perf.LogPath!)[1]);
    Equal(record.RootElement.GetProperty("type").GetString()!,"drop","drop creates independent JSON record");
    foreach(string field in new[]{"character","players","npcs","room","particles","graphics","phases"})
        Equal(record.RootElement.TryGetProperty(field,out _),true,"drop captures "+field);
    perf.Process(perf.Start(),2);perf.Cadence(noDrop,particleWorld,()=>new{surfaces=0});
    using var cadence=System.Text.Json.JsonDocument.Parse(File.ReadAllLines(perf.LogPath!)[2]);
    Equal(cadence.RootElement.GetProperty("type").GetString()!,"cadence","stable gameplay records cadence without a baseline drop");
    Equal(cadence.RootElement.GetProperty("catchUpProcesses").GetInt32(),1,"cadence counts processes executing multiple logic steps");
    Equal(cadence.RootElement.GetProperty("maxStepsPerProcess").GetInt32(),2,"cadence records maximum logic steps per process");
    perf.ResetCadence();perf.Cadence(noDrop,particleWorld,()=>new{surfaces=0});
    using var resetCadence=System.Text.Json.JsonDocument.Parse(File.ReadAllLines(perf.LogPath!)[3]);
    Equal(resetCadence.RootElement.GetProperty("logicSteps").GetInt32(),0,"cadence reset discards previous scene work");
    Environment.SetEnvironmentVariable("SLACK_PERF","0");perf.Configure(loggingRoot,true);
    Equal(perf.Enabled,false,"explicit disable suppresses debug performance log");
}
finally{Environment.SetEnvironmentVariable("SLACK_PERF",previousPerf);if(Directory.Exists(loggingRoot))Directory.Delete(loggingRoot,true);}
Console.WriteLine($"PASS {assertions} assertions");

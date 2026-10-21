using System.Text.Json;

namespace SlackOfDescent;

public sealed class GmInstance
{
    public int Id;
    public int Object;
    public bool Destroyed;
    public bool Active = true;
    public GmScope Scope = new();
}

public sealed partial class GmWorld
{
    public readonly Dictionary<string, JsonElement> Code;
    public readonly JsonElement Manifest;
    public readonly Dictionary<int, JsonElement> Objects = new();
    private readonly Dictionary<int,int> parents=new();
    private readonly Dictionary<(int Object,int Kind,int Sub),JsonElement> staticEvents=new();
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
    public readonly GmRandom Random = new();
    public int CurrentRoom = -1;
    public int? PendingRoom;
    public int NextId;
    public long Frame;
    public bool Ended;
    public Action? Redraw;
    public Action? InputEvents;
    public Func<string, GmValue[], GmContext, GmValue?>? Platform;

    public GmWorld(string root, string sourceRoot,string? userRoot=null)
    {
        Root = root;
        SourceRoot = sourceRoot;
        UserRoot=Path.GetFullPath(userRoot??Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),"SlackOfDescentGodot"));
        Manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json"))).RootElement;
        Code = JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(root,"code.json")),new JsonSerializerOptions{MaxDepth=1024})!;
        Evaluator = new(this);
        NextId = Manifest.GetProperty("lastInstanceId").GetInt32() + 1;
        foreach (var category in new[] {"scripts","objects","rooms","sprites","sounds","backgrounds","fonts"})
        foreach (var asset in Manifest.GetProperty(category).EnumerateArray())
        {
            int id = asset.GetProperty("id").GetInt32();
            string name = asset.GetProperty("name").GetString()!;
            Constants[name] = id;
            switch (category)
            {
                case "scripts": Scripts[name] = asset.GetProperty("code").GetString()!; break;
                case "objects":
                    Objects[id] = asset;parents[id]=asset.GetProperty("parent").GetInt32();
                    foreach(var ev in asset.GetProperty("events").EnumerateArray())staticEvents[(id,ev.GetProperty("kind").GetInt32(),ev.GetProperty("sub").GetInt32())]=ev.GetProperty("actions");
                    break;
                case "rooms": Rooms[id] = asset; break;
                case "sprites": Sprites[id] = asset; break;
            }
        }
        foreach (var asset in Manifest.GetProperty("external").EnumerateArray()) External[asset.GetProperty("path").GetString()!] = asset.GetProperty("code").GetString()!;
        Constants["room_first"]=Manifest.GetProperty("roomOrder")[0].GetInt32();
        Constants["room_last"]=Manifest.GetProperty("roomOrder")[Manifest.GetProperty("roomOrder").GetArrayLength()-1].GetInt32();
        foreach (var pair in new Dictionary<string,double> {
            ["true"]=1,["false"]=0,["pi"]=Math.PI,["noone"]=-4,["all"]=-3,["self"]=-1,["other"]=-2,["global"]=-5,
            ["c_white"]=16777215,["c_black"]=0,["c_red"]=255,["c_lime"]=65280,["c_blue"]=16711680,["c_yellow"]=65535,["c_gray"]=8421504,["c_silver"]=12632256,["c_aqua"]=16776960,["c_fuchsia"]=16711935,["c_maroon"]=128,["c_green"]=32768,["c_navy"]=8388608,["c_olive"]=32896,["c_purple"]=8388736,["c_teal"]=8421376,["c_orange"]=4235519,
            ["vk_nokey"]=0,["vk_anykey"]=1,["vk_backspace"]=8,["vk_tab"]=9,["vk_enter"]=13,["vk_shift"]=16,["vk_control"]=17,["vk_alt"]=18,["vk_escape"]=27,["vk_space"]=32,["vk_left"]=37,["vk_up"]=38,["vk_right"]=39,["vk_down"]=40,["vk_home"]=36,["vk_end"]=35,["vk_pageup"]=33,["vk_pagedown"]=34,["vk_insert"]=45,["vk_delete"]=46,
            ["mb_left"]=1,["mb_right"]=2,["mb_middle"]=3,["mb_none"]=0,["mb_any"]=-1,
            ["fa_directory"]=16,["fa_archive"]=32,["dll_cdecl"]=0,["dll_stdcall"]=1,["ty_real"]=0,["ty_string"]=1,
            ["bm_normal"]=0,["bm_add"]=1,["bm_max"]=2,["bm_subtract"]=3,["bm_zero"]=1,["bm_one"]=2,["bm_src_alpha"]=5,["bm_inv_src_alpha"]=6,
            ["fa_left"]=0,["fa_center"]=1,["fa_right"]=2,["pr_pointlist"]=1,["pr_linelist"]=2,["pr_linestrip"]=3,["pr_trianglelist"]=4,["pr_trianglestrip"]=5,["pr_trianglefan"]=6,
            ["ev_create"]=0,["ev_destroy"]=1,["ev_alarm"]=2,["ev_step"]=3,["ev_collision"]=4,["ev_keyboard"]=5,["ev_other"]=7,["ev_draw"]=8,["ev_keypress"]=9,["ev_keyrelease"]=10,
            ["ev_step_normal"]=0,["ev_step_begin"]=1,["ev_step_end"]=2,["ev_room_start"]=4,["ev_room_end"]=5,["cr_default"]=-1,["cr_none"]=-1
        }) Constants[pair.Key] = pair.Value;
        for (int i=1;i<=12;i++) Constants["vk_f"+i] = 111+i;
        for(int i=0;i<16;i++)Constants["ev_user"+i]=10+i;
        foreach(var pair in new Dictionary<string,int>{["bm_src_color"]=3,["bm_inv_src_color"]=4,["bm_dest_alpha"]=7,["bm_inv_dest_alpha"]=8,["bm_dest_color"]=9,["bm_inv_dest_color"]=10,["bm_src_alpha_sat"]=11})Constants[pair.Key]=pair.Value;
        foreach(var pair in new Dictionary<string,int>{["vk_return"]=13,["vk_pause"]=19,["vk_printscreen"]=44,["vk_numpad0"]=96,["vk_numpad1"]=97,["vk_numpad2"]=98,["vk_numpad3"]=99,["vk_numpad4"]=100,["vk_numpad5"]=101,["vk_numpad6"]=102,["vk_numpad7"]=103,["vk_numpad8"]=104,["vk_numpad9"]=105,["vk_multiply"]=106,["vk_add"]=107,["vk_subtract"]=109,["vk_decimal"]=110,["vk_divide"]=111,["vk_lshift"]=160,["vk_rshift"]=161,["vk_lcontrol"]=162,["vk_rcontrol"]=163,["vk_lalt"]=164,["vk_ralt"]=165})Constants[pair.Key]=pair.Value;
        for (int i=0;i<=14;i++) Constants["pt_shape_" + new[]{"pixel","disk","square","line","star","circle","ring","sphere","flare","spark","explosion","cloud","smoke","snow","snow"}[i]] = i;
        foreach (var constant in Manifest.GetProperty("constants").EnumerateArray())
        {
            var text=constant.GetProperty("value").GetString()!;
            var constantName=constant.GetProperty("name").GetString()!;
            if (double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n))Constants[constantName]=n;
            else if(Constants.TryGetValue(text,out var referenced))Constants[constantName]=referenced;
            else if(text.Length>=2&&(text[0]=='"'||text[0]=='\'')&&text[^1]==text[0])Constants[constantName]=text[1..^1];
            else throw new NotSupportedException("Constant expression "+text);
        }
        foreach(var pair in new Dictionary<string,GmValue> { ["room_speed"]=30,["room_width"]=640,["room_height"]=480,["mouse_x"]=0,["mouse_y"]=0,["current_time"]=0,["fps"]=30,["game_id"]=0,["working_directory"]=sourceRoot+Path.DirectorySeparatorChar,["program_directory"]=sourceRoot+Path.DirectorySeparatorChar,["temp_directory"]=Path.GetTempPath(),["keyboard_key"]=0,["keyboard_lastkey"]=0,["keyboard_string"]="",["mouse_button"]=0,["mouse_lastbutton"]=0,["cursor_sprite"]=-1,["view_enabled"]=0,["view_current"]=0,["background_color"]=0,["background_showcolor"]=1,["show_score"]=0,["show_lives"]=0,["show_health"]=0,["score"]=0,["lives"]=0,["health"]=100 }) Engine.Write(pair.Key,pair.Value);
        foreach (string name in new[] {"view_xview","view_yview","view_wview","view_hview","view_xport","view_yport","view_wport","view_hport","view_hborder","view_vborder","view_hspeed","view_vspeed","view_object","view_angle","view_visible","background_visible","background_foreground","background_index","background_x","background_y","background_width","background_height","background_htiled","background_vtiled","background_xscale","background_yscale","background_hspeed","background_vspeed","background_blend","background_alpha"})
            for(int i=0;i<8;i++) Engine.Write(name,name.Contains("scale")||name.Contains("alpha")?1:0,[i]);
    }

    public GmValue Read(GmInstance instance,string name,IReadOnlyList<int>? indices=null)
    {
        if(name=="id") return instance.Id;
        if(name=="object_index") return instance.Object;
        if(name is "bbox_left" or "bbox_top" or "bbox_right" or "bbox_bottom")
        {var b=Bounds(instance);return name switch{"bbox_left"=>b.Left,"bbox_top"=>b.Top,"bbox_right"=>b.Right,_=>b.Bottom};}
        if(name=="room") return CurrentRoom;
        if(name=="instance_count") return Instances.Count(i=>!i.Destroyed);
        if(name=="instance_id") return Instances.Where(i=>!i.Destroyed).ElementAt(indices?[0]??0).Id;
        if(name=="event_type"||name=="event_number") return 0;
        if(name.StartsWith("sprite_",StringComparison.Ordinal)&&name is "sprite_width" or "sprite_height" or "sprite_xoffset" or "sprite_yoffset")
        {
            var sprite=instance.Scope.ReadOr("sprite_index",-1).Int;
            if(!Sprites.TryGetValue(sprite,out var s))return 0;
            return name switch {"sprite_width"=>s.GetProperty("frames")[0].GetProperty("width").GetInt32(),"sprite_height"=>s.GetProperty("frames")[0].GetProperty("height").GetInt32(),"sprite_xoffset"=>s.GetProperty("originX").GetInt32(),_=>s.GetProperty("originY").GetInt32()};
        }
        if(name=="image_number")return Sprites.TryGetValue(instance.Scope.ReadOr("sprite_index",-1).Int,out var sp)?sp.GetProperty("frames").GetArrayLength():0;
        if(Engine.Values.ContainsKey(GmScope.Key(name,indices)))return Engine.Read(name,indices);
        return instance.Scope.Read(name,indices);
    }

    public void Write(GmInstance instance,string name,GmValue value,IReadOnlyList<int>? indices=null)
    {
        if(name=="room"){PendingRoom=value.Int;return;}
        if(Engine.Values.ContainsKey(GmScope.Key(name,indices))){Engine.Write(name,value,indices);return;}
        if(name is "hspeed" or "vspeed"&&instance.Scope.ReadOr(name).Equals(value))return;
        if(name=="direction"){double direction=value.Number%360;value=direction<0?direction+360:direction;}
        instance.Scope.Write(name,value,indices);
        if(name is "hspeed" or "vspeed")
        {
            var h=instance.Scope.ReadOr("hspeed").Number;var v=instance.Scope.ReadOr("vspeed").Number;
            instance.Scope.Write("speed",Math.Sqrt(h*h+v*v));
            double direction=Direction(0,0,h,v),rounded=Math.Round(direction,MidpointRounding.ToEven);
            instance.Scope.Write("direction",Math.Abs(direction-rounded)<0.0001?rounded:direction);
        }
        if(name is "speed" or "direction")
        {
            var speed=instance.Scope.ReadOr("speed").Number;var dir=instance.Scope.ReadOr("direction").Number*Math.PI/180;
            double h=Math.Cos(dir)*speed,v=-Math.Sin(dir)*speed;
            double roundedH=Math.Round(h,MidpointRounding.ToEven),roundedV=Math.Round(v,MidpointRounding.ToEven);
            instance.Scope.Write("hspeed",Math.Abs(h-roundedH)<0.0001?roundedH:h);instance.Scope.Write("vspeed",Math.Abs(v-roundedV)<0.0001?roundedV:v);
        }
    }

    public bool IsObject(int actual,int target)
    {
        for(int depth=0;depth<128&&Objects.ContainsKey(actual);depth++)
        {if(actual==target)return true;actual=parents.GetValueOrDefault(actual,-1);}
        return false;
    }
    public IEnumerable<GmInstance> Select(int target,GmContext context)
    {
        if(target==-1)return context.Self.Destroyed?[]:[context.Self];
        if(target==-2)return context.Other.Destroyed?[]:[context.Other];
        if(target==-4)return [];
        if(target>=100000)return Instances.Where(i=>i.Id==target&&!i.Destroyed&&i.Active);
        return Instances.Where(i=>!i.Destroyed&&i.Active&&(target==-3||i.Id==target||IsObject(i.Object,target)));
    }
    public GmInstance Create(int obj,double x,double y,int? id=null,bool run=true)
    {
        if(!Objects.TryGetValue(obj,out var o))throw new InvalidOperationException("Missing object "+obj);
        var instance=new GmInstance{Id=id??NextId++,Object=obj};
        foreach(var pair in new Dictionary<string,GmValue>{["x"]=x,["y"]=y,["xprevious"]=x,["yprevious"]=y,["xstart"]=x,["ystart"]=y,["sprite_index"]=o.GetProperty("sprite").GetInt32(),["mask_index"]=o.GetProperty("mask").GetInt32(),["solid"]=o.GetProperty("solid").GetBoolean(),["visible"]=o.GetProperty("visible").GetBoolean(),["persistent"]=o.GetProperty("persistent").GetBoolean(),["depth"]=o.GetProperty("depth").GetInt32(),["image_index"]=0,["image_speed"]=1,["image_xscale"]=1,["image_yscale"]=1,["image_angle"]=0,["image_alpha"]=1,["image_blend"]=16777215,["hspeed"]=0,["vspeed"]=0,["speed"]=0,["direction"]=0,["friction"]=0,["gravity"]=0,["gravity_direction"]=270,["path_index"]=-1,["timeline_index"]=-1})instance.Scope.Write(pair.Key,pair.Value);
        for(int i=0;i<12;i++)instance.Scope.Write("alarm",-1,[i]);
        Instances.Add(instance);
        if(run)Event(instance,0,0);
        return instance;
    }

    public bool HasEvent(int obj,int kind,int sub)
    {
        while(Objects.ContainsKey(obj))
        {
            if(addedEvents.ContainsKey((obj,kind,sub))||staticEvents.ContainsKey((obj,kind,sub)))return true;
            obj=parents.GetValueOrDefault(obj,-1);
        }
        return false;
    }

    public bool Event(GmInstance instance,int kind,int sub,GmInstance? other=null,int? start=null)
    {
        int obj=start??instance.Object;
        while(Objects.TryGetValue(obj,out var definition))
        {
            if(addedEvents.TryGetValue((obj,kind,sub),out var added))
            {
                var context=new GmContext(instance,other){EventObject=obj,EventKind=kind,EventSub=sub};
                try
                {
                    if(!clearedEvents.Contains((obj,kind,sub))&&staticEvents.TryGetValue((obj,kind,sub),out var actions))Actions(actions,context);
                    foreach(var code in added.ToArray())Evaluator.Run(code,context);
                }
                catch(GmFlow f)when(f.Kind is "exit" or "return"){}
                return true;
            }
                if(staticEvents.TryGetValue((obj,kind,sub),out var entries))
                {
                    var c=new GmContext(instance,other){EventObject=obj,EventKind=kind,EventSub=sub};
                    try{Actions(entries,c);}catch(GmFlow f)when(f.Kind is "exit" or "return"){}
                    return true;
                }
            obj=parents.GetValueOrDefault(obj,-1);
        }
        return false;
    }

    private void Actions(JsonElement actions,GmContext context)
    {
        var entries=actions.EnumerateArray().ToArray();
        int index=0;
        while(index<entries.Length)ActionBlock(entries,ref index,context,true);
    }

    private void ActionBlock(JsonElement[] entries,ref int index,GmContext context,bool execute)
    {
        if(index>=entries.Length)return;
        var action=entries[index++];
        int kind=action.GetProperty("kind").GetInt32();
        if(kind==1)
        {
            while(index<entries.Length&&entries[index].GetProperty("kind").GetInt32()!=2)ActionBlock(entries,ref index,context,execute);
            if(index<entries.Length)index++;
            return;
        }
        if(kind==4){if(execute)throw new GmFlow("exit");return;}
        if(kind is 2 or 3)return;
        bool condition=action.GetProperty("condition").GetBoolean();
        var result=execute?InvokeAction(action,context):new GmValue(0);
        if(condition)
        {
            bool passed=result.Truth^action.GetProperty("invert").GetBoolean();
            ActionBlock(entries,ref index,context,execute&&passed);
            if(index<entries.Length&&entries[index].GetProperty("kind").GetInt32()==3)
            {index++;ActionBlock(entries,ref index,context,execute&&!passed);}
        }
    }

    private GmValue InvokeAction(JsonElement action,GmContext context)
    {
        GmValue result=0;
        var targets=action.GetProperty("target").GetInt32()==-1?[context.Self]:Select(action.GetProperty("target").GetInt32(),context).ToArray();
        foreach(var target in targets)
        {
            var c=new GmContext(target,context.Other,context.Arguments){EventObject=context.EventObject,EventKind=context.EventKind,EventSub=context.EventSub,Locals=context.Locals};
            if(action.GetProperty("code").ValueKind==JsonValueKind.String)result=Evaluator.Run(action.GetProperty("code").GetString()!,c);
            else if(action.GetProperty("execution").GetInt32()==1)
            {
                var args=action.GetProperty("parameters").EnumerateArray().Select(p=>Evaluator.Eval(p,c)).ToArray();
                c.Locals.Write("argument_relative",action.GetProperty("relative").GetBoolean());
                result=Call(action.GetProperty("function").GetString()!,args,c);
            }
        }
        return result;
    }

    public void LoadRoom(int id)
    {
        if(!Rooms.TryGetValue(id,out var room))throw new InvalidOperationException("Missing room "+id);
        Evaluator.ResetBudget();
        foreach(var i in Instances.Where(i=>!i.Destroyed).ToArray())Event(i,7,5);
        Instances.RemoveAll(i=>!i.Scope.ReadOr("persistent").Truth||i.Destroyed);
        CurrentRoom=id;PendingRoom=null;
        Tiles.Clear();Tiles.AddRange(room.GetProperty("tiles").EnumerateArray());HiddenTileLayers.Clear();
        nextTile=Math.Max(nextTile,Manifest.GetProperty("lastTileId").GetInt32());
        Engine.Write("room_width",room.GetProperty("width").GetInt32());Engine.Write("room_height",room.GetProperty("height").GetInt32());Engine.Write("room_speed",room.GetProperty("speed").GetInt32());
        Engine.Write("background_color",room.GetProperty("colour").GetDouble());Engine.Write("view_enabled",room.GetProperty("viewsEnabled").GetBoolean());
        int vi=0;
        foreach(var view in room.GetProperty("views").EnumerateArray())
        {
            foreach(var pair in new Dictionary<string,string>{{"xview","x"},{"yview","y"},{"wview","width"},{"hview","height"},{"xport","portX"},{"yport","portY"},{"wport","portWidth"},{"hport","portHeight"},{"hborder","hborder"},{"vborder","vborder"},{"hspeed","hspeed"},{"vspeed","vspeed"},{"object","target"}})Engine.Write("view_"+pair.Key,view.GetProperty(pair.Value).GetDouble(),[vi]);
            Engine.Write("view_visible",view.GetProperty("visible").GetBoolean(),[vi++]);
        }
        var created=new List<(GmInstance Instance,JsonElement Data)>();
        foreach(var data in room.GetProperty("instances").EnumerateArray())
        {
            var i=Create(data.GetProperty("object").GetInt32(),data.GetProperty("x").GetDouble(),data.GetProperty("y").GetDouble(),data.GetProperty("id").GetInt32(),false);
            i.Scope.Write("image_xscale",data.GetProperty("xscale").GetDouble());i.Scope.Write("image_yscale",data.GetProperty("yscale").GetDouble());i.Scope.Write("image_angle",data.GetProperty("angle").GetDouble());i.Scope.Write("image_blend",data.GetProperty("blend").GetDouble());
            created.Add((i,data));
        }
        foreach(var (i,data) in created){Evaluator.Run(data.GetProperty("code").GetString()!,new(i));Event(i,0,0);}
        var dummy=Instances.FirstOrDefault(i=>!i.Destroyed)??new GmInstance{Id=-1000,Object=-1};
        Evaluator.Run(room.GetProperty("code").GetString()!,new(dummy));
        foreach(var i in Instances.Where(i=>!i.Destroyed).ToArray())Event(i,7,4);
        Trace.Add("room:"+room.GetProperty("name").GetString());
    }

    public void Step()
    {
        if(Ended)return;
        Evaluator.ResetBudget();
        if(PendingRoom is int next){LoadRoom(next);return;}
        Frame++;Engine.Write("current_time",Frame*1000.0/Math.Max(1,Engine.Read("room_speed").Number));
        var live=Instances.Where(i=>!i.Destroyed&&i.Active).ToArray();
        foreach(var i in live){i.Scope.Write("xprevious",i.Scope.ReadOr("x"));i.Scope.Write("yprevious",i.Scope.ReadOr("y"));Event(i,3,1);}
        foreach(var i in live.Where(i=>!i.Destroyed&&i.Active))for(int a=0;a<12;a++)
        {
            var t=i.Scope.Read("alarm",[a]).Int;
            if(t>=0){i.Scope.Write("alarm",t-1,[a]);if(t==1)Event(i,2,a);}
        }
        InputEvents?.Invoke();
        foreach(var i in live.Where(i=>!i.Destroyed&&i.Active))Event(i,3,0);
        foreach(var i in live.Where(i=>!i.Destroyed&&i.Active))
        {
            var h=i.Scope.ReadOr("hspeed").Number;var v=i.Scope.ReadOr("vspeed").Number;
            var friction=i.Scope.ReadOr("friction").Number;
            if(friction!=0)
            {
                double speed=Math.Sqrt(h*h+v*v),remaining=Math.Max(0,speed-friction);
                if(speed>0){h*=remaining/speed;v*=remaining/speed;Write(i,"hspeed",h);Write(i,"vspeed",v);}
            }
            var g=i.Scope.ReadOr("gravity").Number;var d=i.Scope.ReadOr("gravity_direction").Number*Math.PI/180;
            if(g!=0){h+=g*Math.Cos(d);v-=g*Math.Sin(d);Write(i,"hspeed",h);Write(i,"vspeed",v);}
            i.Scope.Write("x",i.Scope.ReadOr("x").Number+h);i.Scope.Write("y",i.Scope.ReadOr("y").Number+v);
        }
        BoundaryEvents();
        foreach(var i in live.Where(i=>!i.Destroyed&&i.Active))
        {
            var targets=new HashSet<int>();
            int obj=i.Object;
            while(Objects.TryGetValue(obj,out var definition))
            {
                foreach(var ev in definition.GetProperty("events").EnumerateArray())if(ev.GetProperty("kind").GetInt32()==4)targets.Add(ev.GetProperty("sub").GetInt32());
                foreach(var ev in addedEvents.Keys)if(ev.Object==obj&&ev.Kind==4)targets.Add(ev.Sub);
                obj=definition.GetProperty("parent").GetInt32();
            }
            foreach(int target in targets)
            foreach(var other in Instances.Where(o=>o!=i&&!o.Destroyed&&o.Active&&IsObject(o.Object,target)).ToArray())
            {
                if(i.Destroyed)break;
                if(!Touches(i,other,i.Scope.ReadOr("x").Number,i.Scope.ReadOr("y").Number))continue;
                if(other.Scope.ReadOr("solid").Truth){Write(i,"x",i.Scope.ReadOr("xprevious"));Write(i,"y",i.Scope.ReadOr("yprevious"));}
                Event(i,4,target,other);
            }
        }
        foreach(var i in live.Where(i=>!i.Destroyed&&i.Active))Event(i,3,2);
        Instances.RemoveAll(i=>i.Destroyed);
        Redraw?.Invoke();
        foreach(var i in Instances.Where(i=>!i.Destroyed&&i.Active).ToArray())
        {
            var sprite=i.Scope.ReadOr("sprite_index",-1).Int;
            int n=Sprites.TryGetValue(sprite,out var s)?s.GetProperty("frames").GetArrayLength():0;
            var index=i.Scope.ReadOr("image_index").Number+i.Scope.ReadOr("image_speed").Number;
            i.Scope.Write("image_index",index);
            if(index>=n)
            {
                i.Scope.Write("image_index",index-n);
                Event(i,7,7);
            }
        }
        Instances.RemoveAll(i=>i.Destroyed);
        if(PendingRoom is int destination)LoadRoom(destination);
    }

    public static double Direction(double x1,double y1,double x2,double y2)=>(Math.Atan2(y1-y2,x2-x1)*180/Math.PI+360)%360;
}

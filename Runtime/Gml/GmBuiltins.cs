using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private readonly Dictionary<int,List<GmValue>> lists=new();
    // Text files are read lazily: the item/motion data files are large, and materializing every line
    // (plus a full CR-stripped copy) up front dominated the room-load allocation.
    private sealed class TextFileCursor
    {
        private readonly string? text;
        private readonly StreamReader? stream;
        private int offset;
        private string? peeked;
        private bool ended;

        public TextFileCursor(string text)
        {
            this.text = text;
        }

        public TextFileCursor(StreamReader stream)
        {
            this.stream = stream;
        }

        public string? Peek()
        {
            if (peeked is not null || ended)
                return peeked;
            if (text is not null)
            {
                if (offset >= text.Length)
                {
                    ended = true;
                    return null;
                }
                int newline = text.IndexOf('\n', offset);
                int end = newline < 0 ? text.Length : newline;
                var line = text.AsSpan(offset, end - offset);
                offset = end + 1;
                peeked = line.IndexOf('\r') >= 0 ? line.ToString().Replace("\r", "") : line.ToString();
                return peeked;
            }
            peeked = stream!.ReadLine();
            if (peeked is null)
                ended = true;
            return peeked;
        }

        public void Advance()
        {
            if (Peek() is not null)
                peeked = null;
        }

        public void Close() => stream?.Dispose();
    }

    private readonly Dictionary<int, TextFileCursor> textReads = new();
    private readonly Dictionary<int,StreamWriter> textWrites=new();
    private readonly Dictionary<int,FileStream> binaries=new();
    private readonly Dictionary<int,string> externals=new();
    private string[] foundFiles=[];
    private int foundIndex;
    private int handle=1;
    private readonly Dictionary<string,string> dynamicCode=new(StringComparer.Ordinal);

    // sources.json holds every packed GML source; materializing the whole map (and copying all of its
    // strings) on the first read dominated the room-load allocation, so entries are resolved on demand.
    private JsonDocument? sourceDocument;
    private readonly Dictionary<string,string?> sourceEntries=new(StringComparer.OrdinalIgnoreCase);

    private bool TrySource(string path,out string text)
    {
        text="";
        if(!sourceEntries.TryGetValue(path,out var cached))
        {
            sourceDocument??=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root,"sources.json")));
            cached=null;
            foreach(var property in sourceDocument.RootElement.EnumerateObject())
                if(string.Equals(property.Name,path,StringComparison.OrdinalIgnoreCase))
                {
                    cached=property.Value.GetString();
                    break;
                }
            sourceEntries[path]=cached;
        }
        if(cached is null)return false;
        text=cached;
        return true;
    }

    private static string CasePath(string root,string path)
    {
        var resolved=root;
        foreach(var part in path.Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            if(part==".")continue;
            var exact=Path.Combine(resolved,part);
            if(File.Exists(exact)||Directory.Exists(exact)||!Directory.Exists(resolved)){resolved=exact;continue;}
            resolved=Directory.EnumerateFileSystemEntries(resolved).FirstOrDefault(p=>string.Equals(Path.GetFileName(p),part,StringComparison.OrdinalIgnoreCase))??exact;
        }
        return resolved;
    }

    private string? SavedFile(string path)
    {
        var resolved=Resolve(path);
        return resolved.StartsWith(UserRoot+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&File.Exists(resolved)?resolved:null;
    }

    private string[] FindFiles(string pattern)
    {
        pattern=pattern.Replace('\\','/');
        if(Path.IsPathRooted(pattern))pattern=Path.GetRelativePath(SourceRoot,pattern).Replace('\\','/');
        string parent=Path.GetDirectoryName(pattern)?.Replace('\\','/')??"",search=Path.GetFileName(pattern);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Matches(string name)=>System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(search,name,true);
        foreach(string root in new[]{SourceRoot,UserRoot})
        {
            string directory=CasePath(root,parent);
            if(Directory.Exists(directory))foreach(string path in Directory.EnumerateFiles(directory))
            {
                string name=Path.GetFileName(path);if(Matches(name))names.Add(name);
            }
        }
        foreach(string path in Media.Keys.Concat(External.Keys))
            if(string.Equals(Path.GetDirectoryName(path)?.Replace('\\','/')??"",parent,StringComparison.OrdinalIgnoreCase)&&Matches(Path.GetFileName(path)))names.Add(Path.GetFileName(path));
        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string Resolve(string path,bool writing=false)
    {
        path=path.Replace('\\','/');
        if(Path.IsPathRooted(path))
        {
            var relative=Path.GetRelativePath(SourceRoot,path);
            if(relative.StartsWith("..",StringComparison.Ordinal)||Path.IsPathRooted(relative))
                throw new InvalidOperationException("Path outside game assets: "+path);
            path=relative;
        }
        var userPath=Path.GetFullPath(Path.Combine(UserRoot,path));
        if(!userPath.StartsWith(UserRoot+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&userPath!=UserRoot)throw new InvalidOperationException("Invalid game path "+path);
        path=Path.GetRelativePath(UserRoot,userPath).Replace('\\','/');
        userPath=CasePath(UserRoot,path);
        if(writing){Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);return userPath;}
        if(File.Exists(userPath))return userPath;
        return CasePath(SourceRoot,path);
    }

    public string Compile(string source)
    {
        if(dynamicCode.TryGetValue(source,out var existing))return existing;
        var compiler=GmPaths.FindCompiler(Root);
        using var process=Process.Start(new ProcessStartInfo(compiler,"--parse"){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true,StandardInputEncoding=new System.Text.UTF8Encoding(false),StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8})!;
        process.StandardInput.Write(source);process.StandardInput.Close();
        var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();process.WaitForExit();
        if(process.ExitCode!=0)throw new InvalidOperationException("GML compilation: "+error);
        var key="dynamic/"+dynamicCode.Count;
        Code[key]=JsonDocument.Parse(output).RootElement;dynamicCode[source]=key;
        return key;
    }

    private readonly Dictionary<string,string> fileCode=new(StringComparer.OrdinalIgnoreCase);

    internal string FileCode(string path)
    {
        path=path.Replace('\\','/');
        if(fileCode.TryGetValue(path,out var cached))return cached;
        var saved=SavedFile(path);
        var code=saved is not null?Compile(File.ReadAllText(saved)):External.TryGetValue(path,out var external)?external:Compile(File.ReadAllText(Resolve(path)));
        return fileCode[path]=code;
    }

    // Compile-time call-site resolution: a static name binds straight to the platform or math
    // implementation, so the hot path skips the generic dispatch chain (call stats, Scripts lookup,
    // name comparisons). Returns null when the call must keep the generic path.
    internal static bool CallSiteSpecialization =
        System.Environment.GetEnvironmentVariable("SLACK_CALL_SITE") != "0";

    internal Func<GmValue[], GmContext, GmValue>? CallSite(string name, int count)
    {
        if (!CallSiteSpecialization || Scripts.ContainsKey(name))
            return null;
        if (count is 1 or 2 && HasFixedArguments(name, count) && MathSite(name) is { } math)
            return (a, c) =>
                CallObserver is null && CallInterceptor is null ? math(a) : CallFixed(name, a[0], count == 2 ? a[1] : 0, count, c);
        if (name is "make_color_rgb" && count == 3)
            return (a, c) =>
                CallObserver is null && CallInterceptor is null
                    ? ((a[0].Int & 255) | ((a[1].Int & 255) << 8) | ((a[2].Int & 255) << 16))
                    : Call(name, a, c);
        if (Platform is not null && name.Length > 5 && name.StartsWith("draw_", StringComparison.Ordinal) && name != "draw_sprite_blur")
            return (a, c) =>
            {
                if (CallObserver is not null || CallInterceptor is not null)
                    return Call(name, a, c);
                return Platform?.Invoke(name, a, c) ?? Call(name, a, c);
            };
        return null;
    }

    private static Func<GmValue[], GmValue>? MathSite(string name) => name switch
    {
        "abs" => a => System.Math.Abs(a[0].Number),
        "floor" => a => System.Math.Floor(a[0].Number),
        "ceil" => a => System.Math.Ceiling(a[0].Number),
        "round" => a => System.Math.Round(a[0].Number, MidpointRounding.ToEven),
        "sign" => a => System.Math.Sign(a[0].Number),
        "sqrt" => a => System.Math.Sqrt(a[0].Number),
        "sqr" => a => a[0].Number * a[0].Number,
        "sin" => a => System.Math.Sin(a[0].Number),
        "cos" => a => System.Math.Cos(a[0].Number),
        "power" => a => System.Math.Pow(a[0].Number, a[1].Number),
        "min" => a => double.IsNaN(a[0].Number) || !(a[1].Number < a[0].Number || double.IsNaN(a[1].Number)) ? a[0].Number : a[1].Number,
        "max" => a => double.IsNaN(a[0].Number) || a[1].Number > a[0].Number ? a[1].Number : a[0].Number,
        _ => null,
    };

    internal static bool HasFixedArguments(string name,int count)=>count switch
    {
        1=>name is "abs" or "ceil" or "floor" or "round" or "sign" or "sqrt" or "sqr" or "sin" or "cos" or "arccos" or "degtorad" or "radtodeg" or "random" or "irandom"
            or "sprite_get_number" or "sprite_get_width" or "sprite_get_height" or "sprite_get_xoffset" or "sprite_get_yoffset" or "instance_number" or "instance_exists" or "variable_local_exists",
        2=>name is "power" or "min" or "max" or "random_range" or "irandom_range" or "lengthdir_x" or "lengthdir_y",
        3=>name is "place_meeting" or "instance_place",
        _=>false
    };

    internal GmValue CallFixed(string name,GmValue first,GmValue second,int count,GmContext context)
    {
        if(Scripts.ContainsKey(name))return Call(name,count==1?[first]:[first,second],context);
        Calls[name]=Calls.GetValueOrDefault(name)+1;
        return FixedResult(name,first,second,context);
    }

    internal GmValue CallFixed(string name,GmValue first,GmValue second,GmValue third,GmContext context)
    {
        if(Scripts.ContainsKey(name)||Platform is not null)return Call(name,[first,second,third],context);
        Calls[name]=Calls.GetValueOrDefault(name)+1;
        var instance=InstanceAt(first.Number,second.Number,third.Int,context);
        return name switch{"place_meeting"=>instance is not null,"instance_place"=>instance?.Id??-4,_=>throw new InvalidOperationException("Unsupported fixed argument call "+name)};
    }

    private GmValue FixedResult(string name,GmValue first,GmValue second,GmContext context)=>name switch
    {
        "abs"=>Math.Abs(first.Number),"ceil"=>Math.Ceiling(first.Number),"floor"=>Math.Floor(first.Number),"round"=>Math.Round(first.Number,MidpointRounding.ToEven),
        "sign"=>Math.Sign(first.Number),"sqrt"=>Math.Sqrt(first.Number),"sqr"=>first.Number*first.Number,"power"=>Math.Pow(first.Number,second.Number),
        "sin"=>Math.Sin(first.Number),"cos"=>Math.Cos(first.Number),"arccos"=>Math.Acos(first.Number),"degtorad"=>first.Number*Math.PI/180,"radtodeg"=>first.Number*180/Math.PI,
        "min"=>double.IsNaN(first.Number)||!(second.Number<first.Number||double.IsNaN(second.Number))?first.Number:second.Number,
        "max"=>double.IsNaN(first.Number)||second.Number>first.Number?second.Number:first.Number,
        "random"=>Random.Next(first.Number),
        "random_range"=>Math.Min(first.Number,second.Number)+Random.Next(Math.Abs(second.Number-first.Number)),
        "irandom"=>Random.NextInt(unchecked((uint)first.Int)),
        "irandom_range"=>unchecked(Math.Min(first.Int,second.Int)+Random.NextInt((uint)Math.Abs((long)second.Int-first.Int))),
        "lengthdir_x"=>Math.Cos(second.Number*Math.PI/180)*first.Number,"lengthdir_y"=>-Math.Sin(second.Number*Math.PI/180)*first.Number,
        "sprite_get_number"=>Sprites.TryGetValue(first.Int,out var sprite)?sprite.GetProperty("frames").GetArrayLength():0,
        "sprite_get_width" or "sprite_get_height"=>Sprites.TryGetValue(first.Int,out var sizedSprite)&&sizedSprite.GetProperty("frames").GetArrayLength()>0?sizedSprite.GetProperty("frames")[0].GetProperty(name=="sprite_get_width"?"width":"height").GetInt32():0,
        "sprite_get_xoffset" or "sprite_get_yoffset"=>Sprites.TryGetValue(first.Int,out var offsetSprite)?offsetSprite.GetProperty(name=="sprite_get_xoffset"?"originX":"originY").GetInt32():0,
        "instance_number"=>InstanceCount(first.Int,context),"instance_exists"=>Select(first.Int,context).Any(),
        "variable_local_exists"=>context.Self.Scope.Values.ContainsKey(first.ToString()),
        _=>throw new InvalidOperationException("Unsupported fixed argument call "+name)
    };

    public GmValue Call(string name,GmValue[] a,GmContext c)
    {
        var observed=CallObserver;
        if(CallInterceptor is not null&&CallInterceptor(name,c,out var intercepted))return observed is null?intercepted:observed(name,c,intercepted);
        if(observed is not null)return observed(name,c,CallCore(name,a,c));
        return CallCore(name,a,c);
    }
    private GmValue CallCore(string name,GmValue[] a,GmContext c)
    {
        Calls[name]=Calls.GetValueOrDefault(name)+1;
        if(name=="draw_sprite_blur")return DrawSpriteBlur(a,c);
        // Draw commands are served by the rendering platform; skipping the asset/particle/room lookup
        // chain keeps the per-command dispatch to one delegate call.
        if(name.Length>5&&name.StartsWith("draw_",StringComparison.Ordinal)&&Platform is not null)
        {
            var drawn=Platform(name,a,c);
            if(drawn is GmValue value)return value;
        }
        if(name is "MotionInitialize" or "DoubleMotionInitialize")return InitializeMotion(name,a,c);
        if(name=="setMotionForPlayer")return LoadPlayerMotion(a,c);
        if(name=="addLoadCode"&&External.TryGetValue(a[3].ToString().Replace('\\','/'),out var loadedCode))
        {
            var eventKey=(a[0].Int,a[1].Int,a[2].Int);
            if(!addedEvents.TryGetValue(eventKey,out var events))addedEvents[eventKey]=events=new();
            events.Add(loadedCode);InvalidateEvents();return 0;
        }
        if(Scripts.TryGetValue(name,out var key))
        {
            var call=RentContext(c.Self,c.Other,a,null);
            call.EventObject=c.EventObject;call.EventKind=c.EventKind;call.EventSub=c.EventSub;
            try{return Evaluator.Run(key,call);}
            finally{ReturnContext(call,true);}
        }
        double N(int i)=>i<a.Length?a[i].Number:0;
        int I(int i)=>i<a.Length?a[i].Int:0;
        string S(int i)=>i<a.Length?a[i].ToString():"";
        GmValue V(int i)=>i<a.Length?a[i]:0;
        var Self=c.Self;
        switch(name)
        {
            case "slack_character_jump_available":return V(0).Truth || (V(1).Truth && (ExtendCharacterJump?.Invoke(Self)??false));
            case "slack_character_jumped":CharacterJumped?.Invoke(Self);return 0;
            case "action_inherited":return Call("event_inherited",[],c);
            case "action_set_alarm":Self.Scope.Write("alarm",V(0),[I(1)]);return 0;
            case "action_next_room":return Call("room_goto_next",[],c);
            case "action_kill_object":return Call("instance_destroy",[],c);
            case "action_if":return V(0).Truth;
            case "action_if_variable":return I(2)==0?V(0).Equals(V(1)):I(2)==1?V(0).CompareTo(V(1))<0:V(0).CompareTo(V(1))>0;
            case "action_if_number":{int n=Select(I(0),c).Count();return I(2)==0?n==I(1):I(2)==1?n<I(1):n>I(1);}
            case "action_if_dice":return Random.Next(N(0))<1;
            case "action_set_hspeed":Write(Self,"hspeed",c.Locals.ReadOr("argument_relative").Truth?Self.Scope.ReadOr("hspeed")+V(0):V(0));return 0;
            case "action_set_vspeed":Write(Self,"vspeed",c.Locals.ReadOr("argument_relative").Truth?Self.Scope.ReadOr("vspeed")+V(0):V(0));return 0;
            case "action_set_motion":return Call(c.Locals.ReadOr("argument_relative").Truth?"motion_add":"motion_set",a,c);
            case "action_move":
            {
                var directions=S(0);if(directions.Length!=9)throw new ArgumentException("Direction selector must have nine entries");
                if(!directions.Contains('1'))return 0;
                int selected;do{selected=Random.NextInt(8);}while(directions[selected]!='1');
                double speed=selected==4?0:N(1),direction=new[]{225,270,315,180,0,0,135,90,45}[selected];
                if(c.Locals.ReadOr("argument_relative").Truth)return Call("motion_add",[direction,speed],c);
                Write(Self,"direction",direction);Write(Self,"speed",speed);return 0;
            }
            case "action_set_gravity":Write(Self,"gravity_direction",V(0));Write(Self,"gravity",V(1));return 0;
            case "action_move_to":{var relative=c.Locals.ReadOr("argument_relative").Truth;Write(Self,"x",relative?Self.Scope.ReadOr("x")+V(0):V(0));Write(Self,"y",relative?Self.Scope.ReadOr("y")+V(1):V(1));return 0;}
            case "action_move_start":Write(Self,"x",Self.Scope.ReadOr("xstart"));Write(Self,"y",Self.Scope.ReadOr("ystart"));return 0;
            case "action_create_object":return Call("instance_create",[V(1),V(2),V(0)],c);
            case "action_execute_script":{var script=Manifest.GetProperty("scripts").EnumerateArray().First(s=>s.GetProperty("id").GetInt32()==I(0));return Call(script.GetProperty("name").GetString()!,a.Skip(1).ToArray(),c);}
            case "action_sprite_set":Write(Self,"sprite_index",V(0));Write(Self,"image_index",V(1));Write(Self,"image_speed",V(2));return 0;
            case "action_sprite_transform":Write(Self,"image_xscale",V(0));Write(Self,"image_yscale",V(1));Write(Self,"image_angle",V(2));return 0;
            case "action_sound":return Call(I(1)==0?"sound_play":"sound_loop",[V(0)],c);
            case "action_end_sound":return Call("sound_stop",a,c);
            case "abs":case "ceil":case "floor":case "round":case "sign":case "sqrt":case "sqr":case "power":
            case "sin":case "cos":case "arccos":case "degtorad":case "radtodeg":case "random":case "random_range":
            case "irandom":case "irandom_range":case "lengthdir_x":case "lengthdir_y":return FixedResult(name,V(0),V(1),c);
            case "min":return a.Min(v=>v.Number);case "max":return a.Max(v=>v.Number);
            case "choose":return a.Length>0?a[Random.NextInt((uint)(a.Length-1))]:0;
            case "random_get_seed":return Random.Seed;
            case "random_set_seed":Random.Seed=I(0);return 0;
            case "randomize":Random.Randomize();return 0;
            case "point_distance":return Math.Sqrt(Math.Pow(N(2)-N(0),2)+Math.Pow(N(3)-N(1),2));case "point_direction":return Direction(N(0),N(1),N(2),N(3));
            case "distance_to_point":return Math.Sqrt(Math.Pow(N(0)-Self.Scope.ReadOr("x").Number,2)+Math.Pow(N(1)-Self.Scope.ReadOr("y").Number,2));
            case "string":return S(0);case "real":return double.Parse(S(0),CultureInfo.InvariantCulture);case "is_real":return !V(0).IsString;case "is_string":return V(0).IsString;case "ord":return S(0).Length>0?(int)S(0)[0]:0;
            case "string_length":return S(0).Length;case "string_lower":return S(0).ToLowerInvariant();case "string_pos":return S(1).IndexOf(S(0),StringComparison.Ordinal)+1;
            case "string_char_at":return I(1)<1||I(1)>S(0).Length?"":S(0).Substring(I(1)-1,1);
            case "string_copy":{int start=Math.Clamp(I(1)-1,0,S(0).Length);return S(0).Substring(start,Math.Clamp(I(2),0,S(0).Length-start));}
            case "string_replace_all":return S(0).Replace(S(1),S(2),StringComparison.Ordinal);
            case "string_format":return N(0).ToString("F"+Math.Clamp(I(2),0,16),CultureInfo.InvariantCulture).PadLeft(Math.Max(0,I(1)));
            case "make_color_rgb":return (I(0)&255)|((I(1)&255)<<8)|((I(2)&255)<<16);
            case "make_color_hsv":
            {
                double h=(N(0)%256+256)%256*6/255,s=Math.Clamp(N(1)/255,0,1),v=Math.Clamp(N(2)/255,0,1);
                double chroma=v*s,x=chroma*(1-Math.Abs(h%2-1)),m=v-chroma;
                var rgb=((int)h%6) switch{0=>(chroma,x,0.0),1=>(x,chroma,0.0),2=>(0.0,chroma,x),3=>(0.0,x,chroma),4=>(x,0.0,chroma),_=>(chroma,0.0,x)};
                return (int)((rgb.Item1+m)*255)|((int)((rgb.Item2+m)*255)<<8)|((int)((rgb.Item3+m)*255)<<16);
            }
            case "color_get_hue":case "color_get_saturation":case "color_get_value":
            {
                double r=(I(0)&255)/255.0,g=((I(0)>>8)&255)/255.0,b=((I(0)>>16)&255)/255.0;
                double max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min;
                if(name=="color_get_value")return max*255;
                if(name=="color_get_saturation")return max==0?0:d/max*255;
                double h=d==0?0:max==r?(g-b)/d:max==g?(b-r)/d+2:(r-g)/d+4;
                return ((h/6+1)%1)*255;
            }
            case "color_get_red":return I(0)&255;case "color_get_green":return (I(0)>>8)&255;case "color_get_blue":return (I(0)>>16)&255;
            case "ds_list_create":{int id=handle++;lists[id]=new();return id;}
            case "ds_list_add":lists[I(0)].AddRange(a.Skip(1));return 0;
            case "ds_list_size":return lists[I(0)].Count;
            case "ds_list_find_index":return lists[I(0)].FindIndex(v=>v.Equals(V(1)));
            case "ds_list_find_value":return I(1)>=0&&I(1)<lists[I(0)].Count?lists[I(0)][I(1)]:0;
            case "ds_list_delete":lists[I(0)].RemoveAt(I(1));return 0;
            case "variable_global_exists":return Globals.Values.ContainsKey(S(0));
            case "variable_local_exists":return Self.Scope.Values.ContainsKey(S(0));
            case "variable_global_get":return Globals.Read(S(0));case "variable_global_set":Globals.Write(S(0),V(1));return 0;
            case "variable_local_set":Write(Self,S(0),V(1));return 0;
            case "variable_global_array_get":return Globals.ReadOr(S(0),new GmIndices(I(1)));case "variable_global_array2_get":return Globals.ReadOr(S(0),new GmIndices(I(1),I(2)));
            case "variable_local_array_get":return Self.Scope.ReadOr(S(0),new GmIndices(I(1)));case "variable_local_array2_get":return Self.Scope.ReadOr(S(0),new GmIndices(I(1),I(2)));
            case "instance_create":return Create(I(2),N(0),N(1)).Id;
            case "instance_destroy":Event(Self,1,0);Self.Destroyed=true;return 0;
            case "instance_exists":case "instance_number":return FixedResult(name,V(0),default,c);
            case "instance_find":return Select(I(0),c).Skip(Math.Max(0,I(1))).FirstOrDefault()?.Id??-4;
            case "instance_nearest":return Select(I(2),c).OrderBy(i=>Math.Pow(i.Scope.ReadOr("x").Number-N(0),2)+Math.Pow(i.Scope.ReadOr("y").Number-N(1),2)).FirstOrDefault()?.Id??-4;
            case "instance_activate_object":foreach(var i in ActivationInstances(I(0)))i.Active=true;return 0;
            case "instance_deactivate_object":foreach(var i in Select(I(0),c))i.Active=false;return 0;
            case "instance_activate_region":case "instance_deactivate_region":
            {
                var region=new GmBounds(N(0),N(1),N(0)+N(2),N(1)+N(3));
                bool activate=name=="instance_activate_region",inside=N(4)>=0.5,
                    notme=name=="instance_deactivate_region"&&N(5)>=0.5;
                EnsureCollisionIndex();
                if(inside)
                {
                    foreach(var i in Nearby(region))
                    {
                        if(i.Destroyed||i.Active==activate||(notme&&i==Self))continue;
                        if(collisionMembership.TryGetValue(i,out var bounds)&&bounds.Overlaps(region))i.Active=activate;
                    }
                    foreach(var i in emptyColliders)
                    {
                        if(i.Destroyed||i.Active==activate||(notme&&i==Self))continue;
                        double x=i.Scope.ReadOr("x").Number,y=i.Scope.ReadOr("y").Number;
                        if(new GmBounds(x,y,x,y).Overlaps(region))i.Active=activate;
                    }
                }
                else
                {
                    foreach(var i in Instances)
                    {
                        if(i.Destroyed||i.Active==activate||(notme&&i==Self))continue;
                        bool overlap;
                        if(collisionMembership.TryGetValue(i,out var bounds))overlap=bounds.Overlaps(region);
                        else{double x=i.Scope.ReadOr("x").Number,y=i.Scope.ReadOr("y").Number;overlap=new GmBounds(x,y,x,y).Overlaps(region);}
                        if(!overlap)i.Active=activate;
                    }
                }
                return 0;
            }
            case "instance_change":{if(N(1)>=0.5)Event(Self,1,0);ChangeObject(Self,I(0));if(N(1)>=0.5)Event(Self,0,0);return 0;}
            case "object_get_parent":return parents.GetValueOrDefault(I(0),-1);case "object_exists":return Objects.ContainsKey(I(0));
            case "event_user":Event(Self,7,10+I(0),c.Other);return 0;
            case "event_perform":Event(Self,I(0),I(1),c.Other);return 0;
            case "event_inherited":Event(Self,c.EventKind,c.EventSub,c.Other,Objects[c.EventObject].GetProperty("parent").GetInt32());return 0;
            case "room_exists":return Rooms.ContainsKey(I(0));case "room_get_name":return Rooms[I(0)].GetProperty("name").GetString()!;
            case "room_set_width":case "room_set_height":case "room_set_caption":
            {
                int room=I(0);var overrides=Overrides(room);
                if(name=="room_set_caption")
                {
                    string caption=S(1);
                    if(overrides.Caption==caption)return 0;
                    overrides.Caption=caption;
                }
                else
                {
                    int value=I(1);
                    if(name=="room_set_width")
                    {
                        if(overrides.Width==value)return 0;
                        overrides.Width=value;
                    }
                    else
                    {
                        if(overrides.Height==value)return 0;
                        overrides.Height=value;
                    }
                }
                return 0;
            }
            case "room_set_view":
            {
                var view=Overrides(I(0)).View(I(1));
                bool visible=N(2)>=0.5;
                if(view.Visible!=visible)view.Visible=visible;
                for(int p=0;p<RoomViewFields.Length;p++)
                {
                    int value=I(p+3);
                    if(view.Fields[p]==value)continue;
                    view.Fields[p]=value;
                }
                return 0;
            }
            case "room_next":{var ids=Manifest.GetProperty("roomOrder").EnumerateArray().Select(x=>x.GetInt32()).ToList();int i=ids.IndexOf(I(0));return i>=0&&i+1<ids.Count?ids[i+1]:-1;}
            case "room_goto":PendingRoom=I(0);return 0;case "room_restart":PendingRoom=CurrentRoom;return 0;
            case "room_goto_next":PendingRoom=Call("room_next",[CurrentRoom],c).Int;return 0;
            case "game_end":Ended=true;return 0;
            case "file_exists":{var path=S(0).Replace('\\','/');return path.Equals("bin/bGM/bGM.exe",StringComparison.OrdinalIgnoreCase)||File.Exists(Resolve(path))||External.ContainsKey(path)||Media.ContainsKey(path);}
            case "file_find_first":foundFiles=FindFiles(S(0));foundIndex=0;return foundFiles.FirstOrDefault()??"";
            case "file_find_next":return ++foundIndex<foundFiles.Length?foundFiles[foundIndex]:"";
            case "file_text_open_read":
            {
                int id=handle++;var path=S(0).Replace('\\','/');var saved=SavedFile(path);
                textReads[id]=saved is not null?new TextFileCursor(new StreamReader(saved))
                    :TrySource(path,out var text)?new TextFileCursor(text)
                    :External.ContainsKey(path)?new TextFileCursor("")
                    :new TextFileCursor(new StreamReader(Resolve(path)));
                return id;
            }
            case "file_text_eof":return textReads[I(0)].Peek() is null;
            case "file_text_read_string":return textReads[I(0)].Peek()??"";
            case "file_text_readln":textReads[I(0)].Advance();return 0;
            case "file_text_open_write":{int id=handle++;textWrites[id]=new StreamWriter(Resolve(S(0),true));return id;}
            case "file_text_write_string":textWrites[I(0)].Write(S(1));return 0;
            case "file_text_writeln":textWrites[I(0)].WriteLine();return 0;
            case "file_text_close":if(textReads.Remove(I(0),out var reader))reader.Close();if(textWrites.Remove(I(0),out var writer))writer.Dispose();return 0;
            case "file_bin_open":{int id=handle++;binaries[id]=new FileStream(Resolve(S(0),I(1)!=0),I(1)==0?FileMode.Open:FileMode.OpenOrCreate,I(1)==0?FileAccess.Read:FileAccess.ReadWrite);return id;}
            case "file_bin_read_byte":return binaries[I(0)].ReadByte();
            case "file_bin_write_byte":binaries[I(0)].WriteByte((byte)I(1));return 0;
            case "file_bin_rewrite":binaries[I(0)].SetLength(0);return 0;
            case "file_bin_close":binaries[I(0)].Dispose();binaries.Remove(I(0));return 0;
            case "execute_file":return Evaluator.Run(FileCode(S(0)),c);
            case "execute_string":return Evaluator.Run(Compile(S(0)),c);
            case "screen_redraw":Redraw?.Invoke();return 0;
            case "motion_set":Write(Self,"direction",V(0));Write(Self,"speed",V(1));return 0;
            case "motion_add":Write(Self,"hspeed",Self.Scope.ReadOr("hspeed").Number+Math.Cos(N(0)*Math.PI/180)*N(1));Write(Self,"vspeed",Self.Scope.ReadOr("vspeed").Number-Math.Sin(N(0)*Math.PI/180)*N(1));return 0;
            case "sprite_exists":return Sprites.ContainsKey(I(0));
            case "sprite_get_number":case "sprite_get_width":case "sprite_get_height":case "sprite_get_xoffset":case "sprite_get_yoffset":return FixedResult(name,V(0),default,c);
            case "sprite_get_bbox_left":case "sprite_get_bbox_right":case "sprite_get_bbox_top":case "sprite_get_bbox_bottom":return Sprites[I(0)].GetProperty("masks")[0].GetProperty(name[16..]).GetInt32();
            case "background_get_width":case "background_get_height":{var b=Manifest.GetProperty("backgrounds").EnumerateArray().First(b=>b.GetProperty("id").GetInt32()==I(0));return b.GetProperty(name=="background_get_width"?"width":"height").GetInt32();}
        }
        var particle=ParticleCall(name,a,c);
        if(particle is GmValue particleResult)return particleResult;
        var asset=Assets(name,a,c);
        if(asset is GmValue assetResult)return assetResult;
        var roomAsset=RoomAssets(name,a);
        if(roomAsset is GmValue roomResult)return roomResult;
        var platform=Platform?.Invoke(name,a,c);
        if(platform is GmValue result)return result;
        var collision=Collision(name,a,c);
        if(collision is GmValue hit)return hit;
        Unsupported.Add(name);
        throw new NotSupportedException("Unimplemented GameMaker function "+name);
    }
}

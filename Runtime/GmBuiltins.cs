using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public sealed partial class GmWorld
{
    private readonly Dictionary<int,List<GmValue>> lists=new();
    private readonly Dictionary<int,(string[] Lines,int Line)> textReads=new();
    private readonly Dictionary<int,StreamWriter> textWrites=new();
    private readonly Dictionary<int,FileStream> binaries=new();
    private readonly Dictionary<int,string> externals=new();
    private string[] foundFiles=[];
    private int foundIndex;
    private int handle=1;
    private readonly Dictionary<string,string> dynamicCode=new(StringComparer.Ordinal);
    private Dictionary<string,string>? sourceText;

    private Dictionary<string,string> Sources=>sourceText??=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Root,"sources.json")))!.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);

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
        var compiler=Path.Combine(Path.GetDirectoryName(Root)!,"Tools/Extract/target/release/slack-extract");
        if(!File.Exists(compiler))throw new FileNotFoundException("GML compiler unavailable",compiler);
        using var process=Process.Start(new ProcessStartInfo(compiler,"--parse"){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false})!;
        process.StandardInput.Write(source);process.StandardInput.Close();
        var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();process.WaitForExit();
        if(process.ExitCode!=0)throw new InvalidOperationException("GML compilation: "+error);
        var key="dynamic/"+dynamicCode.Count;
        Code[key]=JsonDocument.Parse(output).RootElement;dynamicCode[source]=key;
        return key;
    }

    public GmValue Call(string name,GmValue[] a,GmContext c)
    {
        Calls[name]=Calls.GetValueOrDefault(name)+1;
        if(name is "MotionInitialize" or "DoubleMotionInitialize")return InitializeMotion(name,a,c);
        if(name=="setMotionForPlayer")return LoadPlayerMotion(a,c);
        if(name=="addLoadCode"&&External.TryGetValue(a[3].ToString().Replace('\\','/'),out var loadedCode))
        {
            var eventKey=(a[0].Int,a[1].Int,a[2].Int);
            if(!addedEvents.TryGetValue(eventKey,out var events))addedEvents[eventKey]=events=new();
            events.Add(loadedCode);return 0;
        }
        if(Scripts.TryGetValue(name,out var key))return Evaluator.Run(key,new(c.Self,c.Other,a){EventObject=c.EventObject,EventKind=c.EventKind,EventSub=c.EventSub});
        double N(int i)=>i<a.Length?a[i].Number:0;
        int I(int i)=>i<a.Length?a[i].Int:0;
        string S(int i)=>i<a.Length?a[i].ToString():"";
        GmValue V(int i)=>i<a.Length?a[i]:0;
        var Self=c.Self;
        switch(name)
        {
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
            case "abs":return Math.Abs(N(0));case "ceil":return Math.Ceiling(N(0));case "floor":return Math.Floor(N(0));case "round":return Math.Round(N(0),MidpointRounding.ToEven);
            case "sign":return Math.Sign(N(0));case "sqrt":return Math.Sqrt(N(0));case "sqr":return N(0)*N(0);case "power":return Math.Pow(N(0),N(1));
            case "sin":return Math.Sin(N(0));case "cos":return Math.Cos(N(0));case "arccos":return Math.Acos(N(0));case "degtorad":return N(0)*Math.PI/180;case "radtodeg":return N(0)*180/Math.PI;
            case "min":return a.Min(v=>v.Number);case "max":return a.Max(v=>v.Number);
            case "random":return Random.Next(N(0));
            case "random_range":return Math.Min(N(0),N(1))+Random.Next(Math.Abs(N(1)-N(0)));
            case "irandom":return Random.NextInt(unchecked((uint)I(0)));
            case "irandom_range":return unchecked(Math.Min(I(0),I(1))+Random.NextInt((uint)Math.Abs((long)I(1)-I(0))));
            case "choose":return a.Length>0?a[Random.NextInt((uint)(a.Length-1))]:0;
            case "random_get_seed":return Random.Seed;
            case "random_set_seed":Random.Seed=I(0);return 0;
            case "randomize":Random.Randomize();return 0;
            case "point_distance":return Math.Sqrt(Math.Pow(N(2)-N(0),2)+Math.Pow(N(3)-N(1),2));case "point_direction":return Direction(N(0),N(1),N(2),N(3));
            case "lengthdir_x":return Math.Cos(N(1)*Math.PI/180)*N(0);case "lengthdir_y":return -Math.Sin(N(1)*Math.PI/180)*N(0);
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
            case "variable_global_array_get":return Globals.Values.GetValueOrDefault(GmScope.Key(S(0),[I(1)]));case "variable_global_array2_get":return Globals.Values.GetValueOrDefault(GmScope.Key(S(0),[I(1),I(2)]));
            case "variable_local_array_get":return Self.Scope.Values.GetValueOrDefault(GmScope.Key(S(0),[I(1)]));case "variable_local_array2_get":return Self.Scope.Values.GetValueOrDefault(GmScope.Key(S(0),[I(1),I(2)]));
            case "instance_create":return Create(I(2),N(0),N(1)).Id;
            case "instance_destroy":Event(Self,1,0);Self.Destroyed=true;return 0;
            case "instance_exists":return Select(I(0),c).Any();case "instance_number":return Select(I(0),c).Count();
            case "instance_find":return Select(I(0),c).Skip(Math.Max(0,I(1))).FirstOrDefault()?.Id??-4;
            case "instance_nearest":return Select(I(2),c).OrderBy(i=>Math.Pow(i.Scope.ReadOr("x").Number-N(0),2)+Math.Pow(i.Scope.ReadOr("y").Number-N(1),2)).FirstOrDefault()?.Id??-4;
            case "instance_activate_object":foreach(var i in Instances.Where(i=>I(0)==-3||i.Id==I(0)||IsObject(i.Object,I(0))))i.Active=true;return 0;
            case "instance_deactivate_object":foreach(var i in Select(I(0),c).ToArray())i.Active=false;return 0;
            case "instance_activate_region":case "instance_deactivate_region":
            {
                var region=new GmBounds(N(0),N(1),N(0)+N(2),N(1)+N(3));
                foreach(var i in Instances.Where(i=>!i.Destroyed&&!(name=="instance_deactivate_region"&&N(5)>=0.5&&i==Self)))
                    if(Bounds(i).Overlaps(region)==(N(4)>=0.5))i.Active=name=="instance_activate_region";
                return 0;
            }
            case "instance_change":{if(N(1)>=0.5)Event(Self,1,0);Self.Object=I(0);if(N(1)>=0.5)Event(Self,0,0);return 0;}
            case "object_get_parent":return parents.GetValueOrDefault(I(0),-1);case "object_exists":return Objects.ContainsKey(I(0));
            case "event_user":Event(Self,7,10+I(0),c.Other);return 0;
            case "event_perform":Event(Self,I(0),I(1),c.Other);return 0;
            case "event_inherited":Event(Self,c.EventKind,c.EventSub,c.Other,Objects[c.EventObject].GetProperty("parent").GetInt32());return 0;
            case "room_exists":return Rooms.ContainsKey(I(0));case "room_get_name":return Rooms[I(0)].GetProperty("name").GetString()!;
            case "room_set_width":case "room_set_height":case "room_set_caption":
            {
                var node=JsonNode.Parse(Rooms[I(0)].GetRawText())!;
                string property=name=="room_set_width"?"width":name=="room_set_height"?"height":"caption";
                node[property]=name=="room_set_caption"?JsonValue.Create(S(1)):JsonValue.Create(I(1));
                Rooms[I(0)]=JsonSerializer.SerializeToElement(node);return 0;
            }
            case "room_set_view":
            {
                var node=JsonNode.Parse(Rooms[I(0)].GetRawText())!;
                var view=node["views"]![I(1)]!;view["visible"]=N(2)>=0.5;
                var properties=new[]{"x","y","width","height","portX","portY","portWidth","portHeight","hborder","vborder","hspeed","vspeed","target"};
                for(int p=0;p<properties.Length;p++)view[properties[p]]=I(p+3);
                Rooms[I(0)]=JsonSerializer.SerializeToElement(node);return 0;
            }
            case "room_next":{var ids=Manifest.GetProperty("roomOrder").EnumerateArray().Select(x=>x.GetInt32()).ToList();int i=ids.IndexOf(I(0));return i>=0&&i+1<ids.Count?ids[i+1]:-1;}
            case "room_goto":PendingRoom=I(0);return 0;case "room_restart":PendingRoom=CurrentRoom;return 0;
            case "room_goto_next":PendingRoom=Call("room_next",[CurrentRoom],c).Int;return 0;
            case "game_end":Ended=true;return 0;
            case "file_exists":{var path=S(0).Replace('\\','/');return path.Equals("bin/bGM/bGM.exe",StringComparison.OrdinalIgnoreCase)||File.Exists(Resolve(path))||External.ContainsKey(path)||Media.ContainsKey(path);}
            case "file_find_first":{var pattern=S(0).Replace('\\','/');var parent=Path.GetDirectoryName(pattern)??"";var dir=Resolve(parent);foundFiles=Directory.Exists(dir)?Directory.EnumerateFiles(dir,Path.GetFileName(pattern)).Order(StringComparer.Ordinal).Select(Path.GetFileName).Cast<string>().ToArray():[];foundIndex=0;return foundFiles.FirstOrDefault()??"";}
            case "file_find_next":return ++foundIndex<foundFiles.Length?foundFiles[foundIndex]:"";
            case "file_text_open_read":{int id=handle++;var path=S(0).Replace('\\','/');var saved=SavedFile(path);textReads[id]=(saved is not null?File.ReadAllLines(saved):Sources.TryGetValue(path,out var text)?text.Replace("\r","").Split('\n'):External.ContainsKey(path)?[]:File.ReadAllLines(Resolve(path)),0);return id;}
            case "file_text_eof":return textReads[I(0)].Line>=textReads[I(0)].Lines.Length;
            case "file_text_read_string":{var f=textReads[I(0)];return f.Line<f.Lines.Length?f.Lines[f.Line]:"";}
            case "file_text_readln":{var f=textReads[I(0)];textReads[I(0)]=(f.Lines,f.Line+1);return 0;}
            case "file_text_open_write":{int id=handle++;textWrites[id]=new StreamWriter(Resolve(S(0),true));return id;}
            case "file_text_write_string":textWrites[I(0)].Write(S(1));return 0;
            case "file_text_writeln":textWrites[I(0)].WriteLine();return 0;
            case "file_text_close":textReads.Remove(I(0));if(textWrites.Remove(I(0),out var writer))writer.Dispose();return 0;
            case "file_bin_open":{int id=handle++;binaries[id]=new FileStream(Resolve(S(0),I(1)!=0),I(1)==0?FileMode.Open:FileMode.OpenOrCreate,I(1)==0?FileAccess.Read:FileAccess.ReadWrite);return id;}
            case "file_bin_read_byte":return binaries[I(0)].ReadByte();
            case "file_bin_write_byte":binaries[I(0)].WriteByte((byte)I(1));return 0;
            case "file_bin_rewrite":binaries[I(0)].SetLength(0);return 0;
            case "file_bin_close":binaries[I(0)].Dispose();binaries.Remove(I(0));return 0;
            case "execute_file":{var path=S(0).Replace('\\','/');var saved=SavedFile(path);var compiled=saved is not null?Compile(File.ReadAllText(saved)):External.TryGetValue(path,out var external)?external:Compile(File.ReadAllText(Resolve(path)));return Evaluator.Run(compiled,c);}
            case "execute_string":return Evaluator.Run(Compile(S(0)),c);
            case "screen_redraw":Redraw?.Invoke();return 0;
            case "motion_set":Write(Self,"direction",V(0));Write(Self,"speed",V(1));return 0;
            case "motion_add":Write(Self,"hspeed",Self.Scope.ReadOr("hspeed").Number+Math.Cos(N(0)*Math.PI/180)*N(1));Write(Self,"vspeed",Self.Scope.ReadOr("vspeed").Number-Math.Sin(N(0)*Math.PI/180)*N(1));return 0;
            case "sprite_exists":return Sprites.ContainsKey(I(0));
            case "sprite_get_number":return Sprites.TryGetValue(I(0),out var sprite)?sprite.GetProperty("frames").GetArrayLength():0;
            case "sprite_get_width":case "sprite_get_height":{if(!Sprites.TryGetValue(I(0),out var s)||s.GetProperty("frames").GetArrayLength()==0)return 0;return s.GetProperty("frames")[0].GetProperty(name=="sprite_get_width"?"width":"height").GetInt32();}
            case "sprite_get_xoffset":case "sprite_get_yoffset":return Sprites[I(0)].GetProperty(name=="sprite_get_xoffset"?"originX":"originY").GetInt32();
            case "sprite_get_bbox_left":case "sprite_get_bbox_right":case "sprite_get_bbox_top":case "sprite_get_bbox_bottom":return Sprites[I(0)].GetProperty("masks")[0].GetProperty(name[16..]).GetInt32();
            case "background_get_width":case "background_get_height":{var b=Manifest.GetProperty("backgrounds").EnumerateArray().First(b=>b.GetProperty("id").GetInt32()==I(0));return b.GetProperty(name=="background_get_width"?"width":"height").GetInt32();}
        }
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

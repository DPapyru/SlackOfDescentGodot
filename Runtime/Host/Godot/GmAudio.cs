using Godot;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SlackOfDescent;

public sealed class GmAudio
{
    private readonly Node owner;
    private readonly GmWorld world;
    private readonly Dictionary<int,AudioStreamPlayer> players=new();
    private readonly Dictionary<int,string> functions=new();
    private readonly Dictionary<int,uint> types=new();
    private readonly Dictionary<int,double> volumes=new();
    private readonly Dictionary<int,bool> loops=new();
    private readonly Dictionary<int,double> groups=new();
    private readonly Dictionary<int,int> modes=new();
    private readonly Dictionary<int,Vector3> positions=new();
    private int next=10000;
    private int nextEmitter;
    private int nextSound=201;
    private readonly Dictionary<string,int> music=new();
    private readonly Dictionary<int,string> dynamicSounds=new();
    public GmAudio(Node owner,GmWorld world){this.owner=owner;this.world=world;}
    public int AuditResourceSounds()
    {
        var embedded=world.Manifest.GetProperty("sounds").EnumerateArray().Where(s=>s.GetProperty("present").GetBoolean()).Select(s=>Path.Combine(world.Root,s.GetProperty("path").GetString()!));
        var external=Directory.EnumerateFiles(world.SourceRoot,"*",SearchOption.AllDirectories).Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".wav" or ".mp3" or ".ogg");
        var paths=embedded.Concat(external).Distinct().ToArray();
        foreach(string path in paths)
        {
            using var stream=Load(path);
            if(stream is null||stream.GetLength()<=0)throw new InvalidDataException("Packaged sound failed to decode: "+path);
        }
        return paths.Length;
    }
    public void Shutdown()
    {
        loops.Clear();
        var streams=new HashSet<AudioStream>();
        foreach(var player in players.Values)
        {
            if(!GodotObject.IsInstanceValid(player))continue;
            player.Stop();
            if(player.Stream is { } stream)streams.Add(stream);
            player.Stream=null;
        }
        foreach(var stream in streams)stream.Dispose();
        players.Clear();
    }
    private AudioStream Load(string path)
    {
        var bytes=File.ReadAllBytes(path);
        if(Path.GetExtension(path).Equals(".wav",StringComparison.OrdinalIgnoreCase))
        {
            using var reader=new BinaryReader(new MemoryStream(bytes));
            if(new string(reader.ReadChars(4))!="RIFF")throw new InvalidDataException("Invalid WAV "+path);
            reader.ReadUInt32();
            if(new string(reader.ReadChars(4))!="WAVE")throw new InvalidDataException("Invalid WAV "+path);
            ushort format=0,channels=0,bits=0;int rate=0;byte[]? samples=null;
            while(reader.BaseStream.Position+8<=bytes.Length)
            {
                var chunk=new string(reader.ReadChars(4));uint length=reader.ReadUInt32();
                long end=reader.BaseStream.Position+length;
                if(chunk=="fmt "&&length>=16){format=reader.ReadUInt16();channels=reader.ReadUInt16();rate=reader.ReadInt32();reader.ReadUInt32();reader.ReadUInt16();bits=reader.ReadUInt16();}
                if(chunk=="data")samples=reader.ReadBytes((int)Math.Min(length,bytes.Length-reader.BaseStream.Position));
                reader.BaseStream.Position=Math.Min(bytes.Length,end+(length%2));
            }
            if(format==1&&samples is not null&&bits is 8 or 16&&channels is 1 or 2)
            {
                if(bits==8)for(int i=0;i<samples.Length;i++)samples[i]^=128;
                return new AudioStreamWav{Data=samples,MixRate=rate,Stereo=channels==2,Format=bits==8?AudioStreamWav.FormatEnum.Format8Bits:AudioStreamWav.FormatEnum.Format16Bits};
            }
        }
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav"=>AudioStreamWav.LoadFromBuffer(bytes),
            ".mp3"=>new AudioStreamMP3{Data=bytes},
            ".ogg"=>AudioStreamOggVorbis.LoadFromBuffer(bytes),
            _=>throw new NotSupportedException("Audio format "+path)
        };
    }
    private int Create(string path,int? requested=null)
    {
        var p=new AudioStreamPlayer{Stream=Load(world.Resolve(path))};
        owner.AddChild(p);int id=requested??nextEmitter++;players[id]=p;volumes[id]=1;types[id]=0;
        if(requested is null)world.Globals.Write("Sounds",0,[id]);
        p.Finished+=()=>{if(loops.GetValueOrDefault(id))p.Play();};
        return id;
    }
    private void Volume(int id)
    {
        if(!players.TryGetValue(id,out var p))return;
        double volume=volumes.GetValueOrDefault(id,1);
        uint flags=types.GetValueOrDefault(id);
        for(int g=0;g<32;g++)if((flags&(1u<<g))!=0)volume*=groups.GetValueOrDefault(g,1);
        p.VolumeDb=volume<=0?-80:Mathf.LinearToDb((float)volume);
    }
    public GmValue? Call(string name,GmValue[] a)
    {
        int I(int i)=>i<a.Length?a[i].Int:0;
        double N(int i)=>i<a.Length?a[i].Number:0;
        string S(int i)=>i<a.Length?a[i].ToString():"";
        if(name=="sound_add")
        {
            int id=nextSound++;Create(S(0),100000+id);dynamicSounds[id]=S(0);return id;
        }
        if(name=="sound_delete"){if(players.Remove(100000+I(0),out var old))old.QueueFree();dynamicSounds.Remove(I(0));return 0;}
        if(name=="execute_program")
        {
            if(!S(0).Replace('\\','/').EndsWith("/bGM.exe",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("External program "+S(0));
            var tokens=Regex.Matches(S(1),"\"([^\"]*)\"|([^\\s]+)").Select(m=>m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value).ToArray();
            var options=new Dictionary<string,string>();
            for(int t=0;t<tokens.Length;t++)
            {
                string flag=tokens[t];
                if(flag is "-L" or "-e" or "-m" or "-o" or "-d" or "-r" or "-u")options[flag]="";
                else if(t+1<tokens.Length)options[flag]=tokens[++t];
            }
            if(options.TryGetValue("-f",out var file))
            {
                if(!File.Exists(world.Resolve(file))){GD.Print("AUDIO_MISSING ",file);return -1;}
                int id=Create(file);music[options["-i"]]=id;loops[id]=options.ContainsKey("-L");
                if(options.TryGetValue("-v",out var volume))volumes[id]=double.Parse(volume,CultureInfo.InvariantCulture);
                if(options.TryGetValue("-s",out var pitch))players[id].PitchScale=float.Parse(pitch,CultureInfo.InvariantCulture);
                Volume(id);players[id].Play();
            }
            else if(options.TryGetValue("-t",out var token)&&music.TryGetValue(token,out var id))
            {
                var player=players[id];
                if(options.ContainsKey("-m"))player.Stop();
                if(options.ContainsKey("-o"))player.StreamPaused=!player.StreamPaused;
                if(options.ContainsKey("-r"))player.Seek(0);
                if(options.ContainsKey("-d")){player.QueueFree();players.Remove(id);music.Remove(token);}
                if(options.ContainsKey("-L"))loops[id]=true;
                if(options.ContainsKey("-u"))loops[id]=false;
                if(options.TryGetValue("-v",out var volume)){volumes[id]=double.Parse(volume,CultureInfo.InvariantCulture);Volume(id);}
                if(options.TryGetValue("-s",out var pitch))player.PitchScale=float.Parse(pitch,CultureInfo.InvariantCulture);
            }
            return 0;
        }
        if(name=="external_define")
        {
            if(!string.Equals(Path.GetFileName(S(0)),"SGAudio.dll",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Native extension "+S(0));
            int id=next++;functions[id]=S(1);return id;
        }
        if(name=="external_free")return 0;
        if(name=="external_call")
        {
            var function=functions[I(0)];int id=I(1);
            switch(function)
            {
                case "sga_Init":return 1;
                case "sga_Free":foreach(var p in players.Values)p.Stop();return 0;
                case "sga_CreateEmitter":return Create(S(1));
                case "sga_DestroyEmitter":if(players.Remove(id,out var old))old.QueueFree();return 0;
                case "sga_Play":if(players.TryGetValue(id,out var player))player.Play();return 0;
                case "sga_Stop":case "sga_Rewind":if(players.TryGetValue(id,out var stop))stop.Stop();return 0;
                case "sga_Pause":if(players.TryGetValue(id,out var pause))pause.StreamPaused=true;return 0;
                case "sga_IsPlaying":return players.TryGetValue(id,out var active)&&active.Playing;
                case "sga_SetVolume":volumes[id]=N(2);Volume(id);return 0;
                case "sga_GetVolume":return volumes.GetValueOrDefault(id,1);
                case "sga_SetGroupVolume":groups[id]=N(2);foreach(int key in players.Keys)Volume(key);return 0;
                case "sga_GetGroupVolume":return groups.GetValueOrDefault(id,1);
                case "sga_SetType":types[id]=(uint)N(2);Volume(id);return 0;
                case "sga_GetType":return types.GetValueOrDefault(id);
                case "sga_Set3DMode":modes[id]=I(2);if(I(2)==0)positions[id]=Vector3.Zero;return 0;
                case "sga_SetPosition":positions[id]=new((float)N(2),(float)N(3),(float)N(4));return 0;
                case "sga_SetLooping":loops[id]=N(2)>=0.5;return 0;
                case "sga_TrackPlay":
                    if(players.Remove(id,out var previous))previous.QueueFree();
                    id=Create(S(2));loops[id]=N(3)>=0.5;types[id]=(uint)N(4);Volume(id);players[id].Play();return id;
                default:throw new NotSupportedException("SGAudio function "+function);
            }
        }
        if(name is "sound_play" or "sound_loop" or "sound_stop" or "sound_volume" or "sound_isplaying" or "sound_exists" or "sound_get_name")
        {
            int id=I(0);
            var asset=world.Manifest.GetProperty("sounds").EnumerateArray().FirstOrDefault(s=>s.GetProperty("id").GetInt32()==id);
            if(name=="sound_exists")return dynamicSounds.ContainsKey(id)||asset.ValueKind!=System.Text.Json.JsonValueKind.Undefined;
            if(name=="sound_get_name")return dynamicSounds.TryGetValue(id,out var dynamicName)?dynamicName:asset.GetProperty("name").GetString()!;
            id+=100000;
            if(name=="sound_isplaying")return players.TryGetValue(id,out var existing)&&existing.Playing;
            if(name=="sound_stop"){if(players.TryGetValue(id,out var p))p.Stop();return 0;}
            if(name=="sound_volume"){volumes[id]=N(1);Volume(id);return 0;}
            if(!players.ContainsKey(id))
            {
                var p=new AudioStreamPlayer{Stream=Load(Path.Combine(world.Root,asset.GetProperty("path").GetString()!))};owner.AddChild(p);players[id]=p;
                volumes.TryAdd(id,asset.GetProperty("volume").GetDouble());
                p.Finished+=()=>{if(loops.GetValueOrDefault(id))p.Play();};
            }
            loops[id]=name=="sound_loop";Volume(id);players[id].Play();return 0;
        }
        return null;
    }
}

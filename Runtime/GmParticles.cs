namespace SlackOfDescent;

public sealed class GmParticleType
{
    public int Shape,Sprite=-1,LifeMin=100,LifeMax=100,StepType,StepNumber,DeathType,DeathNumber;
    public bool Animated,Stretched,RandomFrame,RelativeAngle,Additive;
    public double SizeMin=1,SizeMax=1,SizeIncrement,SizeWiggle,XScale=1,YScale=1;
    public double SpeedMin,SpeedMax,SpeedIncrement,SpeedWiggle,DirectionMin,DirectionMax,DirectionIncrement,DirectionWiggle;
    public double AngleMin,AngleMax,AngleIncrement,AngleWiggle,Gravity,GravityDirection=270;
    public int ColourMode,Colour1=0xffffff,Colour2=0xffffff,Colour3=0xffffff;
    public double Alpha1=1,Alpha2=1,Alpha3=1;
}

public sealed class GmParticle
{
    public int Type,Timer,Lifetime,Colour,Subimage,RandomStart;
    public double X,Y,Speed,Direction,Angle,Alpha,Size;
}

public sealed class GmParticleEmitter
{
    public double Left,Right,Top,Bottom;
    public int Shape,Distribution,Type=-1,Number;
}

public sealed class GmParticleSystem
{
    public readonly List<GmParticle> Particles=new();
    public readonly Dictionary<int,GmParticleEmitter> Emitters=new();
    public bool OldToNew=true,AutoUpdate=true,AutoDraw=true;
    public double Depth,X,Y;
}

public sealed class GmParticles
{
    public readonly Dictionary<int,GmParticleType> Types=new();
    public readonly Dictionary<int,GmParticleSystem> Systems=new();
    private int above=-1,below=-1;
    private int[][]? effectTypes;

    private static int Add<T>(Dictionary<int,T> entries,T value)
    {
        int id=0;while(entries.ContainsKey(id))id++;
        entries[id]=value;return id;
    }
    public int CreateType()=>Add(Types,new GmParticleType());
    public int CreateSystem()=>Add(Systems,new GmParticleSystem());
    public static int Mix(int first,int second,double amount)
    {
        int Channel(int shift)=>(int)Math.Round(((first>>shift)&255)*(1-amount)+((second>>shift)&255)*amount,MidpointRounding.ToEven);
        return Channel(0)|(Channel(8)<<8)|(Channel(16)<<16);
    }
    public static double Wiggle(int timer,int random,int multiplier,int period)
    {
        double factor=(timer+random*multiplier)%period/(period/4.0);
        return (factor>2?4-factor:factor)-1;
    }
    private static double Range(GmRandom random,double min,double max,int distribution=0)
    {
        if(min>=max)return min;
        if(distribution==0)return min+random.Next(max-min);
        double value;
        do{value=random.Next(6)-3;}while(Math.Exp(-value*value*0.5)<=random.Next(1));
        if(distribution==2){if(value<0)value+=6;return min+(max-min)*value/6;}
        return min+(max-min)*(value+3)/6;
    }
    public void Create(GmParticleSystem system,double x,double y,int type,int count,GmRandom random,int? colour=null)
    {
        if(!Types.TryGetValue(type,out var t))return;
        for(int i=0;i<count;i++)
        {
            var p=new GmParticle{Type=type,X=x,Y=y,Speed=Range(random,t.SpeedMin,t.SpeedMax),Direction=Range(random,t.DirectionMin,t.DirectionMax),Angle=Range(random,t.AngleMin,t.AngleMax),Lifetime=(int)Math.Round(Range(random,t.LifeMin,t.LifeMax),MidpointRounding.ToEven)};
            int initial=t.ColourMode==4?Mix(t.Colour1,t.Colour2,random.Next(1)):t.Colour1;
            p.Colour=colour??initial;p.Alpha=t.Alpha1;p.Size=Range(random,t.SizeMin,t.SizeMax);
            if(t.Sprite>=0&&t.RandomFrame)p.Subimage=random.NextInt(10000);
            p.RandomStart=random.NextInt(100000);system.Particles.Add(p);
        }
    }
    public void Burst(GmParticleSystem system,GmParticleEmitter emitter,int type,int number,GmRandom random)
    {
        if(number<0){if(random.NextInt(unchecked((uint)-number))!=0)return;number=1;}
        for(int i=0;i<number;i++)
        {
            double x,y;
            while(true)
            {
                x=Range(random,0,1,emitter.Distribution);y=Range(random,0,1,emitter.Distribution);
                if(emitter.Distribution==2&&emitter.Shape!=3){if(random.Next(1)>=0.5)y=random.Next(1);else x=random.Next(1);}
                if(emitter.Shape==1&&Math.Sqrt(Math.Pow(x*2-1,2)+Math.Pow(y*2-1,2))>1)continue;
                if(emitter.Shape==2&&(x*2-1)+(y*2-1)>1)continue;
                if(emitter.Shape==3)y=x;
                break;
            }
            Create(system,emitter.Left+(emitter.Right-emitter.Left)*x,emitter.Top+(emitter.Bottom-emitter.Top)*y,type,1,random);
        }
    }
    public void Update(GmRandom random)
    {
        foreach(var system in Systems.Values)if(system.AutoUpdate)Update(system,random);
    }
    public void Update(GmParticleSystem system,GmRandom random)
    {
        int initial=system.Particles.Count;
        for(int i=0;i<initial;i++)
        {
            var p=system.Particles[i];p.Timer++;
            if(!Types.TryGetValue(p.Type,out var t))continue;
            int count=p.Timer>=p.Lifetime?t.DeathNumber:t.StepNumber;
            if(count<0&&random.NextInt(unchecked((uint)-count))==0)count=1;
            Create(system,p.X,p.Y,p.Timer>=p.Lifetime?t.DeathType:t.StepType,count,random);
        }
        system.Particles.RemoveAll(p=>p.Timer>=p.Lifetime);
        foreach(var p in system.Particles)
        {
            if(!Types.TryGetValue(p.Type,out var t))continue;
            p.Speed=Math.Max(0,p.Speed+t.SpeedIncrement);p.Direction+=t.DirectionIncrement;p.Angle+=t.AngleIncrement;
            if(t.Gravity!=0)
            {
                double h=Math.Cos(p.Direction*Math.PI/180)*p.Speed+Math.Cos(t.GravityDirection*Math.PI/180)*t.Gravity;
                double v=-Math.Sin(p.Direction*Math.PI/180)*p.Speed-Math.Sin(t.GravityDirection*Math.PI/180)*t.Gravity;
                p.Speed=Math.Sqrt(h*h+v*v);p.Direction=-Math.Atan2(v,h)*180/Math.PI;
            }
            double direction=p.Direction+t.DirectionWiggle*Wiggle(p.Timer,p.RandomStart,3,24);
            double speed=p.Speed+t.SpeedWiggle*Wiggle(p.Timer,p.RandomStart,4,20);
            p.X+=Math.Cos(direction*Math.PI/180)*speed;p.Y-=Math.Sin(direction*Math.PI/180)*speed;
            p.Size=Math.Max(0,p.Size+t.SizeIncrement);
            double half=p.Lifetime>=1?p.Timer*2.0/p.Lifetime:1;
            if(t.ColourMode==2)p.Colour=Mix(t.Colour1,t.Colour2,half/2);
            if(t.ColourMode==3)p.Colour=half<1?Mix(t.Colour1,t.Colour2,half):Mix(t.Colour2,t.Colour3,half-1);
            p.Alpha=half>=1?(half-1)*t.Alpha3+(2-half)*t.Alpha2:half*t.Alpha2+(1-half)*t.Alpha1;
        }
        foreach(var emitter in system.Emitters.Values)if(emitter.Number!=0)Burst(system,emitter,emitter.Type,emitter.Number,random);
    }
    public void ClearEffects()
    {
        if(Systems.TryGetValue(below,out var b))b.Particles.Clear();
        if(Systems.TryGetValue(above,out var a))a.Particles.Clear();
    }
    public void Effect(int kind,double x,double y,int size,int colour,bool isBelow,double fps,int width,int height,GmRandom random)
    {
        if(kind<0||kind>11)return;
        if(effectTypes is null)
        {
            below=CreateSystem();above=CreateSystem();Systems[below].Depth=100000;Systems[above].Depth=-100000;
            effectTypes=Enumerable.Range(0,12).Select(k=>Enumerable.Range(0,k==0?6:k<10?3:1).Select(_=>CreateType()).ToArray()).ToArray();
        }
        int systemId=isBelow?below:above;
        if(!Systems.TryGetValue(systemId,out var system))return;
        size=size==0?0:size==2?2:1;
        int id=effectTypes[kind][kind==0?size*2:kind<10?size:0];
        if(!Types.TryGetValue(id,out var t))return;
        int Life(double n)=>(int)Math.Round(n/fps,MidpointRounding.ToEven);
        void Spawn(int number=1,int? tint=null)=>Create(system,x,y,id,number,random,tint??colour);
        switch(kind)
        {
            case 0:
                t.Shape=10;t.AngleMax=360;t.DirectionMax=360;t.Alpha1=0.6;t.Alpha2=0.3;t.Alpha3=0;
                t.SizeMin=t.SizeMax=new[]{0.1,0.3,0.4}[size];t.SizeIncrement=new[]{0.05,0.1,0.2}[size]*fps;
                t.SpeedMin=t.SpeedMax=new[]{2.0,4,7}[size]*fps;t.SpeedIncrement=new[]{-0.1,-0.18,-0.2}[size]*fps;
                t.LifeMin=Life(new[]{10,12,15}[size]);t.LifeMax=Life(new[]{15,17,20}[size]);Spawn(20);
                int secondary=effectTypes[0][size*2+1];
                if(Types.TryGetValue(secondary,out var second))
                {
                    second.Shape=10;second.AngleMax=360;second.DirectionMax=360;second.Alpha1=0.8;second.Alpha2=0.4;second.Alpha3=0;
                    second.SizeMin=second.SizeMax=t.SizeMin;second.SizeIncrement=new[]{0.1,0.2,0.4}[size]*fps;
                    second.LifeMin=second.LifeMax=Life(new[]{15,17,20}[size]);Create(system,x,y,secondary,1,random,0);
                }
                break;
            case 1:case 2:
                t.Shape=6;t.Alpha1=1;t.Alpha2=0.5;t.Alpha3=0;t.SizeMin=t.SizeMax=0;
                t.YScale=kind==2?0.5:1;t.SizeIncrement=(kind==2?new[]{0.2,0.35,0.6}:new[]{0.15,0.25,0.4})[size]*fps;
                t.LifeMin=Life(new[]{10,13,18}[size]);t.LifeMax=Life(new[]{12,15,20}[size]);Spawn();break;
            case 3:
                t.Shape=8;t.SizeMin=0.1;t.SizeMax=0.2;t.SpeedMin=0.5*fps;t.SpeedMax=new[]{3.0,6,8}[size]*fps;
                t.DirectionMax=360;t.Alpha1=1;t.Alpha2=0.7;t.Alpha3=0.4;t.Gravity=new[]{0.1,0.15,0.17}[size];
                t.LifeMin=Life(new[]{15,20,30}[size]);t.LifeMax=Life(new[]{25,30,40}[size]);Spawn(new[]{75,150,250}[size]);break;
            case 4:case 5:
                t.Shape=10;t.SizeIncrement=-0.01*fps;t.Alpha1=0.4;t.Alpha2=0.2;t.Alpha3=0;
                t.SizeMin=new[]{0.2,0.4,0.4}[size];t.SizeMax=new[]{0.4,0.7,1}[size];
                t.LifeMin=t.LifeMax=Life(new[]{25,30,50}[size]);
                if(kind==5){t.DirectionMin=t.DirectionMax=90;t.SpeedMin=new[]{3.0,5,6}[size]*fps;t.SpeedMax=t.SpeedMin+fps;}
                for(int i=0;i<new[]{6,11,16}[size];i++)
                {
                    int dx=random.NextInt((uint)new[]{9,29,59}[size])-new[]{5,15,30}[size];
                    int dy=random.NextInt((uint)new[]{9,29,59}[size])-new[]{5,15,30}[size];
                    Create(system,x+dx,y+dy,id,1,random,colour);
                }
                break;
            case 6:case 7:case 8:
                t.Shape=kind==6?4:kind==7?9:8;t.AngleMax=360;
                t.SizeMin=new[]{0.4,0.75,1.2}[size];t.SizeMax=kind==6&&size==0?0.3:t.SizeMin;
                t.SizeIncrement=new[]{-0.02,-0.03,-0.04}[size]*(kind==6?fps:1/fps);
                t.LifeMin=t.LifeMax=Life(new[]{20,25,30}[size]);Spawn();break;
            case 9:
                t.Shape=10;t.YScale=0.5;t.Alpha1=0;t.Alpha2=0.3;t.Alpha3=0;t.LifeMin=t.LifeMax=Life(100);
                t.SizeMin=t.SizeMax=new[]{2.0,4,8}[size];Spawn();break;
            case 10:case 11:
                t.Shape=kind==10?3:13;t.SizeMin=kind==10?0.2:0.1;t.SizeMax=kind==10?0.3:0.25;
                t.Alpha1=t.Alpha2=t.Alpha3=kind==10?0.4:0.6;t.RelativeAngle=kind==10;t.AngleMax=kind==10?0:360;
                t.SpeedMin=(kind==10?7:2.5)*fps;t.SpeedMax=(kind==10?7:3)*fps;
                t.DirectionMin=kind==10?260:240;t.DirectionMax=kind==10?260:300;t.DirectionWiggle=kind==10?0:20;
                t.LifeMin=t.LifeMax=Life((kind==10?0.2:0.5)*height);
                for(int i=0;i<(kind==10?new[]{2,5,9}:new[]{1,3,7})[size];i++)Create(system,random.Next(1.2)*width-(kind==10?0:60),random.NextInt(19)-30,id,1,random,colour);
                break;
        }
    }
}

public sealed partial class GmWorld
{
    public readonly GmParticles Particles=new();
    private GmValue? ParticleCall(string name,GmValue[] a,GmContext context)
    {
        int I(int i)=>i<a.Length?a[i].Int:0;
        double N(int i)=>i<a.Length?a[i].Number:0;
        if(name=="action_effect")
        {
            bool relative=context.Locals.ReadOr("argument_relative").Truth;
            return ParticleCall(N(5)>=0.5?"effect_create_above":"effect_create_below",[a[0],N(1)+(relative?context.Self.Scope.ReadOr("x").Number:0),N(2)+(relative?context.Self.Scope.ReadOr("y").Number:0),a[3],a[4]],context);
        }
        if(name is "effect_create_above" or "effect_create_below")
        {
            Particles.Effect(I(0),N(1),N(2),I(3),I(4),name=="effect_create_below",Math.Max(30/Engine.Read("room_speed").Number,1),Engine.Read("room_width").Int,Engine.Read("room_height").Int,Random);return 0;
        }
        if(name=="effect_clear"){Particles.ClearEffects();return 0;}
        if(name=="part_type_create")return Particles.CreateType();
        if(name=="part_system_create")return Particles.CreateSystem();
        if(name=="part_type_destroy"){Particles.Types.Remove(I(0));return 0;}
        if(name=="part_system_destroy"){Particles.Systems.Remove(I(0));return 0;}
        if(name=="part_type_exists")return Particles.Types.ContainsKey(I(0));
        if(name=="part_system_exists")return Particles.Systems.ContainsKey(I(0));
        if(name.StartsWith("part_type_",StringComparison.Ordinal))
        {
            if(!Particles.Types.TryGetValue(I(0),out var t))return 0;
            switch(name)
            {
                case "part_type_clear":Particles.Types[I(0)]=new();break;
                case "part_type_shape":t.Shape=I(1);t.Sprite=-1;break;
                case "part_type_sprite":t.Sprite=I(1);t.Animated=N(2)>=0.5;t.Stretched=N(3)>=0.5;t.RandomFrame=N(4)>=0.5;break;
                case "part_type_size":t.SizeMin=N(1);t.SizeMax=N(2);t.SizeIncrement=N(3);t.SizeWiggle=N(4);break;
                case "part_type_scale":t.XScale=N(1);t.YScale=N(2);break;
                case "part_type_life":t.LifeMin=I(1);t.LifeMax=I(2);break;
                case "part_type_step":t.StepNumber=I(1);t.StepType=I(2);break;
                case "part_type_death":t.DeathNumber=I(1);t.DeathType=I(2);break;
                case "part_type_speed":t.SpeedMin=N(1);t.SpeedMax=N(2);t.SpeedIncrement=N(3);t.SpeedWiggle=N(4);break;
                case "part_type_direction":t.DirectionMin=N(1);t.DirectionMax=N(2);t.DirectionIncrement=N(3);t.DirectionWiggle=N(4);break;
                case "part_type_orientation":t.AngleMin=N(1);t.AngleMax=N(2);t.AngleIncrement=N(3);t.AngleWiggle=N(4);t.RelativeAngle=N(5)>=0.5;break;
                case "part_type_gravity":t.Gravity=N(1);t.GravityDirection=(N(2)%360+360)%360;break;
                case "part_type_color1":case "part_type_color2":case "part_type_color3":case "part_type_color_mix":t.ColourMode=name=="part_type_color_mix"?4:name[^1]-'0';t.Colour1=I(1);t.Colour2=I(2);t.Colour3=I(3);break;
                case "part_type_alpha1":t.Alpha1=t.Alpha2=t.Alpha3=N(1);break;
                case "part_type_alpha2":t.Alpha1=N(1);t.Alpha2=(N(1)+N(2))/2;t.Alpha3=N(2);break;
                case "part_type_alpha3":t.Alpha1=N(1);t.Alpha2=N(2);t.Alpha3=N(3);break;
                case "part_type_blend":t.Additive=N(1)>=0.5;break;
                default:return null;
            }
            return 0;
        }
        if(!name.StartsWith("part_",StringComparison.Ordinal))return null;
        if(!Particles.Systems.TryGetValue(I(0),out var s))return name=="part_emitter_create"?-1:0;
        switch(name)
        {
            case "part_system_clear":Particles.Systems[I(0)]=new();return 0;
            case "part_system_depth":s.Depth=N(1);return 0;
            case "part_system_position":s.X=N(1);s.Y=N(2);return 0;
            case "part_system_draw_order":s.OldToNew=N(1)>=0.5;return 0;
            case "part_system_automatic_update":s.AutoUpdate=N(1)>=0.5;return 0;
            case "part_system_automatic_draw":s.AutoDraw=N(1)>=0.5;return 0;
            case "part_system_update":Particles.Update(s,Random);return 0;
            case "part_system_drawit":return Platform?.Invoke(name,a,context);
            case "part_particles_create":Particles.Create(s,N(1),N(2),I(3),I(4),Random);return 0;
            case "part_particles_create_color":Particles.Create(s,N(1),N(2),I(3),I(5),Random,I(4));return 0;
            case "part_particles_clear":s.Particles.Clear();return 0;
            case "part_particles_count":return s.Particles.Count;
            case "part_emitter_create":{int id=0;while(s.Emitters.ContainsKey(id))id++;s.Emitters[id]=new();return id;}
            case "part_emitter_destroy":s.Emitters.Remove(I(1));return 0;
            case "part_emitter_destroy_all":s.Emitters.Clear();return 0;
            case "part_emitter_exists":return s.Emitters.ContainsKey(I(1));
        }
        if(!s.Emitters.TryGetValue(I(1),out var e))return 0;
        switch(name)
        {
            case "part_emitter_clear":s.Emitters[I(1)]=new();return 0;
            case "part_emitter_region":e.Left=N(2);e.Right=N(3);e.Top=N(4);e.Bottom=N(5);e.Shape=I(6);e.Distribution=I(7);return 0;
            case "part_emitter_stream":e.Type=I(2);e.Number=I(3);return 0;
            case "part_emitter_burst":Particles.Burst(s,e,I(2),I(3),Random);return 0;
        }
        return null;
    }
}

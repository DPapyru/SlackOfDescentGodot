namespace SlackOfDescent;

public sealed class GmInput
{
    private readonly HashSet<int> held=new();
    private readonly HashSet<int> physicalHeld=new();
    private readonly HashSet<int> pressed=new();
    private readonly HashSet<int> released=new();
    private readonly HashSet<int> mouseHeld=new();
    private readonly HashSet<int> physicalMouseHeld=new();
    private readonly Queue<(bool Mouse,int Code,bool Down)> pending=new();
    private readonly HashSet<int> mousePressed=new();
    private readonly HashSet<int> mouseReleased=new();
    private double previousX;
    private double previousY;
    public int Wheel;
    public void Key(GmWorld world,int code,bool down)
    {
        if(down){held.Add(code);if(physicalHeld.Add(code))pressed.Add(code);world.Engine.Write("keyboard_key",code);world.Engine.Write("keyboard_lastkey",code);}
        else{held.Remove(code);if(physicalHeld.Remove(code))released.Add(code);if(world.Engine.ReadOr("keyboard_key").Int==code)world.Engine.Write("keyboard_key",0);}
    }
    public void Mouse(GmWorld world,int code,bool down)
    {
        if(code is <1 or >3)return;
        if(down){mouseHeld.Add(code);if(physicalMouseHeld.Add(code))mousePressed.Add(code);world.Engine.Write("mouse_button",code);world.Engine.Write("mouse_lastbutton",code);}
        else{mouseHeld.Remove(code);if(physicalMouseHeld.Remove(code))mouseReleased.Add(code);world.Engine.Write("mouse_button",0);}
    }
    private static bool Check(HashSet<int> state,int code,int any)=>code==any?state.Count>0:code==0?state.Count==0:state.Contains(code);
    public GmValue? Call(string name,GmValue[] args,GmWorld world)
    {
        int code=args.Length>0?args[0].Int:0;
        switch(name)
        {
            case "keyboard_check":return Check(held,code,1);
            case "keyboard_check_pressed":return Check(pressed,code,1);
            case "keyboard_check_released":return Check(released,code,1);
            case "mouse_check_button":return Check(mouseHeld,code,-1);
            case "mouse_check_button_pressed":return Check(mousePressed,code,-1);
            case "mouse_check_button_released":return Check(mouseReleased,code,-1);
            case "mouse_wheel_up":return Wheel>0;
            case "mouse_wheel_down":return Wheel<0;
            case "keyboard_clear":held.Remove(code);pressed.Remove(code);released.Remove(code);return 0;
            case "mouse_clear":mouseHeld.Remove(code);mousePressed.Remove(code);mouseReleased.Remove(code);return 0;
            case "io_clear":
                held.Clear();mouseHeld.Clear();EndStep();
                foreach(var variable in new[]{"keyboard_key","keyboard_lastkey","mouse_button","mouse_lastbutton"})world.Engine.Write(variable,0);
                return 0;
        }
        return null;
    }
    public void EndStep(){pressed.Clear();released.Clear();mousePressed.Clear();mouseReleased.Clear();Wheel=0;}
    public void QueueKey(int code,bool down)=>pending.Enqueue((false,code,down));
    public void QueueMouse(int code,bool down)=>pending.Enqueue((true,code,down));
    public void Poll(GmWorld world)
    {
        held.UnionWith(physicalHeld);mouseHeld.UnionWith(physicalMouseHeld);
        while(pending.TryDequeue(out var input))if(input.Mouse)Mouse(world,input.Code,input.Down);else Key(world,input.Code,input.Down);
    }
    public void Clear(GmWorld world)
    {
        physicalHeld.Clear();physicalMouseHeld.Clear();pending.Clear();Call("io_clear",[],world);
    }
    public void Dispatch(GmWorld world)
        =>DispatchAsync(world).GetAwaiter().GetResult();
    public async ValueTask DispatchAsync(GmWorld world)
    {
        async ValueTask All(int kind,int sub,Func<GmInstance,bool>? filter=null)
        {
            foreach(var instance in world.EventInstances(kind,sub))
                if(!instance.Destroyed&&instance.Active&&(filter is null||filter(instance)))await world.EventAsync(instance,kind,sub);
        }
        async ValueTask Keys(HashSet<int> state,int kind)
        {
            foreach(int code in state.Order().ToArray())await All(kind,code);
            await All(kind,state.Count>0?1:0);
        }
        await Keys(held,5);await Keys(pressed,9);await Keys(released,10);
        double x=world.Engine.ReadOr("mouse_x").Number,y=world.Engine.ReadOr("mouse_y").Number;
        bool Over(GmInstance instance)=>world.MouseOver(instance,x,y);
        for(int button=1;button<=3;button++)if(mouseHeld.Contains(button))await All(6,button-1,Over);
        if(mouseHeld.Count==0)await All(6,3,Over);
        for(int button=1;button<=3;button++)if(mousePressed.Contains(button))await All(6,button+3,Over);
        for(int button=1;button<=3;button++)if(mouseReleased.Contains(button))await All(6,button+6,Over);
        await All(6,10,i=>Over(i)&&!world.MouseOver(i,previousX,previousY));
        await All(6,11,i=>!Over(i)&&world.MouseOver(i,previousX,previousY));
        for(int button=1;button<=3;button++)if(mouseHeld.Contains(button))await All(6,button+49);
        for(int button=1;button<=3;button++)if(mousePressed.Contains(button)){await All(6,button+52);await All(6,12);}
        for(int button=1;button<=3;button++)if(mouseReleased.Contains(button)){await All(6,button+55);await All(6,13);}
        if(Wheel>0)await All(6,60);
        if(Wheel<0)await All(6,61);
        previousX=x;previousY=y;
    }
}

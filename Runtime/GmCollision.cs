using System.Text.Json;

namespace SlackOfDescent;

public readonly record struct GmBounds(double Left,double Top,double Right,double Bottom)
{
    public bool Empty=>Right<Left||Bottom<Top;
    public bool Overlaps(GmBounds b)=>!Empty&&!b.Empty&&Left<=b.Right&&Right>=b.Left&&Top<=b.Bottom&&Bottom>=b.Top;
}

public sealed partial class GmWorld
{
    public bool MouseOver(GmInstance instance,double x,double y)=>Bounds(instance).Overlaps(new(x,y,x,y))&&Opaque(instance,x,y);

    private void BoundaryEvents()
    {
        void Dispatch(int sub,double left,double top,double right,double bottom,bool intersect)
        {
            foreach(var instance in Instances.ToArray())
            {
                if(instance.Destroyed||!instance.Active||!HasEvent(instance.Object,7,sub))continue;
                bool hit;
                if(Mask(instance,out _,out _))
                {
                    var bounds=Bounds(instance);
                    hit=intersect?bounds.Left<left||bounds.Top<top||bounds.Right>right||bounds.Bottom>bottom:
                        bounds.Right<left||bounds.Bottom<top||bounds.Left>right||bounds.Top>bottom;
                }
                else
                {
                    double x=instance.Scope.ReadOr("x").Number,y=instance.Scope.ReadOr("y").Number;
                    hit=Math.Floor(x)<left||Math.Floor(y)<top||Math.Ceiling(x)>right||Math.Ceiling(y)>bottom;
                }
                if(hit)Event(instance,7,sub);
            }
        }
        Dispatch(0,0,0,Engine.Read("room_width").Number,Engine.Read("room_height").Number,false);
        Dispatch(1,0,0,Engine.Read("room_width").Number,Engine.Read("room_height").Number,true);
        for(int kind=0;kind<2;kind++)for(int view=0;view<8;view++)
        {
            double x=Engine.Read("view_xview",[view]).Number,y=Engine.Read("view_yview",[view]).Number;
            Dispatch(40+kind*10+view,x,y,x+Engine.Read("view_wview",[view]).Number,y+Engine.Read("view_hview",[view]).Number,kind==1);
        }
    }

    private bool Mask(GmInstance i,out JsonElement sprite,out JsonElement mask)
    {
        int id=i.Scope.ReadOr("mask_index",-1).Int;
        if(id<0)id=i.Scope.ReadOr("sprite_index",-1).Int;
        if(!Sprites.TryGetValue(id,out sprite)||sprite.GetProperty("masks").GetArrayLength()==0){mask=default;return false;}
        var masks=sprite.GetProperty("masks");
        int frame=(int)Math.Floor(i.Scope.ReadOr("image_index").Number);
        frame=(frame%masks.GetArrayLength()+masks.GetArrayLength())%masks.GetArrayLength();
        mask=masks[frame];return true;
    }

    public GmBounds Bounds(GmInstance instance,double? x=null,double? y=null)
    {
        if(!Mask(instance,out var sprite,out var mask))return new(0,0,-1,-1);
        double px=x??instance.Scope.ReadOr("x").Number,py=y??instance.Scope.ReadOr("y").Number;
        double sx=instance.Scope.ReadOr("image_xscale",1).Number,sy=instance.Scope.ReadOr("image_yscale",1).Number;
        double angle=instance.Scope.ReadOr("image_angle").Number*Math.PI/180,cs=Math.Cos(angle),sn=Math.Sin(angle);
        int ox=sprite.GetProperty("originX").GetInt32(),oy=sprite.GetProperty("originY").GetInt32();
        double left=mask.GetProperty("left").GetDouble()-ox,right=mask.GetProperty("right").GetDouble()+1-ox;
        double top=mask.GetProperty("top").GetDouble()-oy,bottom=mask.GetProperty("bottom").GetDouble()+1-oy;
        var corners=new[]{(left,top),(left,bottom),(right,top),(right,bottom)}.Select(p=>(X:px+p.Item1*sx*cs+p.Item2*sy*sn,Y:py-p.Item1*sx*sn+p.Item2*sy*cs)).ToArray();
        return new(Math.Floor(corners.Min(p=>p.X)),Math.Floor(corners.Min(p=>p.Y)),Math.Ceiling(corners.Max(p=>p.X))-1,Math.Ceiling(corners.Max(p=>p.Y))-1);
    }

    private bool Opaque(GmInstance i,double x,double y,double? atX=null,double? atY=null)
    {
        if(!Mask(i,out var sprite,out var mask))return false;
        double sx=i.Scope.ReadOr("image_xscale",1).Number,sy=i.Scope.ReadOr("image_yscale",1).Number;
        if(sx==0||sy==0)return false;
        double dx=x-(atX??i.Scope.ReadOr("x").Number),dy=y-(atY??i.Scope.ReadOr("y").Number);
        double angle=i.Scope.ReadOr("image_angle").Number*Math.PI/180,cs=Math.Cos(angle),sn=Math.Sin(angle);
        int mx=(int)Math.Floor((dx*cs-dy*sn)/sx+sprite.GetProperty("originX").GetInt32());
        int my=(int)Math.Floor((dx*sn+dy*cs)/sy+sprite.GetProperty("originY").GetInt32());
        int w=mask.GetProperty("width").GetInt32(),h=mask.GetProperty("height").GetInt32();
        return mx>=0&&my>=0&&mx<w&&my<h&&mask.GetProperty("data").GetString()![my*w+mx]=='1';
    }

    private bool Touches(GmInstance a,GmInstance b,double x,double y,bool precise=true)
    {
        var ba=Bounds(a,x,y);var bb=Bounds(b);
        if(!ba.Overlaps(bb))return false;
        if(!precise)return true;
        for(double py=Math.Max(ba.Top,bb.Top);py<=Math.Min(ba.Bottom,bb.Bottom);py++)
        for(double px=Math.Max(ba.Left,bb.Left);px<=Math.Min(ba.Right,bb.Right);px++)
            if(Opaque(a,px+0.5,py+0.5,x,y)&&Opaque(b,px+0.5,py+0.5))return true;
        return false;
    }

    private GmValue? Collision(string name,GmValue[] a,GmContext c)
    {
        double N(int i)=>i<a.Length?a[i].Number:0;
        int I(int i)=>i<a.Length?a[i].Int:0;
        var self=c.Self;
        IEnumerable<GmInstance> Targets(int target,bool notMe=true)=>Select(target,c).Where(i=>!notMe||i!=self);
        GmInstance? At(double x,double y,int target,bool solids=false)=>Targets(target).FirstOrDefault(i=>(!solids||i.Scope.ReadOr("solid").Truth)&&Touches(self,i,x,y));
        switch(name)
        {
            case "place_meeting":return At(N(0),N(1),I(2))is not null;
            case "instance_place":return At(N(0),N(1),I(2))?.Id??-4;
            case "place_free":return At(N(0),N(1),-3,true)is null;
            case "action_if_empty":return At(N(0),N(1),-3,I(2)==0)is null;
            case "action_if_object":return At(N(1),N(2),I(0))is not null;
            case "collision_point":
                return Targets(I(2),N(4)>=0.5).FirstOrDefault(i=>Bounds(i).Overlaps(new(N(0),N(1),N(0),N(1)))&&(N(3)<0.5||Opaque(i,N(0),N(1))))?.Id??-4;
            case "collision_rectangle":
            {
                var bounds=new GmBounds(Math.Min(N(0),N(2)),Math.Min(N(1),N(3)),Math.Max(N(0),N(2)),Math.Max(N(1),N(3)));
                foreach(var i in Targets(I(4),N(6)>=0.5))
                {
                    var b=Bounds(i);if(!b.Overlaps(bounds))continue;
                    if(N(5)<0.5)return i.Id;
                    for(double y=Math.Max(b.Top,bounds.Top);y<=Math.Min(b.Bottom,bounds.Bottom);y++)for(double x=Math.Max(b.Left,bounds.Left);x<=Math.Min(b.Right,bounds.Right);x++)if(Opaque(i,x+0.5,y+0.5))return i.Id;
                }
                return -4;
            }
            case "collision_circle":
            {
                double radius=Math.Abs(N(2));
                foreach(var i in Targets(I(3),N(5)>=0.5))
                {
                    var b=Bounds(i);if(b.Empty)continue;
                    double cx=Math.Clamp(N(0),b.Left,b.Right),cy=Math.Clamp(N(1),b.Top,b.Bottom);
                    if(Math.Pow(cx-N(0),2)+Math.Pow(cy-N(1),2)>radius*radius)continue;
                    if(N(4)<0.5)return i.Id;
                    for(double y=Math.Max(b.Top,N(1)-radius);y<=Math.Min(b.Bottom,N(1)+radius);y++)for(double x=Math.Max(b.Left,N(0)-radius);x<=Math.Min(b.Right,N(0)+radius);x++)if(Math.Pow(x-N(0),2)+Math.Pow(y-N(1),2)<=radius*radius&&Opaque(i,x+0.5,y+0.5))return i.Id;
                }
                return -4;
            }
            case "collision_line":
            {
                double dx=N(2)-N(0),dy=N(3)-N(1);int steps=Math.Max(1,(int)Math.Ceiling(Math.Max(Math.Abs(dx),Math.Abs(dy))));
                foreach(var i in Targets(I(4),N(6)>=0.5))
                {
                    var b=Bounds(i);if(!b.Overlaps(new(Math.Min(N(0),N(2)),Math.Min(N(1),N(3)),Math.Max(N(0),N(2)),Math.Max(N(1),N(3)))))continue;
                    for(int p=0;p<=steps;p++){double x=N(0)+dx*p/steps,y=N(1)+dy*p/steps;if(b.Overlaps(new(x,y,x,y))&&(N(5)<0.5||Opaque(i,x,y)))return i.Id;}
                }
                return -4;
            }
            case "distance_to_object":
            {
                var b=Bounds(self);double nearest=double.PositiveInfinity;
                foreach(var i in Targets(I(0)))
                {
                    var t=Bounds(i);double dx=Math.Max(0,Math.Max(b.Left-t.Right,t.Left-b.Right)),dy=Math.Max(0,Math.Max(b.Top-t.Bottom,t.Top-b.Bottom));nearest=Math.Min(nearest,Math.Sqrt(dx*dx+dy*dy));
                }
                return double.IsPositiveInfinity(nearest)?-1:nearest;
            }
            case "move_contact_all":case "move_contact_solid":case "action_move_contact":case "move_outside_solid":
            {
                bool solid=name!="move_contact_all";double angle=N(0)*Math.PI/180,dx=Math.Cos(angle),dy=-Math.Sin(angle),x=self.Scope.ReadOr("x").Number,y=self.Scope.ReadOr("y").Number;
                int max=N(1)<=0?1000:(int)Math.Ceiling(N(1));
                for(int step=0;step<max;step++)
                {
                    if(name=="move_outside_solid") {if(At(x,y,-3,solid)is null)break;}
                    else if(At(x+dx,y+dy,-3,solid)is not null)break;
                    x+=dx;y+=dy;
                }
                Write(self,"x",x);Write(self,"y",y);return 0;
            }
        }
        return null;
    }
}

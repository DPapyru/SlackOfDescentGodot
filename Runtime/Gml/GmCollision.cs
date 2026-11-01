using System.Text.Json;

namespace SlackOfDescent;

public readonly record struct GmBounds(double Left,double Top,double Right,double Bottom)
{
    public bool Empty=>Right<Left||Bottom<Top;
    public bool Overlaps(GmBounds b)=>!Empty&&!b.Empty&&Left<=b.Right&&Right>=b.Left&&Top<=b.Bottom&&Bottom>=b.Top;
}

internal sealed class GmMask
{
    public readonly int Width,Height,OriginX,OriginY;
    public readonly double Left,Top,Right,Bottom;
    private readonly JsonElement source;
    private string? pixels;
    public string Pixels=>pixels??=source.GetProperty("data").GetString()!;
    public GmMask(JsonElement source,int originX,int originY)
    {
        this.source=source;OriginX=originX;OriginY=originY;
        Width=source.GetProperty("width").GetInt32();Height=source.GetProperty("height").GetInt32();
        Left=source.GetProperty("left").GetDouble()-originX;Right=source.GetProperty("right").GetDouble()+1-originX;
        Top=source.GetProperty("top").GetDouble()-originY;Bottom=source.GetProperty("bottom").GetDouble()+1-originY;
    }
}

internal readonly struct GmMaskSampler
{
    private readonly string pixels;
    private readonly int width,height;
    private readonly double xx,xy,yx,yy,tx,ty;
    public GmMaskSampler(GmMask mask,GmInstance instance,double? x=null,double? y=null)
    {
        pixels=mask.Pixels;width=mask.Width;height=mask.Height;
        double sx=instance.Scope.ReadOr("image_xscale",1).Number,sy=instance.Scope.ReadOr("image_yscale",1).Number;
        double angle=instance.Scope.ReadOr("image_angle").Number*Math.PI/180,cs=Math.Cos(angle),sn=Math.Sin(angle);
        xx=cs/sx;xy=-sn/sx;yx=sn/sy;yy=cs/sy;
        double px=x??instance.Scope.ReadOr("x").Number,py=y??instance.Scope.ReadOr("y").Number;
        tx=mask.OriginX-px*xx-py*xy;ty=mask.OriginY-px*yx-py*yy;
    }
    public bool Opaque(double x,double y)
    {
        double localX=x*xx+y*xy+tx,localY=x*yx+y*yy+ty;
        if(!(localX>=0&&localY>=0&&localX<width&&localY<height))return false;
        return pixels[(int)Math.Floor(localY)*width+(int)Math.Floor(localX)]=='1';
    }
}

public sealed partial class GmWorld
{
    private readonly Dictionary<JsonElement,GmMask[]> collisionMasks=new(GmJsonIdentity.Instance);
    private readonly Dictionary<(int X,int Y),HashSet<GmInstance>> collisionCells=new();
    private readonly Dictionary<GmInstance,GmBounds> collisionMembership=new();
    private readonly HashSet<GmInstance> largeColliders=new();
    private readonly HashSet<GmInstance> changedColliders=new();
    private long geometryVersion;
    private readonly Stack<HashSet<GmInstance>> nearbyPool=new();
    private readonly Dictionary<(int Left,int Top,int Right,int Bottom),GmInstance[]> collisionRegions=new();
    private long collisionRegionsVersion=-1;
    private int collisionIndexedCount=-1;
    private const int CollisionCellSize=128;
    private static (int Left,int Top,int Right,int Bottom) Cells(GmBounds b)=>((int)Math.Floor(b.Left/CollisionCellSize),(int)Math.Floor(b.Top/CollisionCellSize),(int)Math.Floor(b.Right/CollisionCellSize),(int)Math.Floor(b.Bottom/CollisionCellSize));
    private static bool Large(GmBounds b)
    {
        var cells=Cells(b);return (long)(cells.Right-cells.Left+1)*(cells.Bottom-cells.Top+1)>256;
    }
    private void WatchCollider(GmInstance instance)
    {
        instance.Scope.Changed=name=>
        {
            // A sprite or speed change must re-validate the animation pass instead of staying skipped.
            if(name is "sprite_index" or "mask_index" or "image_speed")instance.AnimationSettled=false;
            if(name is "x" or "y" or "sprite_index" or "mask_index" or "image_xscale" or "image_yscale" or "image_angle")changedColliders.Add(instance);
            else if(name=="image_index"&&Mask(instance,out var mask)&&(!instance.HasBounds||!ReferenceEquals(instance.BoundsKey.Mask,mask)))changedColliders.Add(instance);
        };
    }
    internal void InvalidateCollisionSprites(){collisionIndexedCount=-1;geometryVersion++;spriteFrameCounts.Clear();}
    internal void ForgetCollisionSprite(JsonElement sprite)=>collisionMasks.Remove(sprite);
    private void IndexCollider(GmInstance instance)
    {
        var bounds=instance.Destroyed?new GmBounds(0,0,-1,-1):Bounds(instance);
        if(collisionMembership.TryGetValue(instance,out var previous)?previous==bounds:bounds.Empty)return;
        geometryVersion++;
        if(collisionMembership.Remove(instance,out var old))
        {
            if(Large(old))largeColliders.Remove(instance);
            else
            {
                var cells=Cells(old);
                for(int y=cells.Top;y<=cells.Bottom;y++)for(int x=cells.Left;x<=cells.Right;x++)
                    if(collisionCells.TryGetValue((x,y),out var members)){members.Remove(instance);if(members.Count==0)collisionCells.Remove((x,y));}
            }
        }
        if(bounds.Empty)return;
        collisionMembership[instance]=bounds;
        if(Large(bounds)){largeColliders.Add(instance);return;}
        var range=Cells(bounds);
        for(int y=range.Top;y<=range.Bottom;y++)for(int x=range.Left;x<=range.Right;x++)
        {
            if(!collisionCells.TryGetValue((x,y),out var members))collisionCells[(x,y)]=members=new();
            members.Add(instance);
        }
    }
    private void EnsureCollisionIndex(GmInstance? ignore=null)
    {
        if(collisionIndexedCount!=Instances.Count)
        {
            geometryVersion++;
            collisionCells.Clear();collisionMembership.Clear();largeColliders.Clear();changedColliders.Clear();
            for(int i=0;i<Instances.Count;i++){var instance=Instances[i];instance.CollisionOrder=i;WatchCollider(instance);IndexCollider(instance);}
            nextCollisionOrder=Instances.Count;
            collisionIndexedCount=Instances.Count;
        }
        else if(changedColliders.Count>0)
        {
            bool pending=ignore is not null&&changedColliders.Contains(ignore);
            foreach(var instance in changedColliders)if(instance!=ignore)IndexCollider(instance);
            changedColliders.Clear();
            if(pending)changedColliders.Add(ignore!);
        }
    }
    private HashSet<GmInstance> Nearby(GmBounds bounds,HashSet<GmInstance>? reuse=null,GmInstance? ignore=null)
    {
        EnsureCollisionIndex(ignore);
        var result=reuse??new HashSet<GmInstance>();result.Clear();if(bounds.Empty)return result;
        if(Large(bounds)){foreach(var pair in collisionMembership)if(pair.Value.Overlaps(bounds))result.Add(pair.Key);return result;}
        var range=Cells(bounds);
        for(int y=range.Top;y<=range.Bottom;y++)for(int x=range.Left;x<=range.Right;x++)
            if(collisionCells.TryGetValue((x,y),out var members))foreach(var instance in members)result.Add(instance);
        foreach(var instance in largeColliders)if(collisionMembership[instance].Overlaps(bounds))result.Add(instance);
        return result;
    }
    private GmInstance[] OrderedNearby(GmBounds bounds,GmInstance? ignore)
    {
        EnsureCollisionIndex(ignore);
        if(bounds.Empty)return [];
        if(collisionRegionsVersion!=geometryVersion){collisionRegions.Clear();collisionRegionsVersion=geometryVersion;}
        var cells=Cells(bounds);bool cache=!Large(bounds);
        if(cache&&collisionRegions.TryGetValue(cells,out var existing))return existing;
        var nearby=nearbyPool.Count>0?nearbyPool.Pop():new HashSet<GmInstance>();
        try
        {
            var region=cache?new GmBounds((double)cells.Left*CollisionCellSize,(double)cells.Top*CollisionCellSize,((double)cells.Right+1)*CollisionCellSize-1,((double)cells.Bottom+1)*CollisionCellSize-1):bounds;
            var ordered=Nearby(region,nearby,ignore).OrderBy(i=>i.CollisionOrder).ToArray();
            if(cache){if(collisionRegions.Count>=256)collisionRegions.Clear();collisionRegions[cells]=ordered;}
            return ordered;
        }
        finally{nearby.Clear();nearbyPool.Push(nearby);}
    }
    private IEnumerable<GmInstance> CollisionCandidates(GmBounds bounds,int target,GmContext context,bool notMe=true)
    {
        if(target is -1 or -2 or -4||target>=100000)
        {
            foreach(var instance in Select(target,context))if(!notMe||instance!=context.Self)yield return instance;
            yield break;
        }
        foreach(var instance in OrderedNearby(bounds,notMe?context.Self:null))
            if(!instance.Destroyed&&instance.Active&&(!notMe||instance!=context.Self)&&(target==-3||instance.Id==target||IsObject(instance.Object,target)))yield return instance;
    }
    public bool MouseOver(GmInstance instance,double x,double y)=>Bounds(instance).Overlaps(new(x,y,x,y))&&Opaque(instance,x,y);

    private void BoundaryEvents()
    {
        void Dispatch(int sub,double left,double top,double right,double bottom,bool intersect)
        {
            foreach(var instance in EventInstances(7,sub).ToArray())
            {
                if(instance.Destroyed||!instance.Active)continue;
                bool hit;
                if(Mask(instance,out _))
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
            double x=Engine.Read("view_xview",view).Number,y=Engine.Read("view_yview",view).Number;
            Dispatch(40+kind*10+view,x,y,x+Engine.Read("view_wview",view).Number,y+Engine.Read("view_hview",view).Number,kind==1);
        }
    }

    private bool Mask(GmInstance i,out GmMask mask)
    {
        int id=i.Scope.ReadOr("mask_index",-1).Int;
        if(id<0)id=i.Scope.ReadOr("sprite_index",-1).Int;
        if(!Sprites.TryGetValue(id,out var sprite)){mask=null!;return false;}
        if(!collisionMasks.TryGetValue(sprite,out var masks))
        {
            int originX=sprite.GetProperty("originX").GetInt32(),originY=sprite.GetProperty("originY").GetInt32();
            collisionMasks[sprite]=masks=sprite.GetProperty("masks").EnumerateArray().Select(m=>new GmMask(m,originX,originY)).ToArray();
        }
        if(masks.Length==0){mask=null!;return false;}
        if(masks.Length==1){mask=masks[0];return true;}
        int frame=(int)Math.Floor(i.Scope.ReadOr("image_index").Number);
        frame=(frame%masks.Length+masks.Length)%masks.Length;
        mask=masks[frame];return true;
    }

    public GmBounds Bounds(GmInstance instance,double? x=null,double? y=null)
    {
        if(NativeServices?.Entities.TryBounds(instance,x,y,out var nativeBounds)==true)return nativeBounds;
        if(!Mask(instance,out var mask))return new(0,0,-1,-1);
        double px=x??instance.Scope.ReadOr("x").Number,py=y??instance.Scope.ReadOr("y").Number;
        double sx=instance.Scope.ReadOr("image_xscale",1).Number,sy=instance.Scope.ReadOr("image_yscale",1).Number;
        double degrees=instance.Scope.ReadOr("image_angle").Number;
        var key=(mask,px,py,sx,sy,degrees);
        var cached=instance.BoundsKey;
        if(instance.HasBounds&&cached.X==px&&cached.Y==py&&cached.ScaleX==sx&&cached.ScaleY==sy&&cached.Angle==degrees&&ReferenceEquals(cached.Mask,mask))return instance.CachedBounds;
        double angle=degrees*Math.PI/180,cs=Math.Cos(angle),sn=Math.Sin(angle);
        double left=mask.Left,right=mask.Right,top=mask.Top,bottom=mask.Bottom;
        double x1=px+left*sx*cs+top*sy*sn,x2=px+left*sx*cs+bottom*sy*sn,x3=px+right*sx*cs+top*sy*sn,x4=px+right*sx*cs+bottom*sy*sn;
        double y1=py-left*sx*sn+top*sy*cs,y2=py-left*sx*sn+bottom*sy*cs,y3=py-right*sx*sn+top*sy*cs,y4=py-right*sx*sn+bottom*sy*cs;
        instance.BoundsKey=key;instance.HasBounds=true;
        return instance.CachedBounds=new(Math.Floor(Math.Min(Math.Min(x1,x2),Math.Min(x3,x4))),Math.Floor(Math.Min(Math.Min(y1,y2),Math.Min(y3,y4))),Math.Ceiling(Math.Max(Math.Max(x1,x2),Math.Max(x3,x4)))-1,Math.Ceiling(Math.Max(Math.Max(y1,y2),Math.Max(y3,y4)))-1);
    }

    private bool Opaque(GmInstance i,double x,double y,double? atX=null,double? atY=null)
    {
        if(NativeServices?.Entities.TryBounds(i,atX,atY,out var nativeBounds)==true)return nativeBounds.Overlaps(new(x,y,x,y));
        if(!Mask(i,out var mask))return false;
        return new GmMaskSampler(mask,i,atX,atY).Opaque(x,y);
    }

    private bool Touches(GmInstance a,GmInstance b,double x,double y,bool precise=true)
    {
        var ba=Bounds(a,x,y);var bb=Bounds(b);
        if(!ba.Overlaps(bb))return false;
        if(!precise)return true;
        if(NativeServices?.Entities.TryBounds(a,x,y,out _)==true||NativeServices?.Entities.TryBounds(b,null,null,out _)==true)
        {
            for(double py=Math.Max(ba.Top,bb.Top);py<=Math.Min(ba.Bottom,bb.Bottom);py++)
            for(double px=Math.Max(ba.Left,bb.Left);px<=Math.Min(ba.Right,bb.Right);px++)
                if(Opaque(a,px+0.5,py+0.5,x,y)&&Opaque(b,px+0.5,py+0.5))return true;
            return false;
        }
        if(!Mask(a,out var am)||!Mask(b,out var bm))return false;
        var first=new GmMaskSampler(am,a,x,y);var second=new GmMaskSampler(bm,b);
        for(double py=Math.Max(ba.Top,bb.Top);py<=Math.Min(ba.Bottom,bb.Bottom);py++)
        for(double px=Math.Max(ba.Left,bb.Left);px<=Math.Min(ba.Right,bb.Right);px++)
            if(first.Opaque(px+0.5,py+0.5)&&second.Opaque(px+0.5,py+0.5))return true;
        return false;
    }

    private GmInstance? InstanceAt(double x,double y,int target,GmContext context,bool solids=false)
    {
        foreach(var instance in CollisionCandidates(Bounds(context.Self,x,y),target,context))
            if((!solids||instance.Scope.ReadOr("solid").Truth)&&Touches(context.Self,instance,x,y))return instance;
        return null;
    }

    private GmValue? Collision(string name,GmValue[] a,GmContext c)
    {
        double N(int i)=>i<a.Length?a[i].Number:0;
        int I(int i)=>i<a.Length?a[i].Int:0;
        var self=c.Self;
        IEnumerable<GmInstance> Targets(int target,bool notMe=true)=>Select(target,c).Where(i=>!notMe||i!=self);
        GmInstance? At(double x,double y,int target,bool solids=false)=>InstanceAt(x,y,target,c,solids);
        switch(name)
        {
            case "move_bounce_solid":case "move_bounce_all":
            {
                bool solid=name=="move_bounce_solid";
                double x=self.Scope.ReadOr("x").Number,y=self.Scope.ReadOr("y").Number;
                bool bounce=At(x,y,-3,solid)is not null;
                if(bounce){x=self.Scope.ReadOr("xprevious").Number;y=self.Scope.ReadOr("yprevious").Number;}
                double h=self.Scope.ReadOr("hspeed").Number,v=self.Scope.ReadOr("vspeed").Number;
                if(I(0)==1)
                {
                    double speed=self.Scope.ReadOr("speed").Number,start=Math.Round(self.Scope.ReadOr("direction").Number/10,MidpointRounding.ToEven)*10;
                    double Side(int step)
                    {
                        double angle=start;
                        for(int turn=0;turn<36;turn++)
                        {
                            angle+=step;
                            if(At(x+speed*Math.Cos(angle*Math.PI/180),y-speed*Math.Sin(angle*Math.PI/180),-3,solid)is null)break;
                            bounce=true;
                        }
                        return angle;
                    }
                    double clockwise=Side(-10),counterclockwise=Side(10);
                    if(bounce)Write(self,"direction",clockwise+counterclockwise+180-start);
                }
                else
                {
                    bool horizontal=At(x+h,y,-3,solid)is not null,vertical=At(x,y+v,-3,solid)is not null;
                    if(horizontal)Write(self,"hspeed",-h);
                    if(vertical)Write(self,"vspeed",-v);
                    if(!horizontal&&!vertical&&At(x+h,y+v,-3,solid)is not null){Write(self,"hspeed",-h);Write(self,"vspeed",-v);}
                }
                Write(self,"x",x);Write(self,"y",y);return 0;
            }
            case "place_meeting":return At(N(0),N(1),I(2))is not null;
            case "instance_place":return At(N(0),N(1),I(2))?.Id??-4;
            case "place_free":return At(N(0),N(1),-3,true)is null;
            case "action_if_empty":return At(N(0),N(1),-3,I(2)==0)is null;
            case "action_if_object":return At(N(1),N(2),I(0))is not null;
            case "collision_point":
                return CollisionCandidates(new(N(0),N(1),N(0),N(1)),I(2),c,N(4)>=0.5).FirstOrDefault(i=>Bounds(i).Overlaps(new(N(0),N(1),N(0),N(1)))&&(N(3)<0.5||Opaque(i,N(0),N(1))))?.Id??-4;
            case "collision_rectangle":
            {
                var bounds=new GmBounds(Math.Min(N(0),N(2)),Math.Min(N(1),N(3)),Math.Max(N(0),N(2)),Math.Max(N(1),N(3)));
                foreach(var i in CollisionCandidates(bounds,I(4),c,N(6)>=0.5))
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
                foreach(var i in CollisionCandidates(new(N(0)-radius,N(1)-radius,N(0)+radius,N(1)+radius),I(3),c,N(5)>=0.5))
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
                foreach(var i in CollisionCandidates(new(Math.Min(N(0),N(2)),Math.Min(N(1),N(3)),Math.Max(N(0),N(2)),Math.Max(N(1),N(3))),I(4),c,N(6)>=0.5))
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

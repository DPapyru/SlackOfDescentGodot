using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public partial class GmCanvas : Node2D
{
    public readonly List<Action<GmCanvas>> Commands=new();
    public Action<GmCanvas>? Paint;
    private readonly List<GmCanvas> batches=new();
    private readonly Dictionary<int,Material> materials=new();
    private BackBufferCopy? screenCopy;
    private int batchIndex;
    private int lastBlend=-1;
    private int lastFilter=-1;
    public void StartBatches(){batchIndex=0;lastBlend=-1;foreach(var batch in batches){batch.Visible=false;batch.Commands.Clear();if(batch.screenCopy is not null)batch.screenCopy.Visible=false;}}
    public void Batch(Action<GmCanvas> command,int blend,int filter=0)
    {
        if(batchIndex>0&&lastBlend==blend&&lastFilter==filter&&blend is not (5 or 6)){batches[batchIndex-1].Commands.Add(command);batches[batchIndex-1].QueueRedraw();return;}
        lastBlend=blend;lastFilter=filter;
        GmCanvas batch;
        if(batchIndex<batches.Count)batch=batches[batchIndex];
        else{batch=new GmCanvas();batches.Add(batch);AddChild(batch);}
        batchIndex++;batch.Visible=true;
        batch.TextureFilter=filter==2?TextureFilterEnum.Linear:filter==1?TextureFilterEnum.Nearest:TextureFilterEnum.ParentNode;
        if(!materials.TryGetValue(blend,out var material))
        {
            if(blend is 4 or 5 or 6)
            {
                string expression=blend==5?"source * destination.a + destination * (1.0 - destination.a)":"source * destination + destination";
                string code=blend==4?"shader_type canvas_item; render_mode unshaded, blend_disabled;":"shader_type canvas_item; render_mode unshaded, blend_disabled; uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest; varying vec4 vertex_tint; void vertex() { vertex_tint = COLOR; } void fragment() { vec4 source = vertex_tint * texture(TEXTURE, UV); vec4 destination = texture(screen_texture, SCREEN_UV); COLOR = "+expression+"; }";
                var shader=new Shader{Code=code};
                material=new ShaderMaterial{Shader=shader};
            }
            else material=new CanvasItemMaterial{BlendMode=blend switch{1=>CanvasItemMaterial.BlendModeEnum.Add,2=>CanvasItemMaterial.BlendModeEnum.Mul,3=>CanvasItemMaterial.BlendModeEnum.Sub,4=>CanvasItemMaterial.BlendModeEnum.PremultAlpha,_=>CanvasItemMaterial.BlendModeEnum.Mix}};
            materials[blend]=material;
        }
        if(blend is 5 or 6)
        {
            if(batch.screenCopy is null){batch.screenCopy=new BackBufferCopy{CopyMode=BackBufferCopy.CopyModeEnum.Viewport};AddChild(batch.screenCopy);}
            MoveChild(batch.screenCopy,GetChildCount()-1);batch.screenCopy.Visible=true;
        }
        MoveChild(batch,GetChildCount()-1);
        batch.Material=material;
        batch.Commands.Add(command);batch.QueueRedraw();
    }
    public override void _Draw(){if(Commands.Count>0)StartBatches();Paint?.Invoke(this);foreach(var command in Commands.ToArray())command(this);}
}

public sealed partial class GmGraphics
{
    public int AuditResourceImages()
    {
        var media=JsonDocument.Parse(File.ReadAllText(Path.Combine(world.Root,"media.json")));
        using(media)
        {
            var frames=world.Manifest.GetProperty("sprites").EnumerateArray().Concat(media.RootElement.EnumerateObject().Select(p=>p.Value)).SelectMany(s=>s.GetProperty("frames").EnumerateArray());
            var paths=frames.Select(f=>f.GetProperty("path").GetString()!).Concat(new[]{"backgrounds","fonts"}.SelectMany(group=>world.Manifest.GetProperty(group).EnumerateArray()).Select(a=>a.GetProperty("path").GetString()!)).Distinct().ToArray();
            foreach(string path in paths)
            {
                using var image=Image.LoadFromFile(Path.Combine(world.Root,path));
                if(image is null||image.IsEmpty())throw new InvalidDataException("Packaged image failed to decode: "+path);
            }
            return paths.Length;
        }
    }
    private readonly Node owner;
    private readonly GmWorld world;
    private readonly GmTriangleCache triangleCache=new();
    private int triangleCacheRoom=int.MinValue;
    private readonly Dictionary<string,Texture2D> textures=new();
    private readonly Dictionary<int,(SubViewport View,GmCanvas Canvas)> surfaces=new();
    private readonly Dictionary<int,Image> surfacePixels=new();
    private readonly Dictionary<int,byte[]> surfaceData=new();
    private readonly Dictionary<int,ImageTexture> surfaceTextures=new();
    private readonly HashSet<int> dirtySurfaces=new();
    private readonly HashSet<int> dirtySurfaceTextures=new();
    private readonly Dictionary<Texture2D,(byte[] Data,int Width,int Height)> textureData=new();
    private readonly List<(double Depth,Texture2D Texture,Rect2 Source,Vector2 Position,Vector2 Scale,Color Tint,Rect2 Bounds)> tileData=new();
    private int tilesVersion=-1;
    private readonly Dictionary<int,string> backgroundPaths=new();
    private readonly List<Action<GmCanvas>> main=new();
    private int nextSurface=1;
    private int target=-1;
    private Color colour=Colors.White;
    private float alpha=1;
    private int font=-1;
    private int halign;
    private int blend;
    private Vector2 viewSize=new(640,480);
    public Vector2 Camera;
    private readonly List<Vector2> vertices=new();
    private readonly List<Vector2> uvs=new();
    private readonly List<Color> vertexColours=new();
    private int primitive;
    private Texture2D? primitiveTexture;
    public Func<IEnumerable<(double Depth,Action Draw)>>? AdditionalDraws;
    public void DrawExternal(string path,Vector2 origin,Vector2 position,Vector2 scale,float angle)
    {var texture=Texture(path);Textured(texture,origin,new(Vector2.Zero,texture.GetSize()),position,scale,angle,Colors.White);}
    private Texture2D[]? particleShapes;
    private ImageTexture? white;
    internal List<(int Sprite,double Frame,float Alpha)>? SpriteAudit;
    internal int SpriteOpaquePixels(int id,double frame)
    {
        if(!world.Sprites.TryGetValue(id,out var sprite))return 0;
        var frames=sprite.GetProperty("frames");if(frames.GetArrayLength()==0)return 0;
        int index=((int)Math.Floor(frame)%frames.GetArrayLength()+frames.GetArrayLength())%frames.GetArrayLength();
        var pixels=TexturePixels(Texture(frames[index].GetProperty("path").GetString()!)).Data;
        int count=0;for(int i=3;i<pixels.Length;i+=4)if(pixels[i]>0)count++;return count;
    }
    public object Statistics()=>new{surfaces=surfaces.Count,textures=textures.Count,pixelCaches=textureData.Count,generated=textures.Keys.Count(k=>k.StartsWith("generated/",StringComparison.Ordinal)),triangleCacheEntries=triangleCache.Count,triangleCacheBytes=triangleCache.Bytes,triangleCacheHits=triangleCache.Hits,triangleCacheMisses=triangleCache.Misses,effects=EffectStatistics(),cpuFallbacks=effectFallbacks};
    private void ReleaseTexture(string path)
    {
        if(world.Sprites.Values.Any(s=>s.GetProperty("frames").EnumerateArray().Any(f=>f.GetProperty("path").GetString()==path)))return;
        foreach(string key in new[]{path,"localization/"+path})if(textures.Remove(key,out var texture))textureData.Remove(texture);
    }
    private ImageTexture White
    {
        get
        {
            if(white is not null)return white;
            using var image=Image.CreateFromData(1,1,false,Image.Format.Rgba8,[255,255,255,255]);return white=ImageTexture.CreateFromImage(image);
        }
    }

    public GmGraphics(Node owner,GmWorld world)
    {
        this.owner=owner;this.world=world;
        foreach(var background in world.Manifest.GetProperty("backgrounds").EnumerateArray())backgroundPaths[background.GetProperty("id").GetInt32()]=background.GetProperty("path").GetString()!;
    }
    public static Color Colour(double value,double opacity=1)
    {
        uint n=(uint)value;
        return new Color((n&255)/255f,((n>>8)&255)/255f,((n>>16)&255)/255f,(float)Math.Clamp(opacity,0,1));
    }
    private Color Tint=>new(colour.R,colour.G,colour.B,Math.Clamp(alpha,0,1));
    private void Add(Action<GmCanvas> command)
    {
        if(target>=0&&effects.ContainsKey(target))SoftwareEffect(target);
        int mode=blend,filter=drawFilter;
        Action<GmCanvas> draw=c=>c.Batch(command,mode,filter);
        if(target<0)main.Add(draw);
        else if(surfaces.TryGetValue(target,out var surface)){surface.Canvas.Commands.Add(draw);surface.Canvas.QueueRedraw();}
    }
    public void Begin(){main.Clear();SpriteAudit?.Clear();}
    public void Render(GmCanvas canvas)
    {
        canvas.Transform=Transform2D.Identity;
        if(world.Engine.Read("view_enabled").Truth)
        {
            var view=world.View(0);var zero=view.ToScreen(Camera.X,Camera.Y);var right=view.ToScreen(Camera.X+1,Camera.Y);var down=view.ToScreen(Camera.X,Camera.Y+1);
            canvas.Transform=new(new((float)(right.X-zero.X),(float)(right.Y-zero.Y)),new((float)(down.X-zero.X),(float)(down.Y-zero.Y)),new((float)zero.X,(float)zero.Y));
        }
        canvas.StartBatches();foreach(var command in main)command(canvas);
    }
    public void Build()
    {
        Begin();
        Camera=world.Engine.ReadOr("view_enabled").Truth?new((float)world.Engine.Read("view_xview",[0]).Number,(float)world.Engine.Read("view_yview",[0]).Number):Vector2.Zero;
        viewSize=world.Engine.ReadOr("view_enabled").Truth?new((float)world.Engine.Read("view_wview",[0]).Number,(float)world.Engine.Read("view_hview",[0]).Number):new(world.Engine.Read("room_width").Int,world.Engine.Read("room_height").Int);
        RoomBackgrounds(false);
        var draws=new List<(double Depth,Action Draw)>();
        if(AdditionalDraws is not null)draws.AddRange(AdditionalDraws());
        foreach(var system in world.Particles.Systems.Values.Where(s=>s.AutoDraw))draws.Add((system.Depth,()=>DrawParticles(system)));
        foreach(var instance in world.Instances.Where(i=>!i.Destroyed&&i.Active&&i.Scope.ReadOr("visible").Truth).ToArray())
            draws.Add((instance.Scope.ReadOr("depth").Number,()=>{if(!instance.Destroyed&&!world.Event(instance,8,0))Instance(instance);}));
        if(tilesVersion!=world.TilesVersion)
        {
            tileData.Clear();tilesVersion=world.TilesVersion;
            foreach(var tile in world.Tiles)
            {
                int depth=tile.GetProperty("depth").GetInt32();if(world.HiddenTileLayers.Contains(depth))continue;
                var source=new Rect2((float)tile.GetProperty("sourceX").GetDouble(),(float)tile.GetProperty("sourceY").GetDouble(),(float)tile.GetProperty("width").GetDouble(),(float)tile.GetProperty("height").GetDouble());
                var position=new Vector2((float)tile.GetProperty("x").GetDouble(),(float)tile.GetProperty("y").GetDouble());
                var scale=new Vector2((float)tile.GetProperty("xscale").GetDouble(),(float)tile.GetProperty("yscale").GetDouble());
                var end=position+source.Size*scale;
                tileData.Add((depth,Texture(backgroundPaths[tile.GetProperty("background").GetInt32()]),source,position,scale,Colour(tile.GetProperty("blend").GetDouble()),new(position.Min(end),(end-position).Abs())));
            }
        }
        var visible=new Rect2(Camera-Vector2.One,viewSize+Vector2.One*2);
        foreach(var tile in tileData)
        {
            if(!visible.Intersects(tile.Bounds,true))continue;
            var data=tile;
            draws.Add((data.Depth,()=>Textured(data.Texture,Vector2.Zero,data.Source,data.Position,data.Scale,0,data.Tint)));
        }
        foreach(var draw in draws.OrderByDescending(d=>d.Depth))draw.Draw();
        RoomBackgrounds(true);
    }
    private void Tiled(Texture2D texture,Vector2 position,Vector2 scale,Color tint,bool horizontal=true,bool vertical=true)
    {
        var size=texture.GetSize()*scale;if(size.X<=0||size.Y<=0)return;
        var bounds=target<0?new Rect2(Camera,viewSize):new Rect2(Vector2.Zero,SurfaceSize(target));
        float x1=horizontal?position.X+MathF.Floor((bounds.Position.X-position.X)/size.X)*size.X:position.X;
        float y1=vertical?position.Y+MathF.Floor((bounds.Position.Y-position.Y)/size.Y)*size.Y:position.Y;
        float x2=horizontal?bounds.End.X:x1+size.X,y2=vertical?bounds.End.Y:y1+size.Y;
        for(float y=y1;y<y2;y+=size.Y)for(float x=x1;x<x2;x+=size.X)Textured(texture,Vector2.Zero,new(Vector2.Zero,texture.GetSize()),new(x,y),scale,0,tint);
    }
    private void RoomBackgrounds(bool foreground)
    {
        int savedBlend=blend;blend=0;
        for(int index=0;index<8;index++)
        {
            GmValue Read(string name)=>world.Engine.Read("background_"+name,[index]);
            if(!Read("visible").Truth||Read("foreground").Truth!=foreground||!backgroundPaths.TryGetValue(Read("index").Int,out var path))continue;
            Tiled(Texture(path),new((float)Read("x").Number,(float)Read("y").Number),new((float)Read("xscale").Number,(float)Read("yscale").Number),Colour(Read("blend").Number,Read("alpha").Number),Read("htiled").Truth,Read("vtiled").Truth);
        }
        blend=savedBlend;
    }
    private Texture2D Texture(string path)
    {
        path=NativeLocalization.AssetPath(path);
        if(textures.TryGetValue(path,out var texture))return texture;
        using var image=Image.LoadFromFile(Path.Combine(world.Root,path));
        return textures[path]=ImageTexture.CreateFromImage(image);
    }
    private void DrawParticles(GmParticleSystem system)
    {
        int savedBlend=blend;
        IEnumerable<GmParticle> particles=system.OldToNew?system.Particles:system.Particles.AsEnumerable().Reverse();
        foreach(var p in particles)
        {
            if(!world.Particles.Types.TryGetValue(p.Type,out var t))continue;
            blend=t.Additive?1:0;
            double size=p.Size+t.SizeWiggle*GmParticles.Wiggle(p.Timer,p.RandomStart,1,16);
            float angle=-Mathf.DegToRad((float)(p.Angle+t.AngleWiggle*GmParticles.Wiggle(p.Timer,p.RandomStart,2,16)+(t.RelativeAngle?p.Direction:0)));
            var scale=new Vector2((float)(size*t.XScale),(float)(size*t.YScale));
            var position=new Vector2((float)(system.X+p.X),(float)(system.Y+p.Y));var tint=Colour(p.Colour,p.Alpha);
            if(t.Sprite>=0)
            {
                int frame=p.Subimage;
                if(t.Animated)frame+=t.Stretched&&p.Lifetime>0&&world.Sprites.TryGetValue(t.Sprite,out var sprite)?sprite.GetProperty("frames").GetArrayLength()*p.Timer/p.Lifetime:p.Timer;
                Sprite(t.Sprite,frame,position,scale,angle,tint);
            }
            else if(t.Shape>=0&&t.Shape<14)
            {
                if(particleShapes is null)
                {
                    var atlas=File.ReadAllBytes(Path.Combine(world.Root,"particles.bin"));particleShapes=new Texture2D[14];
                    for(int shape=0;shape<14;shape++)
                    {
                        var rgba=new byte[64*64*4];Array.Fill(rgba,(byte)255);
                        for(int pixel=0;pixel<64*64;pixel++)rgba[pixel*4+3]=atlas[shape*64*64+pixel];
                        using var image=Image.CreateFromData(64,64,false,Image.Format.Rgba8,rgba);particleShapes[shape]=ImageTexture.CreateFromImage(image);
                    }
                }
                Textured(particleShapes[t.Shape],new(32,32),new(0,0,64,64),position,scale,angle,tint);
            }
        }
        blend=savedBlend;
    }
    private int CaptureSprite(Image source,Rect2I region,int transparency,bool smooth,int originX,int originY,int? append=null)
    {
        int x=Math.Max(0,region.Position.X),y=Math.Max(0,region.Position.Y);
        int width=Math.Min(region.Size.X,source.GetWidth()-x),height=Math.Min(region.Size.Y,source.GetHeight()-y);
        if(width<=0||height<=0)throw new ArgumentException("Empty sprite capture region");
        using var image=source.GetRegion(new(x,y,width,height));image.Convert(Image.Format.Rgba8);
        var pixels=image.GetData();GmPixels.Prepare(pixels,width,height,transparency,smooth);
        using var prepared=Image.CreateFromData(width,height,false,Image.Format.Rgba8,pixels);
        var path="generated/"+Guid.NewGuid().ToString("N");textures[path]=ImageTexture.CreateFromImage(prepared);
        var frame=new{path,width,height};var mask=GmPixels.Mask(pixels,width,height);
        if(append is int id)
        {
            var node=JsonNode.Parse(world.Sprites[id].GetRawText())!;
            node["frames"]!.AsArray().Add(JsonSerializer.SerializeToNode(frame));
            node["masks"]!.AsArray().Add(JsonNode.Parse(mask.GetRawText()));
            world.Sprites[id]=JsonSerializer.SerializeToElement(node);world.InvalidateCollisionSprites();return id;
        }
        return world.RegisterSprite(JsonSerializer.SerializeToElement(new{originX,originY,frames=new[]{frame},masks=new[]{mask}}));
    }
    private void Textured(Texture2D texture,Vector2 origin,Rect2 source,Vector2 position,Vector2 scale,float angle,Color tint)
    {
        if(target>=0&&effects.TryGetValue(target,out var effect))
        {
            var transform=new Transform2D(angle,scale,0,position);
            var a=transform*-origin;var b=transform*(new Vector2(source.Size.X,0)-origin);
            var c=transform*(source.Size-origin);var d=transform*(new Vector2(0,source.Size.Y)-origin);
            if(texture==White&&tint.A>=1&&blend is 0 or 4&&angle==0&&a.X<=0&&a.Y<=0&&c.X>=effect.Logical.X&&c.Y>=effect.Logical.Y)
            {
                effect.Reset();c.Y=d.Y=effect.View.Size.Y*effect.Divisor;c.X=b.X=effect.View.Size.X*effect.Divisor;
            }
            var size=texture.GetSize();var uv=source.Position/size;var end=source.End/size;
            effect.Triangle(texture,blend,a,b,c,tint,tint,tint,uv,new(end.X,uv.Y),end);
            effect.Triangle(texture,blend,a,c,d,tint,tint,tint,uv,end,new(uv.X,end.Y));
            return;
        }
        if(target>=0&&surfacePixels.TryGetValue(target,out var pixels))
        {
            var data=TexturePixels(texture);
            Composite(surfaceData[target],pixels.GetWidth(),pixels.GetHeight(),data.Data,data.Width,data.Height,origin,source,position,scale,angle,tint);
            DirtySurface(target);return;
        }
        var camera=target<0?Camera:Vector2.Zero;
        if(target<0)
        {
            var transform=new Transform2D(angle,scale,0,position-camera);
            var a=transform*-origin;var b=transform*(new Vector2(source.Size.X,0)-origin);
            var c=transform*(source.Size-origin);var d=transform*(new Vector2(0,source.Size.Y)-origin);
            if(Math.Max(Math.Max(a.X,b.X),Math.Max(c.X,d.X))<0||Math.Max(Math.Max(a.Y,b.Y),Math.Max(c.Y,d.Y))<0||Math.Min(Math.Min(a.X,b.X),Math.Min(c.X,d.X))>viewSize.X||Math.Min(Math.Min(a.Y,b.Y),Math.Min(c.Y,d.Y))>viewSize.Y)return;
        }
        Add(c=>{c.DrawSetTransform(position-camera,angle,scale);c.DrawTextureRectRegion(texture,new Rect2(-origin,source.Size),source,tint);c.DrawSetTransform(Vector2.Zero);});
    }
    private Image SurfaceImage(int id)
    {
        SoftwareEffect(id);
        var image=surfacePixels[id];
        if(dirtySurfaces.Remove(id))image.SetData(image.GetWidth(),image.GetHeight(),false,Image.Format.Rgba8,surfaceData[id]);
        return image;
    }
    private void DirtySurface(int id){dirtySurfaces.Add(id);dirtySurfaceTextures.Add(id);}
    private Texture2D SurfaceTexture(int id)
    {
        if(effects.TryGetValue(id,out var effect)){effect.Schedule();return surfaces[id].View.GetTexture();}
        bool changed=dirtySurfaceTextures.Remove(id);
        var image=SurfaceImage(id);
        if(!surfaceTextures.TryGetValue(id,out var texture))surfaceTextures[id]=texture=ImageTexture.CreateFromImage(image);
        else if(changed){texture.Update(image);textureData.Remove(texture);}
        return texture;
    }
    private void Composite(byte[] destination,int width,int height,byte[] image,int imageWidth,int imageHeight,Vector2 origin,Rect2 source,Vector2 position,Vector2 scale,float angle,Color tint)
    {
        if(scale.X==0||scale.Y==0)return;
        var transform=new Transform2D(angle,scale,0,position);
        var inverse=transform.AffineInverse();
        var corners=new[]{-origin,new Vector2(source.Size.X,0)-origin,source.Size-origin,new Vector2(0,source.Size.Y)-origin}.Select(p=>transform*p).ToArray();
        int left=Math.Max(0,(int)Math.Floor(corners.Min(p=>p.X))),right=Math.Min(width-1,(int)Math.Ceiling(corners.Max(p=>p.X)));
        int top=Math.Max(0,(int)Math.Floor(corners.Min(p=>p.Y))),bottom=Math.Min(height-1,(int)Math.Ceiling(corners.Max(p=>p.Y)));
        if(imageWidth==1&&imageHeight==1&&angle==0&&tint.A>=1&&blend is 0 or 4)
        {
            int x1=Math.Max(0,(int)Math.Ceiling(corners.Min(p=>p.X)-0.5)),x2=Math.Min(width,(int)Math.Ceiling(corners.Max(p=>p.X)-0.5));
            int y1=Math.Max(0,(int)Math.Ceiling(corners.Min(p=>p.Y)-0.5)),y2=Math.Min(height,(int)Math.Ceiling(corners.Max(p=>p.Y)-0.5));
            GmPixels.FillRectangle(destination,width,height,x1,y1,x2,y2,(byte)(Math.Clamp(tint.R,0,1)*255),(byte)(Math.Clamp(tint.G,0,1)*255),(byte)(Math.Clamp(tint.B,0,1)*255),255);return;
        }
        float xx=inverse.X.X,xy=inverse.Y.X,yx=inverse.X.Y,yy=inverse.Y.Y,tx=inverse.Origin.X,ty=inverse.Origin.Y;
        if(angle==0&&scale.X>0&&scale.Y>0&&origin==Vector2.Zero&&source.Position==Vector2.Zero&&source.Size==new Vector2(imageWidth,imageHeight)&&tint==Colors.White&&blend==0)
        {
            GmPixels.CompositeNormal(destination,width,height,image,imageWidth,imageHeight,left,top,right+1,bottom+1,xx,yy,tx,ty);
            return;
        }
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {
            float localX=xx*(x+0.5f)+xy*(y+0.5f)+tx+origin.X,localY=yx*(x+0.5f)+yy*(y+0.5f)+ty+origin.Y;
            if(localX<0||localY<0||localX>=source.Size.X||localY>=source.Size.Y)continue;
            int sx=(int)MathF.Floor(source.Position.X+localX),sy=(int)MathF.Floor(source.Position.Y+localY);
            if(sx<0||sy<0||sx>=imageWidth||sy>=imageHeight)continue;
            int si=(sy*imageWidth+sx)*4,di=(y*width+x)*4;
            float sr=image[si]/255f*tint.R,sg=image[si+1]/255f*tint.G,sb=image[si+2]/255f*tint.B,sa=image[si+3]/255f*tint.A;
            BlendPixel(destination,di,sr,sg,sb,sa);
        }
    }
    private void BlendPixel(byte[] destination,int di,float sr,float sg,float sb,float sa,bool round=false)
    {
            float dr=destination[di]/255f,dg=destination[di+1]/255f,db=destination[di+2]/255f,da=destination[di+3]/255f;
            float r,g,b,a;
            switch(blend)
            {
                case 0:r=sr*sa+dr*(1-sa);g=sg*sa+dg*(1-sa);b=sb*sa+db*(1-sa);a=sa*sa+da*(1-sa);break;
                case 1:r=sr*sa+dr;g=sg*sa+dg;b=sb*sa+db;a=sa*sa+da;break;
                case 2:r=sr*dr;g=sg*dg;b=sb*db;a=sa*da;break;
                case 3:r=dr-sr*sa;g=dg-sg*sa;b=db-sb*sa;a=da-sa*sa;break;
                case 4:r=sr;g=sg;b=sb;a=sa;break;
                case 5:r=sr*da+dr*(1-da);g=sg*da+dg*(1-da);b=sb*da+db*(1-da);a=sa*da+da*(1-da);break;
                case 6:r=sr*dr+dr;g=sg*dg+dg;b=sb*db+db;a=sa*da+da;break;
                default:throw new NotSupportedException("Surface blend "+blend);
            }
            byte Quantize(float value)=>(byte)(round?MathF.Round(Math.Clamp(value,0,1)*255):Math.Clamp(value,0,1)*255);
            destination[di]=Quantize(r);
            destination[di+1]=Quantize(g);
            destination[di+2]=Quantize(b);
            destination[di+3]=Quantize(a);
    }
    private void Triangle(Vector2 a,Vector2 b,Vector2 c,Color ca,Color cb,Color cc,Vector2 ua,Vector2 ub,Vector2 uc,Texture2D? texture,bool round=false)
    {
        var pixels=surfacePixels[target];int width=pixels.GetWidth(),height=pixels.GetHeight();var destination=surfaceData[target];
        if(texture is null&&ca==Colors.Black&&cb==ca&&cc==ca&&blend==0)
        {
            if(triangleCacheRoom!=world.CurrentRoom){triangleCache.Clear();triangleCacheRoom=world.CurrentRoom;}
            triangleCache.Fill(destination,width,height,a.X,a.Y,b.X,b.Y,c.X,c.Y);
            DirtySurface(target);return;
        }
        float Edge(Vector2 p,Vector2 q,Vector2 point)=>(q.X-p.X)*(point.Y-p.Y)-(q.Y-p.Y)*(point.X-p.X);
        float area=Edge(a,b,c);if(area==0)return;
        if(area<0){(b,c)=(c,b);(cb,cc)=(cc,cb);(ub,uc)=(uc,ub);area=-area;}
        bool TopLeft(Vector2 p,Vector2 q)=>p.Y>q.Y||p.Y==q.Y&&p.X<q.X;
        bool ab=TopLeft(a,b),bc=TopLeft(b,c),ac=TopLeft(c,a);
        int left=Math.Max(0,(int)Math.Floor(Math.Min(a.X,Math.Min(b.X,c.X)))),right=Math.Min(width-1,(int)Math.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X))));
        int top=Math.Max(0,(int)Math.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y)))),bottom=Math.Min(height-1,(int)Math.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y))));
        (byte[] Data,int Width,int Height) source=default;
        if(texture is not null)source=TexturePixels(texture);
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {
            var point=new Vector2(x+0.5f,y+0.5f);float wa=Edge(b,c,point),wb=Edge(c,a,point),wc=Edge(a,b,point);
            if(wa<0||wa==0&&!bc||wb<0||wb==0&&!ac||wc<0||wc==0&&!ab)continue;
            wa/=area;wb/=area;wc/=area;
            var tint=ca*wa+cb*wb+cc*wc;
            if(texture is not null)
            {
                var uv=ua*wa+ub*wb+uc*wc;
                int sx=((int)MathF.Floor(uv.X*source.Width)%source.Width+source.Width)%source.Width,sy=((int)MathF.Floor(uv.Y*source.Height)%source.Height+source.Height)%source.Height;
                int offset=(sy*source.Width+sx)*4;
                var texels=source.Data!;tint*=new Color(texels[offset]/255f,texels[offset+1]/255f,texels[offset+2]/255f,texels[offset+3]/255f);
            }
            BlendPixel(destination,(y*width+x)*4,tint.R,tint.G,tint.B,tint.A,round);
        }
        DirtySurface(target);
    }
    private (byte[] Data,int Width,int Height) TexturePixels(Texture2D texture)
    {
        if(!textureData.TryGetValue(texture,out var data))
        {
            using var image=texture.GetImage();image.Convert(Image.Format.Rgba8);textureData[texture]=data=(image.GetData(),image.GetWidth(),image.GetHeight());
        }
        return data;
    }
    private void Primitive(Vector2[] points,Color[] colours,Vector2[] coords,Texture2D? texture,int mode)
    {
        if(mode==1){for(int i=0;i<points.Length;i++){var point=points[i];var tint=colours[i];if(target>=0)Textured(White,Vector2.Zero,new(0,0,1,1),point,Vector2.One,0,tint);else Add(c=>c.DrawRect(new(point,Vector2.One),tint));}return;}
        if(mode is 2 or 3)
        {
            for(int i=0;i+1<points.Length;i+=mode==2?2:1)
            {
                var a=points[i];var b=points[i+1];var tint=colours[i];
                if(target>=0)Textured(White,new(0,0.5f),new(0,0,1,1),a,new(a.DistanceTo(b),1),(b-a).Angle(),tint);
                else Add(c=>c.DrawLine(a,b,tint));
            }
            return;
        }
        var indices=GmPixels.Triangles(mode,points.Length);
        if(indices.Length==0)return;
        if(target>=0)
        {
            if(effects.TryGetValue(target,out var effect))
            {
                for(int n=0;n<indices.Length;n+=3)
                {
                    int a=indices[n],b=indices[n+1],c=indices[n+2];
                    effect.Triangle(texture,blend,points[a],points[b],points[c],colours[a],colours[b],colours[c],coords[a],coords[b],coords[c]);
                }
                return;
            }
            for(int i=0;i<indices.Length;i+=3)
            {
                int a=indices[i],b=indices[i+1],c=indices[i+2];
                bool fast=texture is null&&colours[a]==Colors.Black&&colours[b]==colours[a]&&colours[c]==colours[a]&&blend==0;
                long started=world.Performance.Start();
                try{Triangle(points[a],points[b],points[c],colours[a],colours[b],colours[c],coords[a],coords[b],coords[c],texture);}
                finally{world.Performance.End(fast?"surface_triangle_opaque":"surface_triangle_general",started);}
            }
        }
        else Add(c=>RenderingServer.CanvasItemAddTriangleArray(c.GetCanvasItem(),indices,points,colours,coords,texture:texture?.GetRid()??default));
    }
    public void Sprite(int id,double frame,Vector2 position,Vector2 scale,float angle,Color tint)
    {
        if(!world.Sprites.TryGetValue(id,out var s))return;
        if(target<0)SpriteAudit?.Add((id,frame,tint.A));
        var frames=s.GetProperty("frames");
        if(frames.GetArrayLength()==0)return;
        int f=((int)Math.Floor(frame)%frames.GetArrayLength()+frames.GetArrayLength())%frames.GetArrayLength();
        var t=Texture(frames[f].GetProperty("path").GetString()!);
        Textured(t,new(s.GetProperty("originX").GetInt32(),s.GetProperty("originY").GetInt32()),new Rect2(Vector2.Zero,t.GetSize()),position,scale,angle,tint);
    }
    public void Instance(GmInstance instance)
    {
        var s=instance.Scope;
        Sprite(s.ReadOr("sprite_index",-1).Int,s.ReadOr("image_index").Number,new((float)s.ReadOr("x").Number,(float)s.ReadOr("y").Number),new((float)s.ReadOr("image_xscale",1).Number,(float)s.ReadOr("image_yscale",1).Number),-Mathf.DegToRad((float)s.ReadOr("image_angle").Number),Colour(s.ReadOr("image_blend",16777215).Number,s.ReadOr("image_alpha",1).Number));
    }
    public async Task Audit()
    {
        var context=new GmContext(new GmInstance());
        var view=new SubViewport{Size=new(224,64),TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.Always};
        var canvas=new GmCanvas();view.AddChild(canvas);owner.AddChild(view);canvas.Paint=Render;
        int surface=Call("surface_create",[224,64],context)!.Value.Int;
        int[] sourceFactors=[5,5,9,5,2,7,9],destinationFactors=[6,2,1,2,1,8,2];
        for(int pass=0;pass<2;pass++)
        {
            Begin();target=pass==0?surface:-1;
            for(int mode=0;mode<7;mode++)
            {
                blend=0;alpha=1;colour=Colour(20|(180<<8)|(220<<16));
                Call("draw_rectangle",[mode*32,0,mode*32+32,32,0],context);
                if(mode==3)Call("draw_set_blend_mode",[3],context);
                else Call("draw_set_blend_mode_ext",[sourceFactors[mode],destinationFactors[mode]],context);
                colour=Colour(200|(50<<8)|(100<<16));alpha=0.5f;
                Call("draw_rectangle",[mode*32,0,mode*32+32,32,0],context);
            }
            for(int mode=4;mode<=6;mode++)
            {
                float x=(mode-4)*64;
                blend=0;alpha=1;colour=Colour(20|(180<<8)|(220<<16));Call("draw_rectangle",[x,32,x+32,64,0],context);
                Vector2[] points=mode switch{4=>[new(x,32),new(x+32,32),new(x+32,64),new(x,32),new(x+32,64),new(x,64)],5=>[new(x,32),new(x+32,32),new(x,64),new(x+32,64)],_=>[new(x,32),new(x+32,32),new(x+32,64),new(x,64)]};
                Primitive(points,Enumerable.Repeat(Colour(200|(50<<8)|(100<<16),0.5),points.Length).ToArray(),new Vector2[points.Length],null,mode);
            }
        }
        canvas.QueueRedraw();
        await AuditDrawFrames();
        using var image=view.GetTexture().GetImage();var cpu=SurfaceImage(surface);
        var expected=new[]{new Color(110/255f,115/255f,160/255f),new Color(120/255f,205/255f,1),new Color(200*20/65025f,50*180/65025f,100*220/65025f),new Color(0,155/255f,170/255f),new Color(200/255f,50/255f,100/255f),new Color(200/255f,50/255f,100/255f),new Color((200*20/255f+20)/255f,(50*180/255f+180)/255f,1)};
        var errors=new List<string>();
        for(int mode=0;mode<7;mode++)
        {
            var actual=image.GetPixel(mode*32+16,16);var software=cpu.GetPixel(mode*32+16,16);
            GD.Print("BLEND mode=",mode," gpu=",actual," cpu=",software," expected=",expected[mode]);
            float Distance(Color value)=>Math.Max(Math.Abs(value.R-expected[mode].R),Math.Max(Math.Abs(value.G-expected[mode].G),Math.Abs(value.B-expected[mode].B)));
            if(Distance(actual)>2/255f||Distance(software)>2/255f)errors.Add(mode.ToString());
        }
        for(int mode=4;mode<=6;mode++)
        {
            int differences=0;
            for(int y=34;y<62;y++)for(int x=(mode-4)*64+2;x<(mode-4)*64+30;x++)
            {
                var actual=image.GetPixel(x,y);var software=cpu.GetPixel(x,y);
                if(Math.Abs(actual.R-software.R)>2/255f||Math.Abs(actual.G-software.G)>2/255f||Math.Abs(actual.B-software.B)>2/255f)differences++;
                if(Math.Abs(software.R-expected[0].R)>2/255f)differences++;
            }
            GD.Print("TRIANGLE mode=",mode," differences=",differences);if(differences>0)errors.Add("triangle-"+mode);
        }
        var cachedTexture=SurfaceTexture(surface);target=surface;blend=4;colour=Colour(0x123456);alpha=1;Call("draw_rectangle",[0,0,4,4,0],context);
        SurfaceImage(surface);var refreshed=SurfaceTexture(surface);using var refreshedImage=refreshed.GetImage();
        if(Math.Abs(refreshedImage.GetPixel(1,1).R-0x56/255f)>1/255f)errors.Add("surface-refresh-after-capture");
        if(Colour(0xffffff,-1).A!=0||Colour(0xffffff,2).A!=1)errors.Add("opacity-clamp");
        int testSprite=CaptureSprite(SurfaceImage(surface),new Rect2I(0,0,4,4),0,false,0,0);
        int duplicate=world.Call("sprite_duplicate",[testSprite],context).Int;
        string generated=world.Sprites[testSprite].GetProperty("frames")[0].GetProperty("path").GetString()!;
        for(int iteration=0;iteration<4;iteration++)
        {
            var previous=Texture(generated);TexturePixels(previous);
            Call("sprite_set_alpha_from_sprite",[testSprite,duplicate],context);
            if(textureData.ContainsKey(previous))errors.Add("alpha-replacement-cache");
        }
        world.Call("sprite_delete",[testSprite],context);
        if(!textures.ContainsKey(generated))errors.Add("shared-sprite-texture-released");
        Begin();target=-1;blend=0;
        Sprite(duplicate,0,Vector2.Zero,Vector2.One,0,Colors.White);
        world.Call("sprite_delete",[duplicate],context);
        if(textures.ContainsKey(generated))errors.Add("unreferenced-sprite-texture-retained");
        canvas.QueueRedraw();
        await AuditDrawFrames();
        using var queuedImage=view.GetTexture().GetImage();
        if(queuedImage.GetPixel(1,1).A<=0)errors.Add("queued-sprite-texture-lifetime");
        view.QueueFree();Call("surface_free",[surface],context);target=-1;blend=0;alpha=1;
        if(errors.Count>0)throw new InvalidOperationException("Blend mismatch: "+string.Join(",",errors));
    }
    private async Task AuditDrawFrames(int count=2)
    {
        for(int frame=0;frame<count;frame++)
        {
            await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);
            RenderingServer.ForceDraw(false);
        }
    }
    private void Text(string text,Vector2 position,float scaleX=1,float scaleY=1,float rotation=0,float wrapWidth=-1,float lineSpacing=-1)
    {
        text=NativeLocalization.Text(text);
        var selected=world.Manifest.GetProperty("fonts").EnumerateArray().FirstOrDefault(f=>f.GetProperty("id").GetInt32()==font);
        if(selected.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("Font not selected "+font);
        var texture=Texture(selected.GetProperty("path").GetString()!);
        var map=selected.GetProperty("map");
        int height=selected.GetProperty("size").GetInt32();
        var tint=Tint;
        if(NativeLocalization.IsChinese&&text.Any(ch=>ch>=256)&&NativeLocalization.Font is {} chinese)
        {
            var camera=target<0?Camera:Vector2.Zero;
            IEnumerable<string> Lines()
            {
                foreach(string paragraph in text.Replace('#','\n').Split('\n'))
                {
                    if(wrapWidth<=0){yield return paragraph;continue;}
                    string line="";
                    foreach(char character in paragraph)
                    {
                        if(line.Length>0&&chinese.GetStringSize(line+character,HorizontalAlignment.Left,-1,height).X>wrapWidth){yield return line;line="";}
                        line+=character;
                    }
                    yield return line;
                }
            }
            foreach(string line in Lines())
            {
                string captured=line;Vector2 baseline=position-camera;
                float width=chinese.GetStringSize(line,HorizontalAlignment.Left,-1,height).X;
                float offset=halign==1?-width/2:halign==2?-width:0;
                Add(c=>{c.DrawSetTransform(baseline,rotation,new(scaleX,scaleY));c.DrawString(chinese,new(offset,chinese.GetAscent(height)),captured,HorizontalAlignment.Left,-1,height,tint);c.DrawSetTransform(Vector2.Zero);});
                position.Y+=(lineSpacing<0?height:lineSpacing)*scaleY;
            }
            return;
        }
        foreach(var line in text.Replace('#','\n').Split('\n'))
        {
            float width=0;
            foreach(char ch in line)if(ch<256)width+=unchecked((int)map[ch*6+4].GetUInt32());
            float x=halign==1?-width/2:halign==2?-width:0;
            foreach(char ch in line)
            {
                if(ch>=256)continue;
                int n=ch*6;
                var source=new Rect2(map[n].GetSingle(),map[n+1].GetSingle(),map[n+2].GetSingle(),map[n+3].GetSingle());
                var offset=new Vector2(x+unchecked((int)map[n+5].GetUInt32()),0);
                if(source.Size.X>0&&source.Size.Y>0)Textured(texture,-offset,source,position,new(scaleX,scaleY),rotation,tint);
                x+=unchecked((int)map[n+4].GetUInt32());
            }
            position.Y+=height;
        }
    }
    public GmValue? Call(string name,GmValue[] a,GmContext context)
    {
        double N(int i)=>i<a.Length?a[i].Number:0;
        int I(int i)=>i<a.Length?a[i].Int:0;
        float F(int i)=>(float)N(i);
        Vector2 P(int i)=>new(F(i),F(i+1));
        switch(name)
        {
            case "slack_delete_sprite":
            {
                using var deleted=JsonDocument.Parse(a[0].ToString());
                foreach(var frame in deleted.RootElement.GetProperty("frames").EnumerateArray())
                {
                    string path=frame.GetProperty("path").GetString()!;
                    if(path.StartsWith("generated/",StringComparison.Ordinal))ReleaseTexture(path);
                }
                return 0;
            }
            case "part_system_drawit":if(world.Particles.Systems.TryGetValue(I(0),out var system))DrawParticles(system);return 0;
            case "action_draw_ellipse":
            {
                var p1=P(0);var p2=P(2);
                if(context.Locals.ReadOr("argument_relative").Truth){var offset=new Vector2((float)context.Self.Scope.Read("x").Number,(float)context.Self.Scope.Read("y").Number);p1+=offset;p2+=offset;}
                return Call("draw_ellipse_color",[p1.X,p1.Y,p2.X,p2.Y,(int)(colour.R*255)|((int)(colour.G*255)<<8)|((int)(colour.B*255)<<16),0,I(4)],context);
            }
            case "slack_prepare_sprite":
            {
                var node=JsonNode.Parse(world.Sprites[I(0)].GetRawText())!;
                var masks=new List<object>();
                foreach(var frame in node["frames"]!.AsArray())
                {
                    var path=frame!["path"]!.GetValue<string>();using var image=Image.LoadFromFile(Path.Combine(world.Root,path));image.Convert(Image.Format.Rgba8);
                    int w=image.GetWidth(),h=image.GetHeight();var pixels=image.GetData();
                    GmPixels.Prepare(pixels,w,h,N(1)>=0.5?1:0,N(2)>=0.5);
                    using var prepared=Image.CreateFromData(w,h,false,Image.Format.Rgba8,pixels);
                    string generated="generated/"+Guid.NewGuid().ToString("N");textures[generated]=ImageTexture.CreateFromImage(prepared);frame["path"]=generated;
                    masks.Add(GmPixels.Mask(pixels,w,h));
                }
                node["masks"]=JsonSerializer.SerializeToNode(masks);world.Sprites[I(0)]=JsonSerializer.SerializeToElement(node);world.InvalidateCollisionSprites();return 0;
            }
            case "sprite_create_from_screen":
            {
                int width=(int)world.Engine.Read("view_wport",[0]).Number,height=(int)world.Engine.Read("view_hport",[0]).Number;
                if(width<=0||height<=0){width=world.Engine.Read("room_width").Int;height=world.Engine.Read("room_height").Int;}
                using var screen=DisplayServer.GetName()=="headless"?Image.CreateEmpty(width,height,false,Image.Format.Rgba8):owner.GetViewport().GetTexture().GetImage();
                if(screen.GetWidth()!=width||screen.GetHeight()!=height)screen.Resize(width,height,Image.Interpolation.Nearest);
                return CaptureSprite(screen,new(I(0),I(1),I(2),I(3)),I(4),N(5)>=0.5,I(6),I(7));
            }
            case "sprite_create_from_surface":
            case "sprite_add_from_surface":
            {
                bool append=name=="sprite_add_from_surface";int offset=append?1:0;
                return CaptureSprite(SurfaceImage(I(offset)),new(I(1+offset),I(2+offset),I(3+offset),I(4+offset)),I(5+offset),N(6+offset)>=0.5,append?0:I(7),append?0:I(8),append?I(0):null);
            }
            case "action_draw_variable":case "action_draw_text":Text(a[0].ToString(),P(1));return 0;
            case "action_draw_sprite":Sprite(I(0),N(3),P(1),Vector2.One,0,Colors.White);return 0;
            case "draw_background_part":
            {
                var b=world.Manifest.GetProperty("backgrounds").EnumerateArray().First(b=>b.GetProperty("id").GetInt32()==I(0));
                var texture=Texture(b.GetProperty("path").GetString()!);
                Textured(texture,Vector2.Zero,new Rect2(P(1),P(3)),P(5),Vector2.One,0,Colors.White);return 0;
            }
            case "draw_background_ext":case "draw_background_tiled":case "draw_background_tiled_ext":
            {
                var b=world.Manifest.GetProperty("backgrounds").EnumerateArray().First(b=>b.GetProperty("id").GetInt32()==I(0));
                var texture=Texture(b.GetProperty("path").GetString()!);
                var scale=name=="draw_background_tiled"?Vector2.One:P(3);
                var tint=name=="draw_background_tiled"?Colors.White:Colour(N(name=="draw_background_ext"?6:5),N(name=="draw_background_ext"?7:6));
                if(name=="draw_background_ext"){Textured(texture,Vector2.Zero,new(Vector2.Zero,texture.GetSize()),P(1),scale,-Mathf.DegToRad(F(5)),tint);return 0;}
                bool surfaceTarget=target>=0;long started=surfaceTarget?world.Performance.Start():0;
                try{Tiled(texture,P(1),scale,tint);}
                finally{if(surfaceTarget)world.Performance.End("surface_background_tiled",started);}
                return 0;
            }
            case "draw_set_blend_mode":blend=I(0) switch{0=>0,1=>1,3=>3,_=>throw new NotSupportedException("Blend mode "+I(0))};return 0;
            case "draw_set_blend_mode_ext":
                blend=(I(0),I(1)) switch{(5,6)=>0,(5,2)=>1,(1,3)=>2,(9,1)=>2,(2,1)=>4,(7,8)=>5,(9,2)=>6,_=>throw new NotSupportedException($"Blend factors {I(0)},{I(1)}")};return 0;
            case "draw_set_color":case "action_color":colour=Colour(N(0));return 0;
            case "draw_set_alpha":alpha=Math.Clamp(F(0),0,1);return 0;
            case "draw_set_font":font=I(0);return 0;
            case "draw_set_halign":halign=I(0);return 0;
            case "action_font":font=I(0);halign=I(1);return 0;
            case "draw_sprite":Sprite(I(0),N(1)<0?context.Self.Scope.ReadOr("image_index").Number:N(1),P(2),Vector2.One,0,Colors.White);return 0;
            case "draw_sprite_ext":Sprite(I(0),N(1)<0?context.Self.Scope.ReadOr("image_index").Number:N(1),P(2),P(4),-Mathf.DegToRad(F(6)),Colour(N(7),N(8)));return 0;
            case "draw_sprite_stretched":case "draw_sprite_stretched_ext":
            {
                var frames=world.Sprites[I(0)].GetProperty("frames");if(frames.GetArrayLength()==0)return 0;
                var texture=Texture(frames[0].GetProperty("path").GetString()!);
                Textured(texture,Vector2.Zero,new(Vector2.Zero,texture.GetSize()),P(2),P(4)/texture.GetSize(),0,name.EndsWith("_ext",StringComparison.Ordinal)?Colour(N(6),N(7)):Colors.White);return 0;
            }
            case "draw_sprite_part":case "draw_sprite_part_ext":
            {
                var frames=world.Sprites[I(0)].GetProperty("frames");if(frames.GetArrayLength()==0)return 0;
                int f=((int)Math.Floor(N(1))%frames.GetArrayLength()+frames.GetArrayLength())%frames.GetArrayLength();
                var texture=Texture(frames[f].GetProperty("path").GetString()!);
                Textured(texture,Vector2.Zero,new Rect2(P(2),P(4)),P(6),name.EndsWith("_ext",StringComparison.Ordinal)?P(8):Vector2.One,0,name.EndsWith("_ext",StringComparison.Ordinal)?Colour(N(10),N(11)):Colors.White);return 0;
            }
            case "draw_sprite_general":
            {
                var frames=world.Sprites[I(0)].GetProperty("frames");if(frames.GetArrayLength()==0)return 0;
                int f=((int)Math.Floor(N(1))%frames.GetArrayLength()+frames.GetArrayLength())%frames.GetArrayLength();
                var texture=Texture(frames[f].GetProperty("path").GetString()!);
                Textured(texture,Vector2.Zero,new Rect2(P(2),P(4)),P(6),P(8),-Mathf.DegToRad(F(10)),Colour(N(11),N(15)));return 0;
            }
            case "draw_text":Text(a[2].ToString(),P(0));return 0;
            case "draw_text_ext":Text(a[2].ToString(),P(0),wrapWidth:F(4),lineSpacing:F(3));return 0;
            case "draw_text_color":
            {var previousColour=colour;var opacity=alpha;colour=Colour(N(3));alpha=F(7);Text(a[2].ToString(),P(0));colour=previousColour;alpha=opacity;return 0;}
            case "draw_text_transformed_color":
            {var previousColour=colour;var opacity=alpha;colour=Colour(N(6));alpha=F(10);Text(a[2].ToString(),P(0),F(3),F(4),-Mathf.DegToRad(F(5)));colour=previousColour;alpha=opacity;return 0;}
            case "draw_rectangle":case "draw_rectangle_color":
            {
                var camera=target<0?Camera:Vector2.Zero;var p1=P(0)-camera;var p2=P(2)-camera;var tint=name=="draw_rectangle"?Tint:Colour(N(4),alpha);bool filled=N(name=="draw_rectangle"?4:8)<0.5;
                if(target>=0)
                {
                    var bounds=new Rect2(p1,p2-p1).Abs();
                    if(filled)Textured(White,Vector2.Zero,new(0,0,1,1),bounds.Position,bounds.Size,0,tint);
                    else
                    {
                        Textured(White,Vector2.Zero,new(0,0,1,1),bounds.Position,new(bounds.Size.X+1,1),0,tint);
                        Textured(White,Vector2.Zero,new(0,0,1,1),new(bounds.Position.X,bounds.End.Y),new(bounds.Size.X+1,1),0,tint);
                        Textured(White,Vector2.Zero,new(0,0,1,1),bounds.Position,new(1,bounds.Size.Y+1),0,tint);
                        Textured(White,Vector2.Zero,new(0,0,1,1),new(bounds.End.X,bounds.Position.Y),new(1,bounds.Size.Y+1),0,tint);
                    }
                    return 0;
                }
                Add(c=>c.DrawRect(new Rect2(p1,p2-p1).Abs(),tint,filled));return 0;
            }
            case "draw_line_width":case "draw_line_width_color":
            {var offset=target<0?Camera:Vector2.Zero;var p1=P(0)-offset;var p2=P(2)-offset;float width=F(4);var tint=name=="draw_line_width"?Tint:Colour(N(5),alpha);Add(c=>c.DrawLine(p1,p2,tint,width));return 0;}
            case "draw_point":case "draw_point_color":
            {var p=P(0)-(target<0?Camera:Vector2.Zero);var tint=name=="draw_point"?Tint:Colour(N(2),alpha);if(target>=0){SoftwareEffect(target);var image=surfacePixels[target];if(p.X>=0&&p.Y>=0&&p.X<image.GetWidth()&&p.Y<image.GetHeight()){int offset=((int)p.Y*image.GetWidth()+(int)p.X)*4;BlendPixel(surfaceData[target],offset,tint.R,tint.G,tint.B,tint.A);DirtySurface(target);}}else Add(c=>c.DrawRect(new Rect2(p,Vector2.One),tint));return 0;}
            case "draw_circle":case "draw_circle_color":
            {var p=P(0)-(target<0?Camera:Vector2.Zero);float radius=F(2);var tint=name=="draw_circle"?Tint:Colour(N(3),alpha);bool filled=N(name=="draw_circle"?3:5)<0.5;Add(c=>c.DrawCircle(p,radius,tint,filled));return 0;}
            case "draw_ellipse_color":
            {var p1=P(0)-(target<0?Camera:Vector2.Zero);var p2=P(2)-(target<0?Camera:Vector2.Zero);var centre=(p1+p2)/2;var radius=(p2-p1).Abs()/2;var tint=Colour(N(4),alpha);var points=Enumerable.Range(0,65).Select(i=>centre+new Vector2(Mathf.Cos(i*Mathf.Tau/64)*radius.X,Mathf.Sin(i*Mathf.Tau/64)*radius.Y)).ToArray();Add(c=>{if(N(6)<0.5)c.DrawColoredPolygon(points,tint);else c.DrawPolyline(points,tint);});return 0;}
            case "surface_create":
            {
                int id=nextSurface++;
                if(CreateEffect(id,Math.Max(1,I(0)),Math.Max(1,I(1)),context))return id;
                var image=Image.CreateEmpty(Math.Max(1,I(0)),Math.Max(1,I(1)),false,Image.Format.Rgba8);image.Fill(Colors.Black);surfacePixels[id]=image;surfaceData[id]=image.GetData();
                var view=new SubViewport{Size=new(Math.Max(1,I(0)),Math.Max(1,I(1))),TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.Disabled};
                var canvas=new GmCanvas();view.AddChild(canvas);owner.AddChild(view);surfaces[id]=(view,canvas);return id;
            }
            case "surface_exists":return surfaces.ContainsKey(I(0));
            case "surface_free":effects.Remove(I(0));effectInfo.Remove(I(0));triangleCache.Clear();if(surfaces.Remove(I(0),out var old))RetireSurface(old.View);if(surfacePixels.Remove(I(0),out var removed))removed.Dispose();surfaceData.Remove(I(0));dirtySurfaces.Remove(I(0));dirtySurfaceTextures.Remove(I(0));if(surfaceTextures.Remove(I(0),out var freed))textureData.Remove(freed);if(target==I(0))target=-1;return 0;
            case "surface_set_target":if(!surfaces.ContainsKey(I(0)))return false;target=I(0);return true;
            case "surface_reset_target":target=-1;return 0;
            case "surface_get_texture":return I(0);
            case "draw_surface":case "draw_surface_ext":
            {
                if(!surfaces.TryGetValue(I(0),out var s))return 0;
                if(target>=0&&!effects.ContainsKey(target))SoftwareEffect(I(0));
                var texture=SurfaceTexture(I(0));
                var logical=SurfaceSize(I(0));var actual=texture.GetSize();
                var surfaceScale=(name=="draw_surface_ext"?P(3):Vector2.One)*(effects.TryGetValue(I(0),out var sourceEffect)?new Vector2(sourceEffect.Divisor,sourceEffect.Divisor):Vector2.One);
                var sourceSize=effects.ContainsKey(I(0))?logical/(float)effects[I(0)].Divisor:actual;
                int savedFilter=drawFilter;drawFilter=effects.TryGetValue(I(0),out sourceEffect)&&sourceEffect.Divisor==4?2:1;
                Textured(texture,Vector2.Zero,new(Vector2.Zero,sourceSize),P(1),surfaceScale,name=="draw_surface_ext"?-Mathf.DegToRad(F(5)):0,name=="draw_surface_ext"?Colour(N(6),N(7)):Colors.White);drawFilter=savedFilter;return 0;
            }
            case "draw_clear_alpha":
                if(effects.TryGetValue(target,out var cleared)){cleared.Reset();var saved=blend;blend=4;Textured(White,Vector2.Zero,new(0,0,1,1),Vector2.Zero,cleared.Logical,0,Colour(N(0),N(1)));blend=saved;return 0;}
                if(target>=0&&surfaces.TryGetValue(target,out var surface)){surfacePixels[target].Fill(Colour(N(0),N(1)));surfaceData[target]=surfacePixels[target].GetData();DirtySurface(target);surface.Canvas.Commands.Clear();surface.Canvas.StartBatches();surface.View.TransparentBg=N(1)<0.5;surface.Canvas.QueueRedraw();}return 0;
            case "sprite_set_alpha_from_sprite":
            {
                var destination=world.Sprites[I(0)].GetProperty("frames");var source=world.Sprites[I(1)].GetProperty("frames");
                for(int f=0;f<destination.GetArrayLength();f++)
                {
                    var path=destination[f].GetProperty("path").GetString()!;
                    var previous=Texture(path);
                    using var image=previous.GetImage();using var mask=Texture(source[f%source.GetArrayLength()].GetProperty("path").GetString()!).GetImage();
                    for(int y=0;y<image.GetHeight();y++)for(int x=0;x<image.GetWidth();x++)
                    {
                        var pixel=image.GetPixel(x,y);var m=mask.GetPixel(x%mask.GetWidth(),y%mask.GetHeight());pixel.A=(m.R+m.G+m.B)/3;image.SetPixel(x,y,pixel);
                    }
                    textures[path]=ImageTexture.CreateFromImage(image);
                    textureData.Remove(previous);
                }
                return 0;
            }
            case "draw_primitive_begin":case "draw_primitive_begin_texture":primitive=I(0);vertices.Clear();uvs.Clear();vertexColours.Clear();if(target>=0&&!effects.ContainsKey(target)&&name.EndsWith("_texture",StringComparison.Ordinal))SoftwareEffect(I(1));primitiveTexture=name.EndsWith("_texture",StringComparison.Ordinal)&&surfaces.ContainsKey(I(1))?SurfaceTexture(I(1)):null;return 0;
            case "draw_vertex":case "draw_vertex_texture_color":vertices.Add(P(0)-(target<0?Camera:Vector2.Zero));uvs.Add(name=="draw_vertex"?Vector2.Zero:P(2));vertexColours.Add(name=="draw_vertex"?Tint:Colour(N(4),N(5)));return 0;
            case "draw_primitive_end":
            {
                if(effects.TryGetValue(target,out var effect)&&primitive is 4 or 5 or 6)
                {
                    void Emit(int a,int b,int c)=>effect.Triangle(primitiveTexture,blend,vertices[a],vertices[b],vertices[c],vertexColours[a],vertexColours[b],vertexColours[c],uvs[a],uvs[b],uvs[c]);
                    if(primitive==4){for(int n=0;n+2<vertices.Count;n+=3)Emit(n,n+1,n+2);}
                    else if(primitive==5){for(int n=0;n+2<vertices.Count;n++)if(n%2==0)Emit(n,n+1,n+2);else Emit(n+1,n,n+2);}
                    else{for(int n=1;n+1<vertices.Count;n++)Emit(0,n,n+1);}
                }
                else Primitive(vertices.ToArray(),vertexColours.ToArray(),uvs.ToArray(),primitiveTexture,primitive);
                return 0;
            }
        }
        return null;
    }
}

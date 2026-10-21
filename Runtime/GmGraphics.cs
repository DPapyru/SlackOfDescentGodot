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
    public void StartBatches(){batchIndex=0;lastBlend=-1;foreach(var batch in batches){batch.Visible=false;batch.Commands.Clear();if(batch.screenCopy is not null)batch.screenCopy.Visible=false;}}
    public void Batch(Action<GmCanvas> command,int blend)
    {
        if(batchIndex>0&&lastBlend==blend&&blend!=6){batches[batchIndex-1].Commands.Add(command);batches[batchIndex-1].QueueRedraw();return;}
        lastBlend=blend;
        GmCanvas batch;
        if(batchIndex<batches.Count)batch=batches[batchIndex];
        else{batch=new GmCanvas();batches.Add(batch);AddChild(batch);}
        batchIndex++;batch.Visible=true;
        if(!materials.TryGetValue(blend,out var material))
        {
            if(blend==6)
            {
                var shader=new Shader{Code="shader_type canvas_item; render_mode unshaded, blend_disabled; uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest; varying vec4 vertex_tint; void vertex() { vertex_tint = COLOR; } void fragment() { vec4 source = vertex_tint * texture(TEXTURE, UV); vec4 destination = texture(screen_texture, SCREEN_UV); COLOR = source * destination + destination; }"};
                material=new ShaderMaterial{Shader=shader};
            }
            else material=new CanvasItemMaterial{BlendMode=blend switch{1=>CanvasItemMaterial.BlendModeEnum.Add,2=>CanvasItemMaterial.BlendModeEnum.Mul,3=>CanvasItemMaterial.BlendModeEnum.Sub,4=>CanvasItemMaterial.BlendModeEnum.PremultAlpha,_=>CanvasItemMaterial.BlendModeEnum.Mix}};
            materials[blend]=material;
        }
        if(blend==6)
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

public sealed class GmGraphics
{
    private readonly Node owner;
    private readonly GmWorld world;
    private readonly Dictionary<string,Texture2D> textures=new();
    private readonly Dictionary<int,(SubViewport View,GmCanvas Canvas)> surfaces=new();
    private readonly Dictionary<int,Image> surfacePixels=new();
    private readonly List<Action<GmCanvas>> main=new();
    private int nextSurface=1;
    private int target=-1;
    private Color colour=Colors.White;
    private float alpha=1;
    private int font=-1;
    private int halign;
    private int blend;
    public Vector2 Camera;
    private readonly List<Vector2> vertices=new();
    private readonly List<Vector2> uvs=new();
    private readonly List<Color> vertexColours=new();
    private int primitive;
    private Texture2D? primitiveTexture;

    public GmGraphics(Node owner,GmWorld world){this.owner=owner;this.world=world;}
    public static Color Colour(double value,double opacity=1)
    {
        uint n=(uint)value;
        return new Color((n&255)/255f,((n>>8)&255)/255f,((n>>16)&255)/255f,(float)opacity);
    }
    private Color Tint=>new(colour.R,colour.G,colour.B,alpha);
    private void Add(Action<GmCanvas> command)
    {
        int mode=blend;
        Action<GmCanvas> draw=c=>c.Batch(command,mode);
        if(target<0)main.Add(draw);
        else if(surfaces.TryGetValue(target,out var surface)){surface.Canvas.Commands.Add(draw);surface.Canvas.QueueRedraw();}
    }
    public void Begin(){main.Clear();}
    public void Render(GmCanvas canvas){canvas.StartBatches();foreach(var command in main)command(canvas);}
    public void Build()
    {
        Begin();
        Camera=world.Engine.ReadOr("view_enabled").Truth?new((float)world.Engine.Read("view_xview",[0]).Number,(float)world.Engine.Read("view_yview",[0]).Number):Vector2.Zero;
        var draws=new List<(double Depth,Action Draw)>();
        foreach(var instance in world.Instances.Where(i=>!i.Destroyed&&i.Active&&i.Scope.ReadOr("visible").Truth).ToArray())
            draws.Add((instance.Scope.ReadOr("depth").Number,()=>{if(!instance.Destroyed&&!world.Event(instance,8,0))Instance(instance);}));
        foreach(var tile in world.Tiles.Where(t=>!world.HiddenTileLayers.Contains(t.GetProperty("depth").GetInt32())).ToArray())
            draws.Add((tile.GetProperty("depth").GetDouble(),()=>
            {
                var background=world.Manifest.GetProperty("backgrounds").EnumerateArray().First(b=>b.GetProperty("id").GetInt32()==tile.GetProperty("background").GetInt32());
                var texture=Texture(background.GetProperty("path").GetString()!);
                Textured(texture,Vector2.Zero,new((float)tile.GetProperty("sourceX").GetDouble(),(float)tile.GetProperty("sourceY").GetDouble(),(float)tile.GetProperty("width").GetDouble(),(float)tile.GetProperty("height").GetDouble()),new((float)tile.GetProperty("x").GetDouble(),(float)tile.GetProperty("y").GetDouble()),new((float)tile.GetProperty("xscale").GetDouble(),(float)tile.GetProperty("yscale").GetDouble()),0,Colour(tile.GetProperty("blend").GetDouble()));
            }));
        foreach(var draw in draws.OrderByDescending(d=>d.Depth))draw.Draw();
    }
    private Texture2D Texture(string path)
    {
        if(textures.TryGetValue(path,out var texture))return texture;
        return textures[path]=ImageTexture.CreateFromImage(Image.LoadFromFile(Path.Combine(world.Root,path)));
    }
    private void Textured(Texture2D texture,Vector2 origin,Rect2 source,Vector2 position,Vector2 scale,float angle,Color tint)
    {
        if(target>=0&&surfacePixels.TryGetValue(target,out var pixels))
        {
            Composite(pixels,texture.GetImage(),origin,source,position,scale,angle,tint);return;
        }
        var camera=target<0?Camera:Vector2.Zero;
        Add(c=>{c.DrawSetTransform(position-camera,angle,scale);c.DrawTextureRectRegion(texture,new Rect2(-origin,source.Size),source,tint);c.DrawSetTransform(Vector2.Zero);});
    }
    private void Composite(Image destination,Image image,Vector2 origin,Rect2 source,Vector2 position,Vector2 scale,float angle,Color tint)
    {
        if(scale.X==0||scale.Y==0)return;
        var transform=new Transform2D(angle,scale,0,position);
        var inverse=transform.AffineInverse();
        var corners=new[]{-origin,new Vector2(source.Size.X,0)-origin,source.Size-origin,new Vector2(0,source.Size.Y)-origin}.Select(p=>transform*p).ToArray();
        int left=Math.Max(0,(int)Math.Floor(corners.Min(p=>p.X))),right=Math.Min(destination.GetWidth()-1,(int)Math.Ceiling(corners.Max(p=>p.X)));
        int top=Math.Max(0,(int)Math.Floor(corners.Min(p=>p.Y))),bottom=Math.Min(destination.GetHeight()-1,(int)Math.Ceiling(corners.Max(p=>p.Y)));
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {
            var local=inverse*new Vector2(x+0.5f,y+0.5f)+origin;
            if(local.X<0||local.Y<0||local.X>=source.Size.X||local.Y>=source.Size.Y)continue;
            int sx=(int)Math.Floor(source.Position.X+local.X),sy=(int)Math.Floor(source.Position.Y+local.Y);
            if(sx<0||sy<0||sx>=image.GetWidth()||sy>=image.GetHeight())continue;
            var s=image.GetPixel(sx,sy)*tint;var d=destination.GetPixel(x,y);
            var result=blend switch
            {
                0=>s*s.A+d*(1-s.A),1=>s*s.A+d,2=>s*d,3=>d-s*s.A,4=>s,5=>s*d.A+d*(1-d.A),
                6=>s*d+d,_=>throw new NotSupportedException("Surface blend "+blend)
            };
            destination.SetPixel(x,y,result.Clamp());
        }
    }
    public void Sprite(int id,double frame,Vector2 position,Vector2 scale,float angle,Color tint)
    {
        if(!world.Sprites.TryGetValue(id,out var s))return;
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
    private void Text(string text,Vector2 position,float scaleX=1,float scaleY=1,float rotation=0)
    {
        var selected=world.Manifest.GetProperty("fonts").EnumerateArray().FirstOrDefault(f=>f.GetProperty("id").GetInt32()==font);
        if(selected.ValueKind==JsonValueKind.Undefined)throw new InvalidOperationException("Font not selected "+font);
        var texture=Texture(selected.GetProperty("path").GetString()!);
        var map=selected.GetProperty("map");
        int height=selected.GetProperty("size").GetInt32();
        var tint=Tint;
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
                var camera=target<0?Camera:Vector2.Zero;
                var p=position-camera;
                if(source.Size.X>0&&source.Size.Y>0)Add(c=>{c.DrawSetTransform(p,rotation,new(scaleX,scaleY));c.DrawTextureRectRegion(texture,new Rect2(offset,source.Size),source,tint);c.DrawSetTransform(Vector2.Zero);});
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
                    var path=frame!["path"]!.GetValue<string>();var image=Texture(path).GetImage();image.Convert(Image.Format.Rgba8);
                    int w=image.GetWidth(),h=image.GetHeight(),l=w,r=-1,t=h,b=-1;
                    var key=image.GetPixel(0,h-1);var data=new char[w*h];
                    for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                    {
                        var pixel=image.GetPixel(x,y);
                        if(N(1)>=0.5&&pixel.R==key.R&&pixel.G==key.G&&pixel.B==key.B){pixel.A=0;image.SetPixel(x,y,pixel);}
                        bool opaque=pixel.A>0;data[y*w+x]=opaque?'1':'0';
                        if(opaque){l=Math.Min(l,x);r=Math.Max(r,x);t=Math.Min(t,y);b=Math.Max(b,y);}
                    }
                    textures[path]=ImageTexture.CreateFromImage(image);
                    masks.Add(new{width=w,height=h,left=l,right=r,top=t,bottom=b,data=new string(data)});
                }
                node["masks"]=JsonSerializer.SerializeToNode(masks);world.Sprites[I(0)]=JsonSerializer.SerializeToElement(node);return 0;
            }
            case "sprite_create_from_surface":
            case "sprite_add_from_surface":
            {
                bool append=name=="sprite_add_from_surface";int offset=append?1:0;
                int w=I(3+offset),h=I(4+offset);
                var image=surfacePixels[I(offset)].GetRegion(new Rect2I(I(1+offset),I(2+offset),w,h));
                var path="generated/"+Guid.NewGuid().ToString("N");textures[path]=ImageTexture.CreateFromImage(image);
                if(append)
                {
                    var node=JsonNode.Parse(world.Sprites[I(0)].GetRawText())!;
                    node["frames"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{path,width=w,height=h}));
                    world.Sprites[I(0)]=JsonSerializer.SerializeToElement(node);return I(0);
                }
                return world.RegisterSprite(JsonSerializer.SerializeToElement(new{originX=I(7),originY=I(8),frames=new[]{new{path,width=w,height=h}},masks=Array.Empty<object>()}));
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
                var size=texture.GetSize()*scale;if(size.X<=0||size.Y<=0)return 0;
                var start=P(1);var bounds=target<0?new Vector2((float)world.Engine.Read("room_width").Number,(float)world.Engine.Read("room_height").Number):surfaces[target].View.Size;
                for(float y=start.Y%size.Y-size.Y;y<bounds.Y;y+=size.Y)for(float x=start.X%size.X-size.X;x<bounds.X;x+=size.X)Textured(texture,Vector2.Zero,new(Vector2.Zero,texture.GetSize()),new(x,y),scale,0,tint);
                return 0;
            }
            case "draw_set_blend_mode":blend=I(0) switch{0=>0,1=>1,3=>3,_=>throw new NotSupportedException("Blend mode "+I(0))};return 0;
            case "draw_set_blend_mode_ext":
                blend=(I(0),I(1)) switch{(5,6)=>0,(5,2)=>1,(1,3)=>2,(9,1)=>2,(2,1)=>4,(7,8)=>5,(9,2)=>6,_=>throw new NotSupportedException($"Blend factors {I(0)},{I(1)}")};return 0;
            case "draw_set_color":case "action_color":colour=Colour(N(0));return 0;
            case "draw_set_alpha":alpha=F(0);return 0;
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
            case "draw_text_ext":Text(a[2].ToString(),P(0));return 0;
            case "draw_text_color":
            {var previousColour=colour;var opacity=alpha;colour=Colour(N(3));alpha=F(7);Text(a[2].ToString(),P(0));colour=previousColour;alpha=opacity;return 0;}
            case "draw_text_transformed_color":
            {var previousColour=colour;var opacity=alpha;colour=Colour(N(6));alpha=F(10);Text(a[2].ToString(),P(0),F(3),F(4),-Mathf.DegToRad(F(5)));colour=previousColour;alpha=opacity;return 0;}
            case "draw_rectangle":case "draw_rectangle_color":
            {
                var camera=target<0?Camera:Vector2.Zero;var p1=P(0)-camera;var p2=P(2)-camera;var tint=name=="draw_rectangle"?Tint:Colour(N(4),alpha);bool filled=N(name=="draw_rectangle"?4:8)<0.5;
                Add(c=>c.DrawRect(new Rect2(p1,p2-p1).Abs(),tint,filled));return 0;
            }
            case "draw_line_width":case "draw_line_width_color":
            {var offset=target<0?Camera:Vector2.Zero;var p1=P(0)-offset;var p2=P(2)-offset;float width=F(4);var tint=name=="draw_line_width"?Tint:Colour(N(5),alpha);Add(c=>c.DrawLine(p1,p2,tint,width));return 0;}
            case "draw_point":case "draw_point_color":
            {var p=P(0)-(target<0?Camera:Vector2.Zero);var tint=name=="draw_point"?Tint:Colour(N(2),alpha);if(target>=0){var image=surfacePixels[target];if(p.X>=0&&p.Y>=0&&p.X<image.GetWidth()&&p.Y<image.GetHeight())image.SetPixel((int)p.X,(int)p.Y,tint);}else Add(c=>c.DrawRect(new Rect2(p,Vector2.One),tint));return 0;}
            case "draw_circle":case "draw_circle_color":
            {var p=P(0)-(target<0?Camera:Vector2.Zero);float radius=F(2);var tint=name=="draw_circle"?Tint:Colour(N(3),alpha);bool filled=N(name=="draw_circle"?3:5)<0.5;Add(c=>c.DrawCircle(p,radius,tint,filled));return 0;}
            case "draw_ellipse_color":
            {var p1=P(0)-(target<0?Camera:Vector2.Zero);var p2=P(2)-(target<0?Camera:Vector2.Zero);var centre=(p1+p2)/2;var radius=(p2-p1).Abs()/2;var tint=Colour(N(4),alpha);var points=Enumerable.Range(0,65).Select(i=>centre+new Vector2(Mathf.Cos(i*Mathf.Tau/64)*radius.X,Mathf.Sin(i*Mathf.Tau/64)*radius.Y)).ToArray();Add(c=>{if(N(6)<0.5)c.DrawColoredPolygon(points,tint);else c.DrawPolyline(points,tint);});return 0;}
            case "surface_create":
            {
                int id=nextSurface++;
                var image=Image.CreateEmpty(Math.Max(1,I(0)),Math.Max(1,I(1)),false,Image.Format.Rgba8);image.Fill(Colors.Black);surfacePixels[id]=image;
                var view=new SubViewport{Size=new(Math.Max(1,I(0)),Math.Max(1,I(1))),TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.Always};
                var canvas=new GmCanvas();view.AddChild(canvas);owner.AddChild(view);surfaces[id]=(view,canvas);return id;
            }
            case "surface_exists":return surfaces.ContainsKey(I(0));
            case "surface_free":if(surfaces.Remove(I(0),out var old))old.View.QueueFree();surfacePixels.Remove(I(0));return 0;
            case "surface_set_target":if(!surfaces.ContainsKey(I(0)))return false;target=I(0);return true;
            case "surface_reset_target":target=-1;return 0;
            case "surface_get_texture":return I(0);
            case "draw_surface":case "draw_surface_ext":
            {
                if(!surfaces.TryGetValue(I(0),out var s))return 0;
                var texture=ImageTexture.CreateFromImage(surfacePixels[I(0)]);
                Textured(texture,Vector2.Zero,new(Vector2.Zero,s.View.Size),P(1),name=="draw_surface_ext"?P(3):Vector2.One,name=="draw_surface_ext"?-Mathf.DegToRad(F(5)):0,name=="draw_surface_ext"?Colour(N(6),N(7)):Colors.White);return 0;
            }
            case "draw_clear_alpha":
                if(target>=0&&surfaces.TryGetValue(target,out var surface)){surfacePixels[target].Fill(Colour(N(0),N(1)));surface.Canvas.Commands.Clear();surface.Canvas.StartBatches();surface.View.TransparentBg=N(1)<0.5;surface.Canvas.QueueRedraw();}return 0;
            case "sprite_set_alpha_from_sprite":
            {
                var destination=world.Sprites[I(0)].GetProperty("frames");var source=world.Sprites[I(1)].GetProperty("frames");
                for(int f=0;f<destination.GetArrayLength();f++)
                {
                    var path=destination[f].GetProperty("path").GetString()!;
                    var image=Texture(path).GetImage();var mask=Texture(source[f%source.GetArrayLength()].GetProperty("path").GetString()!).GetImage();
                    for(int y=0;y<image.GetHeight();y++)for(int x=0;x<image.GetWidth();x++)
                    {
                        var pixel=image.GetPixel(x,y);var m=mask.GetPixel(x%mask.GetWidth(),y%mask.GetHeight());pixel.A=(m.R+m.G+m.B)/3;image.SetPixel(x,y,pixel);
                    }
                    textures[path]=ImageTexture.CreateFromImage(image);
                }
                return 0;
            }
            case "draw_primitive_begin":case "draw_primitive_begin_texture":primitive=I(0);vertices.Clear();uvs.Clear();vertexColours.Clear();primitiveTexture=name.EndsWith("_texture",StringComparison.Ordinal)&&surfaces.TryGetValue(I(1),out var st)?st.View.GetTexture():null;return 0;
            case "draw_vertex":case "draw_vertex_texture_color":vertices.Add(P(0)-(target<0?Camera:Vector2.Zero));uvs.Add(name=="draw_vertex"?Vector2.Zero:P(2));vertexColours.Add(name=="draw_vertex"?Tint:Colour(N(4),N(5)));return 0;
            case "draw_primitive_end":
            {var points=vertices.ToArray();var colours=vertexColours.ToArray();var coords=uvs.ToArray();var texture=primitiveTexture;var mode=primitive;if(points.Length>=3)Add(c=>{if(mode is 2 or 3)c.DrawPolylineColors(points,colours);else c.DrawPolygon(points,colours,coords,texture);});return 0;}
        }
        return null;
    }
}

using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlackOfDescent;

public sealed partial class GmGraphics
{
    internal List<(int Sprite,double Frame,float Alpha)>? SpriteAudit;
    internal List<(string Text,Vector2 Position,Vector2 Scale)>? TextAudit;
    internal List<(int Sprite,Vector2 Position,Vector2 Scale)>? SpriteGeometryAudit;
    partial void ClearDrawAudit()=>SpriteAudit?.Clear();
    partial void RecordSpriteAudit(int id,double frame,float alpha,Vector2 position,Vector2 scale){SpriteAudit?.Add((id,frame,alpha));SpriteGeometryAudit?.Add((id,position,scale));}
    partial void RecordTextAudit(string text,Vector2 position,Vector2 scale)=>TextAudit?.Add((text,position,scale));
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
}

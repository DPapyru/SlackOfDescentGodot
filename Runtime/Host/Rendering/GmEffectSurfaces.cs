using Godot;

namespace SlackOfDescent;

public sealed partial class GmGraphics
{
    private readonly Dictionary<int,EffectSurface> effects=new();
    private readonly Dictionary<int,(string Kind,Vector2 Logical,int Divisor)> effectInfo=new();
    private int effectFallbacks;
    private int drawFilter;
    private readonly List<SubViewport> retiredSurfaces=new();
    private void RetireSurface(SubViewport view)
    {
        retiredSurfaces.Add(view);
        if(retiredSurfaces.Count==1)RenderingServer.FramePostDraw+=ReleaseRetiredSurfaces;
    }
    private void ReleaseRetiredSurfaces()
    {
        if(retiredSurfaces.Count==0)return;
        RenderingServer.FramePostDraw-=ReleaseRetiredSurfaces;
        foreach(var view in retiredSurfaces)if(GodotObject.IsInstanceValid(view))view.QueueFree();
        retiredSurfaces.Clear();
    }
    private Vector2 SurfaceSize(int id)=>effectInfo.TryGetValue(id,out var info)?info.Logical:surfaces[id].View.Size;
    private object EffectStatistics()=>effectInfo.Select(p=>new{id=p.Key,backend=effects.ContainsKey(p.Key)?"gpu":"cpu",kind=p.Value.Kind,
        logicalWidth=(int)p.Value.Logical.X,logicalHeight=(int)p.Value.Logical.Y,
        actualWidth=effects.ContainsKey(p.Key)?surfaces[p.Key].View.Size.X:(int)p.Value.Logical.X,
        actualHeight=effects.ContainsKey(p.Key)?surfaces[p.Key].View.Size.Y:(int)p.Value.Logical.Y,
        batches=effects.TryGetValue(p.Key,out var effect)?effect.BatchCount:0,triangles=effect?.TriangleCount??0}).ToArray();

    private bool CreateEffect(int id,int width,int height,GmContext context)
    {
        string kind=world.Objects.TryGetValue(context.Self.Object,out var obj)?obj.GetProperty("name").GetString()!:"";
        if(kind is not ("objRainController" or "objDark4"))return false;
        int divisor=kind=="objDark4"?4:1;
        effectInfo[id]=(kind,new(width,height),divisor);
        if(DisplayServer.GetName()=="headless")return false;
        var view=new SubViewport{Size=new((width+divisor-1)/divisor,(height+divisor-1)/divisor),TransparentBg=true,Disable3D=true,
            RenderTargetUpdateMode=SubViewport.UpdateMode.Disabled,RenderTargetClearMode=SubViewport.ClearMode.Always};
        var canvas=new GmCanvas{TextureFilter=CanvasItem.TextureFilterEnum.Nearest};
        view.AddChild(canvas);owner.AddChild(view);surfaces[id]=(view,canvas);
        var effect=new EffectSurface(kind,new(width,height),divisor,view,canvas,world.Performance);
        effects[id]=effect;canvas.Paint=effect.Paint;
        return true;
    }

    private void SoftwareEffect(int id)
    {
        if(!effects.Remove(id,out var effect))return;
        effectFallbacks++;
        int savedTarget=target,savedBlend=blend;
        var image=Image.CreateEmpty((int)effect.Logical.X,(int)effect.Logical.Y,false,Image.Format.Rgba8);image.Fill(Colors.Black);
        surfacePixels[id]=image;surfaceData[id]=image.GetData();target=id;
        try
        {
            foreach(var batch in effect.CurrentBatches)
            {
                blend=batch.Blend;
                for(int n=0;n<batch.Count;n+=3)
                    Triangle(batch.Points[n],batch.Points[n+1],batch.Points[n+2],batch.Colours[n],batch.Colours[n+1],batch.Colours[n+2],batch.Uvs[n],batch.Uvs[n+1],batch.Uvs[n+2],batch.Texture);
            }
        }
        finally{target=savedTarget;blend=savedBlend;}
        effect.Schedule();
        DirtySurface(id);
    }

    public void Shutdown()
    {
        foreach(int id in surfaces.Keys.ToArray())Call("surface_free",[id],new(new GmInstance()));
        ReleaseRetiredSurfaces();
    }

    public async Task AuditEffects()
    {
        if(DisplayServer.GetName()=="headless")throw new InvalidOperationException("Effects audit requires a graphics display");
        var context=new GmContext(new GmInstance());
        var errors=new List<string>();
        using var pattern=Image.CreateFromData(2,2,false,Image.Format.Rgba8,[80,120,180,80,160,40,70,120,20,200,80,160,200,80,20,200]);
        var texture=ImageTexture.CreateFromImage(pattern);
        var output=new SubViewport{Size=new(848,480),TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.Always};
        var outputCanvas=new GmCanvas();output.AddChild(outputCanvas);owner.AddChild(output);outputCanvas.Paint=Render;
        foreach(string kind in new[]{"objRainController","objDark4"})foreach(var size in new[]{new Vector2I(640,480),new Vector2I(848,477)})
        {
            context.Self.Object=world.Constants[kind].Int;
            int id=Call("surface_create",[size.X,size.Y],context)!.Value.Int;
            var effect=effects[id];
            for(int version=0;version<3;version++)
            {
                target=id;blend=0;colour=version==2?Colour(0x303030):Colors.White;alpha=1;
                Call("draw_rectangle",[0,0,size.X,size.Y,0],context);
                blend=kind=="objDark4"?1:0;
                for(int repeat=0;repeat<3;repeat++)Textured(texture,Vector2.Zero,new(0,0,2,2),new(8,8),new(24,24),0,Colors.White);
                blend=0;
                Primitive([new(80,0),new(70,160),new(120,160),new(100,0)],[Colors.Black,Colors.Black,Colors.Black,Colors.Black],[Vector2.Zero,Vector2.Zero,Vector2.Zero,Vector2.Zero],null,6);
            }
            target=-1;blend=0;Begin();Camera=Vector2.Zero;viewSize=new(size.X,size.Y);
            Call("draw_surface",[id,0,0],context);outputCanvas.QueueRedraw();
            await AuditDrawFrames(1);
            using var actual=effect.View.GetTexture().GetImage();
            int divisor=effect.Divisor;
            int expectedWidth=(size.X+divisor-1)/divisor,expectedHeight=(size.Y+divisor-1)/divisor;
            if(actual.GetWidth()!=expectedWidth||actual.GetHeight()!=expectedHeight)errors.Add(kind+"-size");
            int reference=nextSurface++;
            var referenceImage=Image.CreateEmpty(expectedWidth,expectedHeight,false,Image.Format.Rgba8);referenceImage.Fill(Colors.Black);
            surfacePixels[reference]=referenceImage;surfaceData[reference]=referenceImage.GetData();target=reference;
            foreach(var batch in effect.CurrentBatches)
            {
                blend=batch.Blend;
                for(int n=0;n<batch.Count;n+=3)
                    Triangle(batch.Points[n]/divisor,batch.Points[n+1]/divisor,batch.Points[n+2]/divisor,batch.Colours[n],batch.Colours[n+1],batch.Colours[n+2],batch.Uvs[n],batch.Uvs[n+1],batch.Uvs[n+2],batch.Texture,round:divisor==4);
            }
            var expected=SurfaceImage(reference);int differences=0;
            for(int y=0;y<expectedHeight;y++)for(int x=0;x<expectedWidth;x++)
            {
                var a=actual.GetPixel(x,y);var b=expected.GetPixel(x,y);
                if(Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))>2/255f){if(differences==0)GD.Print("EFFECT_PIXEL ",kind," at=",new Vector2I(x,y)," gpu=",a," cpu=",b);differences++;}
            }
            if(differences>0)errors.Add(kind+"-pixels:"+differences);
            using var composed=output.GetTexture().GetImage();
            if(composed.GetPixel(size.X-1,size.Y-1).A<0.99f)errors.Add(kind+"-coverage");
            if(divisor==4)
            {
                int upscaleDifferences=0;
                for(int y=0;y<size.Y;y++)for(int x=0;x<size.X;x++)
                {
                    float sx=(x+0.5f)/4-0.5f,sy=(y+0.5f)/4-0.5f;
                    int ix=(int)MathF.Floor(sx),iy=(int)MathF.Floor(sy);
                    Color Sample(int px,int py)=>expected.GetPixel(Math.Clamp(px,0,expectedWidth-1),Math.Clamp(py,0,expectedHeight-1));
                    var top=Sample(ix,iy).Lerp(Sample(ix+1,iy),sx-ix);
                    var bottom=Sample(ix,iy+1).Lerp(Sample(ix+1,iy+1),sx-ix);
                    var b=top.Lerp(bottom,sy-iy);var a=composed.GetPixel(x,y);
                    if(Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))>2/255f)upscaleDifferences++;
                }
                if(upscaleDifferences>0)errors.Add(kind+"-linear-upscale:"+upscaleDifferences);
            }
            GD.Print("EFFECT_AUDIT ",kind," logical=",size," actual=",actual.GetSize()," differences=",differences," batches=",effect.BatchCount," triangles=",effect.TriangleCount);
            target=-1;blend=0;
            int replaySeed=world.Random.Seed;
            if(size.X==640)
            {
                int captured=Call("sprite_create_from_surface",[id,0,0,8,8,0,0,0,0],context)!.Value.Int;
                world.Call("sprite_delete",[captured],context);
            }
            else
            {
                context.Self.Object=-1;
                int software=Call("surface_create",[size.X,size.Y],context)!.Value.Int;
                target=software;Call("draw_surface",[id,0,0],context);target=-1;
                if(SurfaceImage(software).GetPixel(size.X-1,size.Y-1).R<0.18f)errors.Add(kind+"-software-target-content");
                Call("surface_free",[software],context);
            }
            if(world.Random.Seed!=replaySeed)errors.Add(kind+"-replay-random");
            if(effects.ContainsKey(id)||SurfaceImage(id).GetWidth()!=size.X)errors.Add(kind+"-fallback");
            if(SurfaceImage(id).GetPixel(size.X-1,size.Y-1).R<0.18f)errors.Add(kind+"-fallback-content");
            Call("surface_free",[reference],context);Call("surface_free",[id],context);
            Begin();outputCanvas.QueueRedraw();await AuditDrawFrames();
        }
        output.QueueFree();target=-1;blend=0;alpha=1;colour=Colors.White;
        if(errors.Count>0)throw new InvalidOperationException("Effects mismatch: "+string.Join(",",errors));
        await AuditEffectEvents();
    }

    private async Task AuditEffectEvents()
    {
        world.Evaluator.ResetBudget();
        var context=new GmContext(new GmInstance());
        world.Engine.Write("view_wview",640,[0]);world.Engine.Write("view_hview",480,[0]);
        world.Engine.Write("view_xview",0,[0]);world.Engine.Write("view_yview",0,[0]);
        foreach(var entry in new[]{("config_AspectRatio",0.0),("AlphaDarkness",0.8),("DynamicDark",1.0),("Darkness",0.8),("LightAdd_Flash",0.0),("PlayerHP",100.0),("GameOver",0.0)})world.Globals.Write(entry.Item1,entry.Item2);
        var rain=world.Create(world.Constants["objRainController"].Int,0,0,run:false);
        var block=world.Create(world.Constants["objRainBlock"].Int,120,80,run:false);
        var blocker1=world.Create(world.Constants["objRainBlock"].Int,300,120,run:false);
        var blocker2=world.Create(world.Constants["objRainBlock"].Int,400,100,run:false);
        int mask=world.Constants["sprLightAdd"].Int;
        foreach(var blocker in new[]{blocker1,blocker2}){blocker.Scope.Write("mask_index",mask);blocker.Scope.Write("image_angle2",15);}
        world.Event(rain,0,0);int rainId=rain.Scope.Read("surRain").Int;
        rain.Scope.Write("rainBlocker1",blocker1.Id);rain.Scope.Write("rainBlocker2",blocker2.Id);
        int seed=world.Random.Seed;
        world.Event(rain,3,0);world.Event(rain,3,0);
        if(rain.Scope.Read("STEP").Int!=61||world.Random.Seed!=seed)throw new InvalidOperationException("Rain animation/random order changed");
        var rainEffect=effects[rainId];
        if(rainEffect.BatchCount!=3||rainEffect.TriangleCount<20)throw new InvalidOperationException("Rain tiles/blockers missing");
        await AuditDrawFrames();using var before=rainEffect.View.GetTexture().GetImage();
        block.Scope.Write("x",180);blocker1.Scope.Write("x",250);blocker2.Scope.Write("y",140);
        world.Engine.Write("view_xview",32,[0]);world.Event(rain,3,0);
        await AuditDrawFrames();using var after=rainEffect.View.GetTexture().GetImage();
        if(before.GetData().SequenceEqual(after.GetData()))throw new InvalidOperationException("Rain motion/blockers did not update");
        var dark=world.Create(world.Constants["objDark4"].Int,0,0,run:false);
        var light=world.Create(world.Constants["objLightAdd"].Int,200,180,run:false);
        world.Event(dark,0,0);int darkId=dark.Scope.Read("Surf").Int;
        seed=world.Random.Seed;var expectedRandom=new GmRandom(seed);expectedRandom.Next(0.1);expectedRandom.Next(0.1);
        world.Event(dark,3,0);world.Event(dark,3,0);
        if(world.Random.Seed!=expectedRandom.Seed)throw new InvalidOperationException("Dark flicker random order changed");
        var darkEffect=effects[darkId];await AuditDrawFrames();
        using var dim=darkEffect.View.GetTexture().GetImage();
        darkEffect.Schedule();await AuditDrawFrames();using var paused=darkEffect.View.GetTexture().GetImage();
        if(!dim.GetData().SequenceEqual(paused.GetData()))throw new InvalidOperationException("Paused darkness accumulated light");
        world.Globals.Write("LightAdd_Flash",0.8);world.Event(dark,3,0);
        await AuditDrawFrames();using var flash=darkEffect.View.GetTexture().GetImage();
        if(flash.GetPixel(0,0).R<=dim.GetPixel(0,0).R)throw new InvalidOperationException("Darkness flash missing");
        int fallbackSeed=world.Random.Seed;SoftwareEffect(darkId);
        if(world.Random.Seed!=fallbackSeed)throw new InvalidOperationException("Fallback consumed random numbers");
        world.Event(rain,7,5);world.Event(dark,7,5);
        if(surfaces.ContainsKey(rainId)||surfaces.ContainsKey(darkId))throw new InvalidOperationException("Room-end effect release failed");
        foreach(var instance in new[]{rain,block,blocker1,blocker2,dark,light})instance.Destroyed=true;
        target=-1;blend=0;Begin();await AuditDrawFrames();
        GD.Print("EFFECT_EVENTS_OK rain tiles/blockers/animation/camera; darkness flicker/flash/catch-up/pause/resume; room-end release");
    }

    private sealed class EffectBatch
    {
        public readonly GmDrawCommand Command;
        private readonly int divisor;
        public Texture2D? Texture;
        public int Blend,Count;
        public Vector2[] Points=new Vector2[96],Uvs=new Vector2[96];
        public Color[] Colours=new Color[96];
        private int[] indices=Enumerable.Range(0,96).ToArray();
        public EffectBatch(int divisor){this.divisor=divisor;Command=new(Draw);}
        public void Vertex(Vector2 point,Color colour,Vector2 uv)
        {
            if(Count==Points.Length)
            {
                int size=Count*2;Array.Resize(ref Points,size);Array.Resize(ref Uvs,size);Array.Resize(ref Colours,size);
                indices=Enumerable.Range(0,size).ToArray();
            }
            Points[Count]=point;Colours[Count]=colour;Uvs[Count++]=uv;
        }
        private void Draw(GmCanvas canvas)
        {
            canvas.DrawSetTransform(Vector2.Zero,0,new Vector2(1f/divisor,1f/divisor));
            RenderingServer.CanvasItemAddTriangleArray(canvas.GetCanvasItem(),indices.AsSpan(0,Count),Points.AsSpan(0,Count),Colours.AsSpan(0,Count),Uvs.AsSpan(0,Count),ReadOnlySpan<int>.Empty,ReadOnlySpan<float>.Empty,Texture?.GetRid()??default,-1);
            canvas.DrawSetTransform(Vector2.Zero);
        }
    }

    private sealed class EffectSurface(string kind,Vector2 logical,int divisor,SubViewport view,GmCanvas canvas,GmPerformance performance)
    {
        public readonly string Kind=kind;
        public readonly Vector2 Logical=logical;
        public readonly int Divisor=divisor;
        public readonly SubViewport View=view;
        public readonly GmCanvas Canvas=canvas;
        private readonly List<EffectBatch> batches=new();
        public int BatchCount{get;private set;}
        public int TriangleCount{get;private set;}
        public IEnumerable<EffectBatch> CurrentBatches=>batches.Take(BatchCount);
        public void Reset(){BatchCount=0;TriangleCount=0;}
        public void Triangle(Texture2D? texture,int blend,Vector2 a,Vector2 b,Vector2 c,Color ca,Color cb,Color cc,Vector2 ua,Vector2 ub,Vector2 uc)
        {
            EffectBatch batch;
            if(BatchCount>0&&batches[BatchCount-1].Texture==texture&&batches[BatchCount-1].Blend==blend)batch=batches[BatchCount-1];
            else
            {
                if(BatchCount==batches.Count)batches.Add(new(Divisor));
                batch=batches[BatchCount++];batch.Texture=texture;batch.Blend=blend;batch.Count=0;
            }
            batch.Vertex(a,ca,ua);batch.Vertex(b,cb,ub);batch.Vertex(c,cc,uc);TriangleCount++;
            Schedule();
        }
        public void Schedule(){Canvas.QueueRedraw();View.RenderTargetUpdateMode=SubViewport.UpdateMode.Once;}
        public void Paint(GmCanvas canvas)
        {
            long started=performance.Start();
            try
            {
                canvas.StartBatches();
                for(int n=0;n<BatchCount;n++){var batch=batches[n];canvas.Batch(batch.Command,batch.Blend);}
            }
            finally{performance.End("effect_cpu_submit",started);}
        }
    }
}

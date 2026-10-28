using Godot;

namespace SlackOfDescent;

public sealed partial class GmGraphics
{
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
}

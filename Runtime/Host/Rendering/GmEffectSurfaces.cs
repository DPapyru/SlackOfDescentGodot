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

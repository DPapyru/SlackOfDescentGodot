namespace SlackOfDescent;

public sealed class GmTriangleCache
{
    private const int MaxEntries=8192;
    private const int MaxStoredInts=32*1024*1024/sizeof(int);
    private readonly record struct Key(int Width,int Height,float Ax,float Ay,float Bx,float By,float Cx,float Cy);
    private readonly Dictionary<Key,int[]> entries=new();
    private readonly Queue<Key> order=new();
    private int storedInts;
    public int Count=>entries.Count;
    public int Bytes=>storedInts*sizeof(int);
    public long Hits{get;private set;}
    public long Misses{get;private set;}
    public void Clear(){entries.Clear();order.Clear();storedInts=0;Hits=Misses=0;}
    public void Fill(byte[] rgba,int width,int height,float ax,float ay,float bx,float by,float cx,float cy)
    {
        var key=new Key(width,height,ax,ay,bx,by,cx,cy);
        if(entries.TryGetValue(key,out var spans))Hits++;
        else
        {
            Misses++;spans=GmPixels.TriangleSpans(width,height,ax,ay,bx,by,cx,cy);
            // Bound both entry overhead and coverage storage during camera movement.
            if(spans.Length<=MaxStoredInts)
            {
                while(entries.Count>=MaxEntries||storedInts+spans.Length>MaxStoredInts)
                {
                    var oldest=order.Dequeue();storedInts-=entries[oldest].Length;entries.Remove(oldest);
                }
                entries.Add(key,spans);order.Enqueue(key);storedInts+=spans.Length;
            }
        }
        GmPixels.FillTriangleSpans(rgba,width,spans,0,0,0);
    }
}

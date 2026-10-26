namespace SlackOfDescent;

public readonly record struct GmFrameStamp(long Tick,int Room,long Sequence);

public readonly record struct GmFrameStats(long Published,long Rendered,long Coalesced,long Skipped)
{
    public double CoalesceRatio=>Published==0?0:(double)Coalesced/Published;
    public double RenderRatio=>Published==0?0:(double)Rendered/Published;
}

public sealed class GmFramePlan
{
    private bool dirty;
    private GmFrameStamp stamp;
    private long sequence;
    private long published;
    private long rendered;
    private long coalesced;
    private long skipped;
    private int room=int.MinValue;
    public int Room=>room;
    public bool Dirty=>dirty;
    public long Sequence=>sequence;
    public GmFrameStamp? Pending=>dirty?stamp:null;
    public GmFrameStats Stats=>new(published,rendered,coalesced,skipped);
    public void Reset()=>dirty=false;
    public void Reset(int currentRoom){room=currentRoom;dirty=false;}
    public GmFrameStamp Publish(long tick,int currentRoom)
    {
        if(currentRoom!=room)Reset(currentRoom);
        else if(dirty){coalesced++;return stamp;}
        stamp=new(tick,currentRoom,++sequence);
        dirty=true;published++;
        return stamp;
    }
    public GmFrameStamp? Consume()
    {
        if(!dirty){skipped++;return null;}
        dirty=false;rendered++;
        return stamp;
    }
}

namespace SlackOfDescent;

public readonly record struct GmView(double X,double Y,double Width,double Height,double PortX,double PortY,double PortWidth,double PortHeight,double Angle)
{
    public bool Contains(double x,double y)=>x>=PortX&&y>=PortY&&x<PortX+PortWidth&&y<PortY+PortHeight;
    public (double X,double Y) ToRoom(double x,double y)
    {
        double dx=Width*(x-PortX)/PortWidth-Width/2,dy=Height*(y-PortY)/PortHeight-Height/2;
        double radians=Angle*Math.PI/180,cos=Math.Cos(radians),sin=Math.Sin(radians);
        return (Math.Round(X+Width/2+dx*cos-dy*sin,MidpointRounding.ToEven),Math.Round(Y+Height/2+dx*sin+dy*cos,MidpointRounding.ToEven));
    }
    public (double X,double Y) ToScreen(double x,double y)
    {
        double dx=x-X-Width/2,dy=y-Y-Height/2;
        double radians=Angle*Math.PI/180,cos=Math.Cos(radians),sin=Math.Sin(radians);
        return (PortX+(dx*cos+dy*sin+Width/2)*PortWidth/Width,PortY+(-dx*sin+dy*cos+Height/2)*PortHeight/Height);
    }
}

public sealed partial class GmWorld
{
    public GmView View(int index)
    {
        double N(string name)=>Engine.Read("view_"+name,[index]).Number;
        return new(N("xview"),N("yview"),N("wview"),N("hview"),N("xport"),N("yport"),N("wport"),N("hport"),N("angle"));
    }
    public (int Width,int Height) OutputSize()
    {
        int width=0,height=0;
        if(Engine.Read("view_enabled").Truth)
            for(int i=0;i<8;i++)if(Engine.Read("view_visible",[i]).Truth)
            {
                var view=View(i);width=Math.Max(width,(int)(view.PortX+view.PortWidth));height=Math.Max(height,(int)(view.PortY+view.PortHeight));
            }
        return (Math.Max(1,width>0?width:Engine.Read("room_width").Int),Math.Max(1,height>0?height:Engine.Read("room_height").Int));
    }
    public (double X,double Y) MouseToRoom(double x,double y)
    {
        if(!Engine.Read("view_enabled").Truth)return (x,y);
        int first=-1;
        for(int i=0;i<8;i++)if(Engine.Read("view_visible",[i]).Truth&&View(i) is var view&&view.PortWidth>0&&view.PortHeight>0)
        {
            if(first<0)first=i;
        }
        for(int i=7;i>=0;i--)if(Engine.Read("view_visible",[i]).Truth&&View(i) is var view&&view.PortWidth>0&&view.PortHeight>0&&view.Contains(x,y))return view.ToRoom(x,y);
        return first>=0?View(first).ToRoom(x,y):(x,y);
    }
}

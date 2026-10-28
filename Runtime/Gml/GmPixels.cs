using System.Text.Json;

namespace SlackOfDescent;

public static class GmPixels
{
    public static void CompositeNormal(byte[] destination,int width,int height,byte[] image,int imageWidth,int imageHeight,int left,int top,int right,int bottom,float xx,float yy,float tx,float ty)
    {
        left=Math.Clamp(left,0,width);right=Math.Clamp(right,0,width);
        top=Math.Clamp(top,0,height);bottom=Math.Clamp(bottom,0,height);
        if(left>=right||top>=bottom)return;
        Span<int> columns=right-left<=4096?stackalloc int[right-left]:new int[right-left];
        for(int x=left;x<right;x++)
        {
            float local=xx*(x+0.5f)+tx;
            columns[x-left]=local>=0&&local<imageWidth?(int)MathF.Floor(local)*4:-1;
        }
        for(int y=top;y<bottom;y++)
        {
            float local=yy*(y+0.5f)+ty;
            if(!(local>=0&&local<imageHeight))continue;
            int row=(int)MathF.Floor(local)*imageWidth*4;
            for(int x=left;x<right;x++)
            {
                int column=columns[x-left];if(column<0)continue;
                int si=row+column,di=(y*width+x)*4;
                byte alpha=image[si+3];
                if(alpha==0)continue;
                if(alpha==255)
                {
                    destination[di]=image[si];destination[di+1]=image[si+1];destination[di+2]=image[si+2];destination[di+3]=255;
                    continue;
                }
                float sa=alpha/255f;
                for(int channel=0;channel<4;channel++)
                {
                    float source=image[si+channel]/255f,previous=destination[di+channel]/255f;
                    destination[di+channel]=(byte)(Math.Clamp(source*sa+previous*(1-sa),0,1)*255);
                }
            }
        }
    }

    public static void FillOpaqueTriangle(byte[] rgba,int width,int height,float ax,float ay,float bx,float by,float cx,float cy,byte r,byte g,byte b)
    {
        FillTriangleSpans(rgba,width,TriangleSpans(width,height,ax,ay,bx,by,cx,cy),r,g,b);
    }
    public static void FillTriangleSpans(byte[] rgba,int width,int[] spans,byte r,byte g,byte b)
    {
        var pixels=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,uint>(rgba.AsSpan());
        Span<byte> colour=stackalloc byte[]{r,g,b,255};
        uint value=System.Runtime.InteropServices.MemoryMarshal.Read<uint>(colour);
        for(int row=1;row<spans.Length;row+=2)
        {
            int start=spans[row],end=spans[row+1];
            if(start<end)pixels.Slice((spans[0]+(row-1)/2)*width+start,end-start).Fill(value);
        }
    }
    public static int[] TriangleSpans(int width,int height,float ax,float ay,float bx,float by,float cx,float cy)
    {
        float Edge(float px,float py,float qx,float qy,float x,float y)=>(qx-px)*(y-py)-(qy-py)*(x-px);
        float area=Edge(ax,ay,bx,by,cx,cy);
        if(area==0||!float.IsFinite(area))return [];
        if(area<0){(bx,cx)=(cx,bx);(by,cy)=(cy,by);}
        int left=(int)Math.Clamp(MathF.Floor(Math.Min(ax,Math.Min(bx,cx))),0,width);
        int right=(int)Math.Clamp(MathF.Ceiling(Math.Max(ax,Math.Max(bx,cx)))+1,0,width);
        int top=(int)Math.Clamp(MathF.Floor(Math.Min(ay,Math.Min(by,cy))),0,height);
        int bottom=(int)Math.Clamp(MathF.Ceiling(Math.Max(ay,Math.Max(by,cy)))+1,0,height);
        if(left>=right||top>=bottom)return [];
        var spans=new int[1+(bottom-top)*2];spans[0]=top;
        for(int y=top;y<bottom;y++)
        {
            int start=left,end=right;
            Clip(ax,ay,bx,by,y+0.5f,ref start,ref end);
            Clip(bx,by,cx,cy,y+0.5f,ref start,ref end);
            Clip(cx,cy,ax,ay,y+0.5f,ref start,ref end);
            spans[1+(y-top)*2]=start;spans[2+(y-top)*2]=end;
        }
        return spans;
        void Clip(float px,float py,float qx,float qy,float y,ref int start,ref int end)
        {
            bool inclusive=py>qy||py==qy&&px<qx;
            bool Inside(int x)
            {
                float edge=Edge(px,py,qx,qy,x+0.5f,y);
                return edge>0||edge==0&&inclusive;
            }
            int lo=start,hi=end;
            if(qy==py){if(lo<hi&&!Inside(lo))end=start;return;}
            bool increasing=qy<py;
            while(lo<hi)
            {
                int mid=lo+(hi-lo)/2;
                if(Inside(mid)==increasing)hi=mid;else lo=mid+1;
            }
            if(increasing)start=lo;else end=lo;
        }
    }

    public static int[] Triangles(int mode,int count)
    {
        if(count<3)return [];
        if(mode==4)return Enumerable.Range(0,count-count%3).ToArray();
        if(mode is not (5 or 6))throw new ArgumentOutOfRangeException(nameof(mode));
        var indices=new int[(count-2)*3];
        for(int i=0;i<count-2;i++)
        {
            indices[i*3]=mode==6?0:i%2==0?i:i+1;
            indices[i*3+1]=mode==6?i+1:i%2==0?i+1:i;
            indices[i*3+2]=i+2;
        }
        return indices;
    }
    public static void FillRectangle(byte[] rgba,int width,int height,int left,int top,int right,int bottom,byte r,byte g,byte b,byte a)
    {
        left=Math.Clamp(left,0,width);right=Math.Clamp(right,0,width);top=Math.Clamp(top,0,height);bottom=Math.Clamp(bottom,0,height);
        if(left>=right||top>=bottom)return;
        int start=(top*width+left)*4,length=(right-left)*4;
        for(int i=start;i<start+length;i+=4){rgba[i]=r;rgba[i+1]=g;rgba[i+2]=b;rgba[i+3]=a;}
        for(int y=top+1;y<bottom;y++)Buffer.BlockCopy(rgba,start,rgba,(y*width+left)*4,length);
    }
    public static void Prepare(byte[] rgba,int width,int height,int transparency,bool smooth)
    {
        if(width<=0||height<=0||rgba.Length!=checked(width*height*4))throw new ArgumentException("Invalid RGBA dimensions");
        if(transparency!=2)
        {
            bool transparent=true;
            for(int i=3;i<rgba.Length;i+=4)if(rgba[i]!=0){transparent=false;break;}
            if(transparent)for(int i=3;i<rgba.Length;i+=4)rgba[i]=255;
        }
        if(transparency==1)
        {
            int key=(height-1)*width*4;
            byte r=rgba[key],g=rgba[key+1],b=rgba[key+2];
            if(rgba[key+3]!=0)for(int i=0;i<rgba.Length;i+=4)if(rgba[i]==r&&rgba[i+1]==g&&rgba[i+2]==b)rgba[i+3]=0;
        }
        if(smooth)
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                if(rgba[(y*width+x)*4+3]==0)
                    for(int sy=Math.Max(0,y-1);sy<Math.Min(height,y+2);sy++)for(int sx=Math.Max(0,x-1);sx<Math.Min(width,x+2);sx++)
                    {
                        int alpha=(sy*width+sx)*4+3;
                        if(rgba[alpha]>=32)rgba[alpha]-=32;
                    }
        if(transparency==1)
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int index=(y*width+x)*4;
                if(rgba[index+3]!=0)continue;
                int source=x>0&&rgba[index-1]!=0?index-4:x<width-1&&rgba[index+7]!=0?index+4:
                    y>0&&rgba[index-width*4+3]!=0?index-width*4:y<height-1&&rgba[index+width*4+3]!=0?index+width*4:-1;
                if(source>=0)Array.Copy(rgba,source,rgba,index,3);
            }
        if(transparency==-1)for(int i=3;i<rgba.Length;i+=4)rgba[i]=255;
    }

    public static JsonElement Mask(byte[] rgba,int width,int height)
    {
        if(width<=0||height<=0||rgba.Length!=checked(width*height*4))throw new ArgumentException("Invalid RGBA dimensions");
        int left=width-1,right=0,top=height-1,bottom=0;
        bool found=false;var data=new char[width*height];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            bool opaque=rgba[(y*width+x)*4+3]!=0;data[y*width+x]=opaque?'1':'0';
            if(!opaque)continue;
            if(!found){left=right=x;top=bottom=y;found=true;}
            else{left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
        }
        return JsonSerializer.SerializeToElement(new{width,height,left,right,top,bottom,data=new string(data)});
    }
}

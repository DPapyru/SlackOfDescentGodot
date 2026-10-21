using System.Text.Json;

namespace SlackOfDescent;

public static class GmPixels
{
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

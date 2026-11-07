namespace SlackOfDescent;

public sealed partial class GmWorld
{
    internal Func<GmValue[],GmContext,bool>? CharacterSpriteBlur;
    private GmValue DrawSpriteBlur(GmValue[] args,GmContext context)
    {
        if(CharacterSpriteBlur?.Invoke(args,context)==true)return 0;
        double x=args[2].Number,y=args[3].Number,angle=args[6].Number,alpha=args[8].Number;
        var slice=new GmValue[9];
        slice[0]=args[0];slice[1]=args[1];slice[4]=args[4];slice[5]=args[5];slice[7]=args[7];
        void Draw(double px,double py,double rotation,double opacity)
        {
            slice[2]=px;slice[3]=py;slice[6]=rotation;slice[8]=opacity;
            Call("draw_sprite_ext",slice,context);
        }
        double length=args[9].Number,rotation=args[11].Number,originalRotation=rotation;
        var mode=Globals.Read("config_MotionBlur");
        if(!mode.Truth||length>150||length<=1&&Math.Abs(rotation)<=1){Draw(x,y,angle,alpha);return 0;}
        rotation=Math.Abs(rotation);
        double quality=1;
        if(!Globals.Read("fullQuality").Truth&&mode.Number!=3)
        {
            quality=Math.Max(1,(length*3+rotation)/100+InstanceCount(Constants["objParts"].Int,context)*0.05+InstanceCount(Constants["objEffect"].Int,context)*0.02);
            Globals.Write("BlurQual",Globals.Read("BlurQual").Number+quality);
        }
        length/=quality;rotation/=quality*2;originalRotation/=quality*2;
        double times=Math.Max(rotation,length),dx=0,dy=0;
        if(length>0)
        {
            double direction=args[10].Number*Math.PI/180;
            dx=Math.Cos(direction)*(length/times*quality);dy=-Math.Sin(direction)*(length/times*quality);
        }
        double increment=originalRotation/times*2*quality;
        double half=times/2;Write(context.Self,"qtimes",half);
        for(double i=times;i>0;i-=1)Draw(x+i*dx,y+i*dy,angle+increment*i,alpha/half);
        return 0;
    }
}

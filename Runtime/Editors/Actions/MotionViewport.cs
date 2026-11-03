namespace SlackOfDescent;

public readonly record struct MotionView(double ViewX, double ViewY, double ViewWidth, double ViewHeight, int PortWidth, int PortHeight);

public static class MotionViewport
{
    public static (double X, double Y) ToWorld(double surfaceX, double surfaceY, double surfaceWidth, double surfaceHeight, MotionView view)
    {
        double scale = Scale(surfaceWidth, surfaceHeight, view);
        if (scale <= 0) return (view.ViewX, view.ViewY);
        var (offsetX, offsetY) = Offset(surfaceWidth, surfaceHeight, scale, view);
        return (view.ViewX + (surfaceX - offsetX) / scale * view.ViewWidth / view.PortWidth, view.ViewY + (surfaceY - offsetY) / scale * view.ViewHeight / view.PortHeight);
    }

    public static (double X, double Y) ToSurface(double worldX, double worldY, double surfaceWidth, double surfaceHeight, MotionView view)
    {
        double scale = Scale(surfaceWidth, surfaceHeight, view);
        if (scale <= 0) return (0, 0);
        var (offsetX, offsetY) = Offset(surfaceWidth, surfaceHeight, scale, view);
        return (offsetX + (worldX - view.ViewX) / view.ViewWidth * view.PortWidth * scale, offsetY + (worldY - view.ViewY) / view.ViewHeight * view.PortHeight * scale);
    }

    public static double WorldPerPixel(double surfaceWidth, double surfaceHeight, MotionView view)
    {
        double scale = Scale(surfaceWidth, surfaceHeight, view);
        return scale <= 0 ? 0 : view.ViewWidth / view.PortWidth / scale;
    }

    private static (double X, double Y) Offset(double surfaceWidth, double surfaceHeight, double scale, MotionView view) =>
        ((surfaceWidth - view.PortWidth * scale) / 2, (surfaceHeight - view.PortHeight * scale) / 2);

    private static double Scale(double surfaceWidth, double surfaceHeight, MotionView view)
    {
        if (!(surfaceWidth > 0) || !(surfaceHeight > 0) || view.PortWidth <= 0 || view.PortHeight <= 0 || !(view.ViewWidth > 0) || !(view.ViewHeight > 0)) return 0;
        return Math.Min(surfaceWidth / view.PortWidth, surfaceHeight / view.PortHeight);
    }
}

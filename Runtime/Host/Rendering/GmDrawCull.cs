namespace SlackOfDescent;

// Conservative screen-space rejection; callers keep the exact per-command test as the backstop.
internal static class GmDrawCull
{
    public static float CornerRadius(float originX, float originY, float width, float height)
    {
        float x = Math.Max(Math.Abs(originX), Math.Abs(width - originX));
        float y = Math.Max(Math.Abs(originY), Math.Abs(height - originY));
        return MathF.Sqrt(x * x + y * y);
    }

    public static bool Outside(
        float x,
        float y,
        float radius,
        float cameraX,
        float cameraY,
        float viewWidth,
        float viewHeight
    ) =>
        x + radius < cameraX - 1
        || x - radius > cameraX + viewWidth + 1
        || y + radius < cameraY - 1
        || y - radius > cameraY + viewHeight + 1;
}

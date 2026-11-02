using SlackOfDescent;

public static class DrawCullTests
{
    public static int Run()
    {
        int count = 0, culled = 0;
        void Check(bool value, string name)
        {
            if (!value)
                throw new Exception(name);
            count++;
        }
        var random = new Random(20261102);
        for (int attempt = 0; attempt < 20000; attempt++)
        {
            int frames = 1 + random.Next(4);
            float originX = Range(random, -200, 200), originY = Range(random, -200, 200);
            var sizes = new (float Width, float Height)[frames];
            float extent = 0;
            for (int i = 0; i < frames; i++)
            {
                sizes[i] = (Range(random, 0, 500), Range(random, 0, 500));
                extent = Math.Max(
                    extent,
                    GmDrawCull.CornerRadius(originX, originY, sizes[i].Width, sizes[i].Height)
                );
            }
            float scaleX = Pick(random, -4, 4, 0, 1e-3f, 250), scaleY = Pick(random, -4, 4, 0, 1e-3f, 250);
            float maxScale = Math.Max(Math.Abs(scaleX), Math.Abs(scaleY));
            float cameraX = Range(random, -5000, 5000), cameraY = Range(random, -5000, 5000);
            float reach = Math.Max(200, extent * maxScale * 2);
            float x, y;
            if (random.Next(2) == 0)
            {
                x = Range(random, cameraX - reach, cameraX + 640 + reach);
                y = Range(random, cameraY - reach, cameraY + 480 + reach);
            }
            else
            {
                x = Range(random, -1e6f, 1e6f);
                y = Range(random, -1e6f, 1e6f);
            }
            float angle = Range(random, (float)-Math.PI, (float)Math.PI);
            if (!GmDrawCull.Outside(x, y, extent * maxScale, cameraX, cameraY, 640, 480))
                continue;
            culled++;
            for (int i = 0; i < frames; i++)
                Check(
                    OutsideCorners(
                        x,
                        y,
                        originX,
                        originY,
                        sizes[i].Width,
                        sizes[i].Height,
                        scaleX,
                        scaleY,
                        angle,
                        cameraX,
                        cameraY
                    ),
                    "coarse cull implies exact corner cull"
                );
        }
        Check(culled > 2000, "fuzz exercised conservative culling");
        return count;
    }

    // Mirrors GmGraphics.Textured's four-corner test.
    private static bool OutsideCorners(
        float x,
        float y,
        float originX,
        float originY,
        float width,
        float height,
        float scaleX,
        float scaleY,
        float angle,
        float cameraX,
        float cameraY
    )
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double maxX = double.MinValue, maxY = double.MinValue, minX = double.MaxValue, minY = double.MaxValue;
        foreach (var (px, py) in new[]
        {
            (-originX, -originY),
            (width - originX, -originY),
            (width - originX, height - originY),
            (-originX, height - originY),
        })
        {
            double rx = cos * scaleX * px - sin * scaleY * py + x - cameraX;
            double ry = sin * scaleX * px + cos * scaleY * py + y - cameraY;
            maxX = Math.Max(maxX, rx);
            maxY = Math.Max(maxY, ry);
            minX = Math.Min(minX, rx);
            minY = Math.Min(minY, ry);
        }
        return maxX < 0 || maxY < 0 || minX > 640 || minY > 480;
    }

    private static float Range(Random random, float min, float max) =>
        (float)(min + random.NextDouble() * (max - min));

    private static float Pick(Random random, params float[] values) =>
        values[random.Next(values.Length)];
}

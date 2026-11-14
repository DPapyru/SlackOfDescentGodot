using System;
using System.Collections.Generic;

namespace RopeBonds;

public sealed class VerletChain
{
    public const double Damping = .98;
    public const double MaxVelocity = 6;
    private sealed class Point
    {
        public double X, Y, PreviousX, PreviousY;
    }
    private readonly List<Point> points = new();
    private readonly int iterations;
    public double RestLength { get; }
    public double Gravity { get; }
    public int Count => points.Count;
    public VerletChain(double restLength, double gravity, int iterations = 6)
    {
        if (!double.IsFinite(restLength) || restLength <= 0) throw new ArgumentOutOfRangeException(nameof(restLength));
        if (!double.IsFinite(gravity)) throw new ArgumentOutOfRangeException(nameof(gravity));
        if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
        RestLength = restLength; Gravity = gravity; this.iterations = iterations;
    }
    public (double X, double Y) this[int index] => (points[index].X, points[index].Y);
    public bool IsFinite
    {
        get
        {
            foreach (var point in points)
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.PreviousX) || !double.IsFinite(point.PreviousY)) return false;
            return true;
        }
    }
    public double Spacing(int index)
    {
        if (index < 1 || index >= points.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var parent = points[index - 1]; var point = points[index];
        return Math.Sqrt((point.X - parent.X) * (point.X - parent.X) + (point.Y - parent.Y) * (point.Y - parent.Y));
    }
    public void Reset(int count, double headX, double headY)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        points.Clear();
        for (int index = 0; index < count; index++)
        {
            double x = headX - index * RestLength;
            points.Add(new Point { X = x, Y = headY, PreviousX = x, PreviousY = headY });
        }
    }
    public void Step(double headX, double headY)
    {
        if (points.Count < 2) return;
        for (int index = 1; index < points.Count; index++)
        {
            var point = points[index];
            double velocityX = Math.Clamp((point.X - point.PreviousX) * Damping, -MaxVelocity, MaxVelocity);
            double velocityY = Math.Clamp((point.Y - point.PreviousY) * Damping + Gravity, -MaxVelocity, MaxVelocity);
            point.PreviousX = point.X; point.PreviousY = point.Y;
            point.X += velocityX; point.Y += velocityY;
        }
        var head = points[0];
        head.PreviousX = head.X; head.PreviousY = head.Y;
        head.X = headX; head.Y = headY;
        for (int iteration = 0; iteration < iterations; iteration++) Relax();
    }
    public void Collide(Func<double, double, bool> solid)
    {
        for (int index = 1; index < points.Count; index++)
        {
            var point = points[index];
            if (!solid(point.X, point.Y)) continue;
            point.X = point.PreviousX; point.Y = point.PreviousY;
            point.PreviousX = point.X; point.PreviousY = point.Y;
        }
    }
    private void Relax()
    {
        for (int index = 1; index < points.Count; index++)
        {
            var parent = points[index - 1]; var point = points[index];
            double dx = point.X - parent.X, dy = point.Y - parent.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 1e-9)
            {
                point.X = parent.X + RestLength; point.Y = parent.Y;
                continue;
            }
            double correction = (distance - RestLength) / distance;
            point.X -= dx * correction; point.Y -= dy * correction;
        }
    }
}

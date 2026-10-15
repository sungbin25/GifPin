using System;
using System.Collections.Generic;

namespace GifPin.Core;

/// <summary>Immutable so ActiveItem snapshots, scenes and presets can safely share a profile.</summary>
public sealed record CollisionEffect
{
    public bool Enabled { get; init; } = true;
    public string Kind { get; init; } = Fx.Sparkle;
    public string Color { get; init; } = "#8DE6D1";
    public int Count { get; init; } = 8;
    public double Size { get; init; } = 12;
    public double Life { get; init; } = 0.6;
    public double Speed { get; init; } = 90;

    public CollisionEffect Normalize() => this with
    {
        Kind = Array.IndexOf(Fx.Kinds, Kind) >= 0 ? Kind : Fx.Sparkle,
        Color = Fx.NormalizeColorList(Color), Count = Math.Clamp(Count, 1, 80),
        Size = Finite(Size, 4, 48, 12), Life = Finite(Life, .2, 3, .6), Speed = Finite(Speed, 20, 240, 90),
    };
    internal static double Finite(double n, double min, double max, double fallback) => double.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
    public static CollisionEffect StrongDefault => new() { Kind = Fx.Star, Color = "#FFB35C", Count = 24, Size = 20, Life = 1, Speed = 170 };
}

/// <summary>One frame of an axis-aligned collider, independent of windows and rendering.</summary>
internal sealed class CollisionBody
{
    public string Id = "";
    public double X, Y, PreviousX, PreviousY, Width, Height, Vx, Vy;
    public string Mode = Motion.None;
    public bool Changed;
    public bool Movable => Mode != Motion.None;
    public double InverseMass => Mode is Motion.Slide or Motion.Bounce or Motion.Fall ? 1 : 0;
}

internal readonly record struct CollisionHit(CollisionBody A, CollisionBody B, double X, double Y, double Speed);

internal static class Collisions
{
    // Equal masses, slightly inelastic: normal momentum transfers while tangential motion is preserved.
    private const double Restitution = .9;
    public static List<CollisionHit> Resolve(IReadOnlyList<CollisionBody> bodies)
    {
        var hits = new List<CollisionHit>();
        var touched = new HashSet<(int, int)>();
        for (int pass = 0; pass < 4; pass++)
        for (int i = 0; i < bodies.Count; i++)
        for (int j = i + 1; j < bodies.Count; j++)
        {
            var a = bodies[i]; var b = bodies[j];
            if (!a.Movable && !b.Movable) continue;
            if (!Contact(a, b, pass == 0, out double nx, out double ny, out double depth, out double time)) continue;
            if (time < 1)
            {
                // Stop at first contact rather than allowing fast throws to pass through thin images.
                if (a.Movable) { a.X = a.PreviousX + (a.X - a.PreviousX) * time; a.Y = a.PreviousY + (a.Y - a.PreviousY) * time; }
                if (b.Movable) { b.X = b.PreviousX + (b.X - b.PreviousX) * time; b.Y = b.PreviousY + (b.Y - b.PreviousY) * time; }
            }
            double closing = (a.Vx - b.Vx) * nx + (a.Vy - b.Vy) * ny;
            double total = a.InverseMass + b.InverseMass;
            if (closing > 0 && !touched.Contains((i, j)))
            {
                if (total > 0)
                {
                    double impulse = (1 + Restitution) * closing / total;
                    a.Vx -= impulse * nx * a.InverseMass; a.Vy -= impulse * ny * a.InverseMass;
                    b.Vx += impulse * nx * b.InverseMass; b.Vy += impulse * ny * b.InverseMass;
                }
                if (a.Mode == Motion.Walk && nx != 0) a.Vx = -Math.Abs(a.Vx) * nx;
                if (b.Mode == Motion.Walk && nx != 0) b.Vx = Math.Abs(b.Vx) * nx;
                if (a.Mode == Motion.Walk && ny != 0) a.Vy = 0;
                if (b.Mode == Motion.Walk && ny != 0) b.Vy = 0;
                double x = (Math.Max(a.X, b.X) + Math.Min(a.X + a.Width, b.X + b.Width)) / 2;
                double y = (Math.Max(a.Y, b.Y) + Math.Min(a.Y + a.Height, b.Y + b.Height)) / 2;
                if (closing >= 1) hits.Add(new CollisionHit(a, b, x, y, closing));
                touched.Add((i, j));
            }
            double wa = a.Movable ? 1 : 0, wb = b.Movable ? 1 : 0;
            if (wa + wb > 0)
            {
                double correction = (Math.Max(0, depth) + .02) / (wa + wb);
                a.X -= nx * correction * wa; a.Y -= ny * correction * wa;
                b.X += nx * correction * wb; b.Y += ny * correction * wb;
            }
            a.Changed |= a.Movable; b.Changed |= b.Movable;
        }
        return hits;
    }

    private static bool Contact(CollisionBody a, CollisionBody b, bool sweep, out double nx, out double ny, out double depth, out double time)
    {
        nx = ny = depth = 0; time = 1;
        double dx = (a.X - a.PreviousX) - (b.X - b.PreviousX), dy = (a.Y - a.PreviousY) - (b.Y - b.PreviousY);
        bool previousOverlap = a.PreviousX < b.PreviousX + b.Width && a.PreviousX + a.Width > b.PreviousX
            && a.PreviousY < b.PreviousY + b.Height && a.PreviousY + a.Height > b.PreviousY;
        if (sweep && !previousOverlap && (dx != 0 || dy != 0))
        {
            static (double enter, double exit) Axis(double amin, double amax, double bmin, double bmax, double delta)
            {
                if (Math.Abs(delta) < 1e-9) return amax <= bmin || amin >= bmax ? (double.PositiveInfinity, double.NegativeInfinity) : (double.NegativeInfinity, double.PositiveInfinity);
                double first = (bmin - amax) / delta, last = (bmax - amin) / delta;
                return first < last ? (first, last) : (last, first);
            }
            var x = Axis(a.PreviousX, a.PreviousX + a.Width, b.PreviousX, b.PreviousX + b.Width, dx);
            var y = Axis(a.PreviousY, a.PreviousY + a.Height, b.PreviousY, b.PreviousY + b.Height, dy);
            double enter = Math.Max(x.enter, y.enter), exit = Math.Min(x.exit, y.exit);
            if (enter >= 0 && enter <= 1 && enter <= exit)
            {
                time = enter;
                if (x.enter >= y.enter) nx = Math.Sign(dx); else ny = Math.Sign(dy);
                return true;
            }
        }
        double ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (ox <= 0 || oy <= 0) return false;
        if (ox <= oy) { nx = a.X + a.Width / 2 <= b.X + b.Width / 2 ? 1 : -1; depth = ox; }
        else { ny = a.Y + a.Height / 2 <= b.Y + b.Height / 2 ? 1 : -1; depth = oy; }
        return true;
    }
}

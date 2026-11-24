using System.Numerics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

internal readonly record struct CircleSettings(float Radius, int Points, bool RandomOrder, TimeSpan Interval, bool SmoothTransition = false);

internal sealed class FloorPattern
{
    private readonly int[] order;
    private readonly Func<int, Vector3> pointAt;
    private readonly int intervalMs;
    private readonly bool randomOrder;
    private int next;
    private long? nextJump;
    private long transitionStart;
    private Vector3 transitionFrom;
    private Vector3 transitionTo;
    public Matrix34? TransitionHead { get; private set; }
    public bool SmoothTransition { get; }

    public FloorPattern(CalibrationSnapshot anchor, Vector3 center, Vector3 head, CircleSettings settings)
    {
        Anchor = anchor;
        Center = center;
        Settings = settings;
        SmoothTransition = settings.SmoothTransition;
        float firstAngle = StartAngle(center, head);
        pointAt = index =>
        {
            float angle = PointAngle(firstAngle, index, settings.Points);
            return center + new Vector3(MathF.Cos(angle) * settings.Radius, 0, MathF.Sin(angle) * settings.Radius);
        };
        intervalMs = (int)settings.Interval.TotalMilliseconds;
        randomOrder = settings.RandomOrder;
        order = Enumerable.Range(0, settings.Points).ToArray();
        if (randomOrder) Shuffle(order);
    }

    public FloorPattern(CalibrationSnapshot anchor, Vector3 center, Vector3 head, SweepControls sweep)
    {
        Anchor = anchor;
        var pose = SweepGuide.Pose(center, head, sweep);
        SmoothTransition = sweep.SmoothTransition;
        MovesVertically = sweep.Vertical || sweep.Stacked;
        var pathCenter = pose.Position - Vector3.UnitY * SweepGuide.FloorClearance;
        int pointsPerLayer = sweep.Rows * sweep.PointsPerRow;
        pointAt = index =>
        {
            int layer = index / pointsPerLayer;
            int point = index % pointsPerLayer;
            if (layer % 2 != 0) point = pointsPerLayer - 1 - point;
            return pathCenter + pose.Rotate(SweepGuide.Point(point, sweep)) + SweepGuide.LayerOffset(sweep, layer);
        };
        intervalMs = sweep.IntervalMs;
        randomOrder = sweep.Random;
        order = Enumerable.Range(0, pointsPerLayer * sweep.Layers).ToArray();
        if (randomOrder) Shuffle(order);
    }

    public CalibrationSnapshot Anchor { get; }
    public Vector3? Center { get; }
    public bool MovesVertically { get; }
    public CircleSettings Settings { get; }

    public static float StartAngle(Vector3 center, Vector3 head) =>
        MathF.Atan2(head.Z - center.Z, head.X - center.X);

    public static float PointAngle(float start, int index, int count) =>
        start + index * (MathF.Tau / count);

    public bool Due(long now) => nextJump is null || now >= nextJump;

    public Vector3 TakePoint(long now)
    {
        int index = order[next++];
        if (next == order.Length)
        {
            next = 0;
            if (randomOrder) Shuffle(order, index);
        }
        long deadline = (nextJump ?? now) + intervalMs;
        nextJump = deadline > now ? deadline : now + intervalMs;
        return pointAt(index);
    }

    public Vector3 SampleTransition(long now, Matrix34 rawHead)
    {
        if (TransitionHead is null)
        {
            // Keep the virtual path anchored while physical head motion remains tracked.
            TransitionHead = rawHead;
            transitionFrom = TakePoint(now);
            transitionTo = TakePoint(now);
            transitionStart = now;
            return transitionFrom;
        }

        if (now - transitionStart >= intervalMs)
        {
            transitionFrom = transitionTo;
            transitionTo = TakePoint(now);
            transitionStart = now;
            return transitionFrom;
        }

        float progress = (float)(now - transitionStart) / intervalMs;
        if (Center is not Vector3 center)
            return Vector3.Lerp(transitionFrom, transitionTo, progress);

        float from = StartAngle(center, transitionFrom);
        float to = StartAngle(center, transitionTo);
        float angle = from + MathF.IEEERemainder(to - from, MathF.Tau) * progress;
        return new(center.X + MathF.Cos(angle) * Settings.Radius, 0,
            center.Z + MathF.Sin(angle) * Settings.Radius);
    }

    internal static void Shuffle(int[] order, int previous = -1)
    {
        if (order.Length < 2) return;
        Random.Shared.Shuffle(order);
        if (order[0] == previous)
        {
            int swap = Random.Shared.Next(1, order.Length);
            (order[0], order[swap]) = (order[swap], order[0]);
        }
    }
}

internal static class PatternGuideStyle
{
    public static readonly Color Line = Color.FromArgb(110, 216, 255);
    public static readonly Color Point = Color.FromArgb(255, 118, 198);

    public static void DrawArrow(Graphics graphics, Pen pen, Vector2 tip, Vector2 direction, float length)
    {
        Vector2 side = new(-direction.Y, direction.X);
        Vector2 tail = tip - direction * length;
        Vector2 left = tail + side * (length / 2);
        Vector2 right = tail - side * (length / 2);
        graphics.DrawLines(pen, [new PointF(left.X, left.Y), new PointF(tip.X, tip.Y), new PointF(right.X, right.Y)]);
    }

    public static void DrawSegment(Graphics graphics, Pen pen, Vector2 from, Vector2 to, float arrowLength)
    {
        graphics.DrawLine(pen, from.X, from.Y, to.X, to.Y);
        Vector2 delta = to - from;
        if (delta.LengthSquared() >= 4 * arrowLength * arrowLength)
            DrawArrow(graphics, pen, Vector2.Lerp(from, to, .65f), Vector2.Normalize(delta), arrowLength);
    }
}

internal static class SweepGuide
{
    public const float WidthFraction = .94f;

    public const float FloorClearance = .017f;

    public static float OverlayWidth(SweepControls sweep) =>
        (float)Math.Max(sweep.Width, sweep.EffectiveRowDistance * (sweep.Rows - 1)) / WidthFraction;

    public static Vector3 LayerOffset(SweepControls sweep, int layer) =>
        Vector3.UnitY * ((float)sweep.EffectiveRowDistance * layer);

    public static Matrix34 Pose(Vector3 center, Vector3 eye, SweepControls sweep)
    {
        float angle = FloorPattern.StartAngle(center, eye);
        if (!sweep.Vertical || sweep.Stacked) return CircleGuide.Pose(center, angle - MathF.PI / 2);
        Vector3 normal = new(MathF.Cos(angle), 0, MathF.Sin(angle));
        Vector3 right = new(normal.Z, 0, -normal.X);
        float halfHeight = (float)sweep.EffectiveRowDistance * (sweep.Rows - 1) / 2;
        return Matrix34.FromBasis(right, Vector3.UnitY, normal,
            center + Vector3.UnitY * (halfHeight + FloorClearance));
    }

    // Coordinates are in meters in the overlay's local plane.
    public static Vector3 Point(int index, SweepControls sweep)
    {
        int row = index / sweep.PointsPerRow;
        int column = index % sweep.PointsPerRow;
        float x = column / (sweep.PointsPerRow - 1f) - .5f;
        float along = (row % 2 == 0 ? x : -x) * (float)sweep.Width;
        float across = (row - (sweep.Rows - 1) / 2f) * (float)sweep.EffectiveRowDistance;
        return new(along, across, 0);
    }

    public static byte[] MakeTexture(SweepControls sweep, bool markStart = true, bool reverse = false)
    {
        using var bitmap = new Bitmap(CircleGuide.Size, CircleGuide.Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var line = new Pen(PatternGuideStyle.Line, 4);
        using var ring = new Pen(PatternGuideStyle.Point, 2.5f);
        using var start = new SolidBrush(PatternGuideStyle.Point);
        const float center = CircleGuide.Size / 2f;
        float pixelsPerMeter = CircleGuide.Size / OverlayWidth(sweep);
        Vector2 previous = default;
        for (int i = 0; i < sweep.Rows * sweep.PointsPerRow; i++)
        {
            Vector3 point = Point(i, sweep);
            var pixel = new Vector2(center + point.X * pixelsPerMeter, center - point.Y * pixelsPerMeter);
            if (i > 0 && !sweep.Random)
                PatternGuideStyle.DrawSegment(g, line, reverse ? pixel : previous, reverse ? previous : pixel, 12);
            previous = pixel;
        }
        for (int i = 0; i < sweep.Rows * sweep.PointsPerRow; i++)
        {
            Vector3 point = Point(i, sweep);
            float x = center + point.X * pixelsPerMeter, y = center - point.Y * pixelsPerMeter;
            if (i == 0 && !sweep.Random && markStart) g.FillEllipse(start, x - 9, y - 9, 18, 18);
            else g.DrawEllipse(ring, x - 7, y - 7, 14, 14);
        }
        var pixels = new byte[CircleGuide.Size * CircleGuide.Size * 4];
        DashboardPanel.CopyRgba(bitmap, pixels);
        return pixels;
    }
}

internal static class CircleGuide
{
    public const int Size = 1024;
    public const float RadiusFraction = .47f;

    public static Matrix34 Pose(Vector3 center, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return Matrix34.FromBasis(new(c, 0, s), new(s, 0, -c), Vector3.UnitY,
            center + new Vector3(0, SweepGuide.FloorClearance, 0));
    }

    public static byte[] MakeTexture(int points, bool random = false)
    {
        using var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const float center = Size / 2f;
            const float radius = Size * RadiusFraction;
            using var ring = new Pen(PatternGuideStyle.Line, 4);
            using var point = new Pen(PatternGuideStyle.Point, 2.5f);
            // Random order has no predictable direction.
            if (!random)
            {
                g.DrawEllipse(ring, center - radius, center - radius, radius * 2, radius * 2);
                int arrowStride = Math.Max(1, (points + 15) / 16);
                for (int i = 0; i < points; i += arrowStride)
                {
                    float angle = FloorPattern.PointAngle(0, i, points) + MathF.PI / points;
                    Vector2 radial = new(MathF.Cos(angle), MathF.Sin(angle));
                    PatternGuideStyle.DrawArrow(g, ring, new Vector2(center) + radius * radial,
                        new Vector2(-radial.Y, radial.X), 12);
                }
            }
            float size = Math.Clamp(MathF.Tau * radius * .65f / points, 4, 18);
            for (int i = 0; i < points; i++)
            {
                float angle = FloorPattern.PointAngle(0, i, points);
                float x = center + radius * MathF.Cos(angle);
                float y = center + radius * MathF.Sin(angle);
                g.DrawEllipse(point, x - size / 2, y - size / 2, size, size);
            }
        }
        var pixels = new byte[Size * Size * 4];
        DashboardPanel.CopyRgba(bitmap, pixels);
        return pixels;
    }
}

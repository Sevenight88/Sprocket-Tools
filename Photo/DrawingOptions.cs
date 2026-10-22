using System.Globalization;
using System.Numerics;

namespace SprocketTools;

/// Pure drawing operations: no changes to the live gun, paint or vehicle.
internal static class DrawingOptions
{
    internal static float Strength(float percent) => float.IsFinite(percent) ? Math.Clamp(percent / 100, 0, 1) : 1;
    internal static void Ink(byte[] rgb, int q, byte grey, float strength)
    {
        for (int c = 0; c < 3; c++) rgb[q + c] = (byte)MathF.Round(rgb[q + c] * (1 - strength) + grey * strength);
    }
    internal static string Weight(float kg) => float.IsFinite(kg) && kg > 0 ? (kg / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " 吨" : "";
    internal static byte[] Blueprint(byte[] lines, int width = 0, int height = 0, int gridStep = 0, float gridStrength = 0.2f)
    {
        if (gridStep > 0 && (width <= 0 || height <= 0 || (long)width * height * 3 != lines.Length))
            throw new ArgumentException("蓝图网格的尺寸必须与图片一致。");
        gridStrength = float.IsFinite(gridStrength) ? Math.Clamp(gridStrength, 0, 1) : 0.2f;
        var blue = new byte[lines.Length];
        byte[] paper = { 19, 55, 91 }, ink = { 230, 245, 255 };
        for (int p = 0; p < lines.Length; p += 3)
        {
            float grid = 0;
            if (gridStep > 0)
            {
                int x = p / 3 % width, y = p / 3 / width;
                bool vertical = x % gridStep == 0, horizontal = y % gridStep == 0;
                if (vertical || horizontal)
                    grid = gridStrength * ((vertical && x / gridStep % 4 == 0 || horizontal && y / gridStep % 4 == 0) ? 1 : 0.45f);
            }
            // Grid is paper decoration beneath the ink, so it cannot darken text or vehicle contours.
            for (int c = 0; c < 3; c++)
            {
                int background = paper[c] + (int)((ink[c] - paper[c]) * grid);
                blue[p + c] = (byte)(background + (ink[c] - background) * (255 - lines[p]) / 255);
            }
        }
        return blue;
    }

    internal sealed record Ghost(int View, Drawing.Shape[] Shapes, Vector3 Pivot, Vector3 Tip, Vector3[] Arc, string Label, bool FullCircle = false);
    internal static List<Ghost> Motion(IReadOnlyList<Drawing.Shape> barrel, Vector3 pivot, Vector3 tip,
        Vector3 right, Vector3 up, float minElevation, float maxElevation, float minTraverse, float maxTraverse, bool elevation, bool traverse)
    {
        var result = new List<Ghost>();
        void Add(float degrees, bool vertical)
        {
            if (!float.IsFinite(degrees) || MathF.Abs(degrees) < 0.05f || MathF.Abs(degrees) > 180) return;
            var axis = vertical ? -right : up; // positive elevation raises a +Z gun
            if (axis.LengthSquared() < 0.5f) return;
            axis = Vector3.Normalize(axis);
            Vector3 Turn(Vector3 p, float angle) => pivot + Vector3.Transform(p - pivot, Quaternion.CreateFromAxisAngle(axis, angle * MathF.PI / 180));
            var shapes = barrel.Select(s => Drawing.Weld(s.P.Select(p => Turn(p, degrees)).ToArray(), s.T)).ToArray();
            // The angle arc follows the muzzle's sweep, preserving any offset between the barrel and trunnion.
            int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(degrees) / 2));
            var arc = Enumerable.Range(0, steps + 1).Select(i => Turn(tip, degrees * i / steps)).ToArray();
            string label = MathF.Abs(degrees).ToString("0.#", CultureInfo.InvariantCulture) + "° " +
                (vertical ? degrees > 0 ? "仰角" : "俯角" : degrees > 0 ? "向右转向" : "向左转向");
            result.Add(new Ghost(vertical ? 2 : 0, shapes, pivot, Turn(tip, degrees), arc, label));
        }
        if (elevation) { Add(minElevation, true); if (maxElevation != minElevation) Add(maxElevation, true); }
        if (traverse) { Add(minTraverse, false); if (maxTraverse != minTraverse) Add(maxTraverse, false); }
        return result;
    }

    internal static List<Ghost> TurretMotion(IReadOnlyList<Drawing.Shape> barrel, Vector3 pivot, Vector3 tip, Vector3 up, float min, float max)
    {
        if (!float.IsFinite(min) || !float.IsFinite(max) || max <= min || min < -360 || max > 360 || up.LengthSquared() < 0.5f) return new();
        up = Vector3.Normalize(up);
        Vector3 Turn(Vector3 p, float angle) => pivot + Vector3.Transform(p - pivot, Quaternion.CreateFromAxisAngle(up, angle * MathF.PI / 180));
        bool full = max - min >= 359.9f;
        int steps = Math.Max(2, (int)MathF.Ceiling(Math.Min(360, max - min) / 2));
        var arc = Enumerable.Range(0, steps + 1).Select(i => Turn(tip, min + Math.Min(360, max - min) * i / steps)).ToArray();
        if (full) return new() { new Ghost(0, Array.Empty<Drawing.Shape>(), pivot, arc[0], arc, "360° 炮塔旋转", true) };
        Ghost Limit(float degrees, bool first) => new(0,
            barrel.Select(s => Drawing.Weld(s.P.Select(p => Turn(p, degrees)).ToArray(), s.T)).ToArray(), pivot, Turn(tip, degrees),
            first ? arc : Array.Empty<Vector3>(), degrees.ToString("0.#", CultureInfo.InvariantCulture) + "° 炮塔" + (first ? "下限" : "上限"));
        return new() { Limit(min, true), Limit(max, false) };
    }

    internal static void Segment(byte[] rgb, int w, int h, Vector3 a, Vector3 b, byte grey, bool dashed)
    {
        float length = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
        if (!float.IsFinite(length)) return;
        int count = Math.Clamp((int)MathF.Ceiling(length), 1, Math.Max(w, h) * 3);
        for (int i = 0; i <= count; i++)
        {
            if (dashed && (i / 10) % 2 != 0) continue;
            var p = Vector3.Lerp(a, b, i / (float)count);
            int x = (int)MathF.Round(p.X), y = (int)MathF.Round(p.Y);
            if (x >= 0 && y >= 0 && x < w && y < h) Ink(rgb, (y * w + x) * 3, grey, 1);
        }
    }
    internal static void DrawMotion(IReadOnlyList<Ghost> motion, byte[][] sheets, Drawing.View[] views, int w, int h, (int X, int Y)[] at)
    {
        var labels = new List<(int X, int Y, int W, int H)>();
        foreach (var ghost in motion)
        {
            int i = ghost.View;
            var v = views[i];
            var ink = new bool[v.Width * v.Height];
            Drawing.Lines(ghost.Shapes, v, Drawing.Depths(ghost.Shapes, v), ink, 10);
            foreach (var sheet in sheets)
                for (int y = 0; y < v.Height; y++)
                    for (int x = 0; x < v.Width; x++)
                        if (ink[y * v.Width + x]) DrawingOptions.Ink(sheet, ((at[i].Y + y) * w + at[i].X + x) * 3, 70, 0.8f);
            Vector3 OnSheet(Vector3 p) => v.Project(p) + new Vector3(at[i].X, at[i].Y, 0);
            var tip = ghost.FullCircle ? ghost.Arc.Select(OnSheet).OrderByDescending(p => p.Y).First() : OnSheet(ghost.Tip);
            var pivot = OnSheet(ghost.Pivot);
            var words = Drawing.Words(ghost.Label, 27, false, Math.Max(30, Math.Min(360, v.Width - 16)));
            int tx = Math.Clamp((int)tip.X - words.W / 2, at[i].X + 6, at[i].X + v.Width - words.W - 6);
            int ty = Math.Clamp((int)tip.Y + (tip.Y >= pivot.Y ? words.H + 22 : -22), at[i].Y + words.H + 6, at[i].Y + v.Height - 6);
            // Stagger nearby labels, staying within this view's reserved space.
            for (int attempt = 0; attempt < 12 && labels.Any(r => tx < r.X + r.W + 8 && tx + words.W + 8 > r.X && ty > r.Y - r.H - 8 && ty - words.H - 8 < r.Y); attempt++)
                ty = Math.Clamp(ty + (tip.Y >= pivot.Y ? -1 : 1) * (words.H + 12), at[i].Y + words.H + 6, at[i].Y + v.Height - 6);
            labels.Add((tx, ty, words.W, words.H));
            foreach (var sheet in sheets)
            {
                if (!ghost.FullCircle) DrawingOptions.Segment(sheet, w, h, pivot, tip, 115, true);
                for (int k = 1; k < ghost.Arc.Length; k++) DrawingOptions.Segment(sheet, w, h, OnSheet(ghost.Arc[k - 1]), OnSheet(ghost.Arc[k]), 100, false);
                DrawingOptions.Segment(sheet, w, h, tip, new Vector3(Math.Clamp(tip.X, tx, tx + words.W), tip.Y >= ty ? ty + 4 : ty - words.H - 4, 0), 115, false);
                Drawing.Stamp(sheet, w, h, tx, ty, words, 40);
            }
        }
    }

}

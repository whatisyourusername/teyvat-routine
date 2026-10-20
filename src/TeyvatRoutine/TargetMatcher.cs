using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TeyvatRoutine;
// Multiscale template localization. This is not a semantic tree classifier.
public static class TargetMatcher
{
    public sealed record Analysis(TargetHit? Best, bool Accepted, string Reason);
    private sealed record Candidate(Rectangle Box, double Score, int TemplateIndex = -1);
    private sealed class Pattern
    {
        public readonly float[] Values;
        public readonly int Grid;
        public readonly double Norm;
        public Pattern(float[] pixels, int width, int height, int grid)
        {
            Grid = grid; Values = new float[grid * grid]; double sum = 0;
            for (int y = 0; y < grid; y++) for (int x = 0; x < grid; x++)
            { float v = pixels[Math.Min(height - 1, (int)((y + .5) * height / grid)) * width + Math.Min(width - 1, (int)((x + .5) * width / grid))]; Values[y * grid + x] = v; sum += v; }
            double mean = sum / Values.Length, square = 0;
            for (int i = 0; i < Values.Length; i++) { Values[i] -= (float)mean; square += Values[i] * Values[i]; }
            Norm = Math.Sqrt(square);
        }
        public double Score(float[] frame, int stride, Rectangle box)
        {
            if (Norm < 8 * Math.Sqrt(Values.Length)) return 0;
            double sum = 0, square = 0, dot = 0;
            for (int y = 0; y < Grid; y++)
            {
                int row = (box.Top + Math.Min(box.Height - 1, (int)((y + .5) * box.Height / Grid))) * stride;
                for (int x = 0; x < Grid; x++)
                {
                    double v = frame[row + box.Left + Math.Min(box.Width - 1, (int)((x + .5) * box.Width / Grid))];
                    sum += v; square += v * v; dot += v * Values[y * Grid + x];
                }
            }
            double variance = square - sum * sum / Values.Length;
            return variance < 64 * Values.Length ? 0 : Math.Clamp(dot / (Norm * Math.Sqrt(variance)), 0, 1);
        }
    }
    public static TargetHit? Find(Bitmap screen, TargetNavigation settings, CancellationToken token = default)
    {
        var result = Analyze(screen, settings, token); return result.Accepted ? result.Best : null;
    }
    public static Analysis Analyze(Bitmap screen, TargetNavigation settings, CancellationToken token = default)
    {
        double resize = Math.Min(1, 640.0 / screen.Width);
        int width = Math.Max(1, (int)Math.Round(screen.Width * resize)), height = Math.Max(1, (int)Math.Round(screen.Height * resize));
        using var small = Resize(screen, width, height); var pixels = Gray(small); var frameRgb = Vision.Pixels(small);
        // A tall reward tree often reaches the top edge: never discard that region.
        var view = new Rectangle(0, 0, width, height);
        var candidates = new List<Candidate>();
        int templateIndex = -1;
        foreach (var template in settings.Templates)
        {
            templateIndex++;
            token.ThrowIfCancellationRequested(); using var decoded = Vision.Decode(template);
            foreach (double scale in Enumerable.Range(-9, 21).Select(i => Math.Pow(1.12, i)).Concat(new[] { .5, .67, .85, 1.25, 1.6, 2.0, 2.6, 3.3 }).Distinct().OrderBy(s => s))
            {
                int tw = (int)Math.Round(template.Width * resize * scale), th = (int)Math.Round(template.Height * resize * scale);
                if (tw < 12 || th < 12 || tw > view.Width || th > view.Height) continue;
                using var patch = Resize(decoded, tw, th); var gray = Gray(patch); var patchRgb = Vision.Pixels(patch);
                var coarse = new Pattern(gray, tw, th, 8); var fine = new Pattern(gray, tw, th, 16);
                var verification = new Pattern(gray, tw, th, 32);
                var seeds = new List<Candidate>();
                for (int y = view.Top; y <= view.Bottom - th; y += 4)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = view.Left; x <= view.Right - tw; x += 4)
                    {
                        var box = new Rectangle(x, y, tw, th); double score = coarse.Score(pixels, width, box);
                        if (score < .4 || (seeds.Count >= 8 && score <= seeds[^1].Score)) continue;
                        int near = seeds.FindIndex(c => Math.Abs(c.Box.X - x) < tw * .4 && Math.Abs(c.Box.Y - y) < th * .4);
                        if (near >= 0) { if (seeds[near].Score >= score) continue; seeds.RemoveAt(near); }
                        seeds.Add(new(box, score)); seeds.Sort((a, b) => b.Score.CompareTo(a.Score));
                        if (seeds.Count > 8) seeds.RemoveAt(8);
                    }
                }
                foreach (var seed in seeds)
                {
                    token.ThrowIfCancellationRequested(); Candidate best = new(seed.Box, 0);
                    for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                    {
                        var box = new Rectangle(seed.Box.X + dx, seed.Box.Y + dy, tw, th);
                        if (!view.Contains(box)) continue;
                        double score = fine.Score(pixels, width, box);
                        if (score > best.Score) best = new(box, score);
                    }
                    Candidate verified = new(best.Box, 0);
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        var box = new Rectangle(best.Box.X + dx, best.Box.Y + dy, tw, th);
                        if (!view.Contains(box)) continue;
                        double score = .5 * fine.Score(pixels, width, box) + .5 * verification.Score(pixels, width, box);
                        double color = ChromaDifference(frameRgb, width, box, patchRgb, tw, th);
                        score *= Math.Clamp(1 - Math.Max(0, color - .04) * 4, 0, 1);
                        if (score > verified.Score) verified = new(box, score);
                    }
                    candidates.Add(verified with { TemplateIndex = templateIndex });
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if (candidates.Count == 0) return new(null, false, "특징 후보 없음 · 다른 거리의 이미지 또는 선택 영역 확인");
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score)); var first = candidates[0];
        double runner = candidates.Where(c => !SameObject(first.Box, c.Box)).Select(c => c.Score).DefaultIfEmpty(0).Max();
        var b = first.Box;
        var hit = new TargetHit(new Rectangle((int)Math.Round(b.X / resize), (int)Math.Round(b.Y / resize), (int)Math.Round(b.Width / resize), (int)Math.Round(b.Height / resize)), first.Score, runner, screen.Width, screen.Height);
        int supportingTemplates = candidates.Where(c => c.Score >= settings.MatchThreshold && SameObject(first.Box, c.Box)).Select(c => settings.Templates[c.TemplateIndex].PngBase64).Distinct().Count();
        // A below-threshold distractor must not veto agreement between two registered views.
        // A second independently acceptable object still requires the normal ambiguity margin.
        bool ambiguous = first.Score - runner < settings.AmbiguityMargin && !(supportingTemplates >= 2 && runner < settings.MatchThreshold);
        bool accepted = first.Score >= settings.MatchThreshold && !ambiguous;
        return new(hit, accepted, first.Score < settings.MatchThreshold ? $"유사도 부족 {first.Score:P1} < {settings.MatchThreshold:P1}" : ambiguous ? $"후보 구분 부족 · 1위 {first.Score:P1}, 2위 {runner:P1}" : $"검출 · {first.Score:P1}, 2위 {runner:P1} · 등록 모습 {supportingTemplates}개 일치");
    }
    private static bool SameObject(Rectangle a, Rectangle b)
        => Math.Abs(a.Left + a.Width / 2.0 - b.Left - b.Width / 2.0) < Math.Max(a.Width, b.Width) * .55 && Math.Abs(a.Top + a.Height / 2.0 - b.Top - b.Height / 2.0) < Math.Max(a.Height, b.Height) * .55;
    private static double ChromaDifference(byte[] frame, int stride, Rectangle box, byte[] patch, int width, int height)
    {
        double difference = 0;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int px = Math.Min(width - 1, (int)((x + .5) * width / 8));
            int py = Math.Min(height - 1, (int)((y + .5) * height / 8));
            int a = ((box.Top + py) * stride + box.Left + px) * 3, b = (py * width + px) * 3;
            double fa = Math.Max(30, frame[a] + frame[a+1] + frame[a+2]);
            double fb = Math.Max(30, patch[b] + patch[b+1] + patch[b+2]);
            for (int c = 0; c < 3; c++) difference += Math.Abs(frame[a+c] / fa - patch[b+c] / fb);
        }
        return difference / (64 * 3);
    }
    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result); g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY); g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes); return result;
    }
    private static float[] Gray(Bitmap bitmap)
    {
        var rgb = Vision.Pixels(bitmap); var gray = new float[bitmap.Width * bitmap.Height];
        for (int i = 0; i < gray.Length; i++) gray[i] = rgb[i * 3] * .114f + rgb[i * 3 + 1] * .587f + rgb[i * 3 + 2] * .299f;
        return gray;
    }
}



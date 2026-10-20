using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace TeyvatRoutine;
public static class RecognitionTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        void Test(string name, Action body) { body(); results.Add("PASS  " + name); }
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        using var patch = new Bitmap(40, 48, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(patch))
        { g.Clear(Color.FromArgb(40, 50, 60)); g.FillRectangle(Brushes.Salmon, 5, 8, 12, 26); g.FillEllipse(Brushes.LightBlue, 18, 2, 18, 22); g.DrawLine(Pens.White, 4, 42, 34, 28); }
        var checkpoint = Vision.Crop(patch, new Rectangle(0, 0, 40, 48));
        Test("Checkpoint recognition tolerates brightness shift and searches odd radii", () =>
        {
            using var scene = new Bitmap(200, 120, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scene))
            {
                using var attributes = new ImageAttributes(); attributes.SetColorMatrix(new ColorMatrix(new[] { new float[] {.7f,0,0,0,0}, new float[] {0,.7f,0,0,0}, new float[] {0,0,.7f,0,0}, new float[] {0,0,0,1,0}, new float[] {.08f,.08f,.08f,0,1} }));
                g.DrawImage(patch, new Rectangle(70, 30, 40, 48), 0, 0, 40, 48, GraphicsUnit.Pixel, attributes);
            }
            checkpoint.X = 70; checkpoint.Y = 30; checkpoint.SearchRadius = 3;
            Check(Vision.Score(scene, checkpoint) >= .86, "Brightness variation rejected");
            using var blank = new Bitmap(200, 120, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(blank)) g.Clear(Color.Salmon);
            Check(Vision.Score(blank, checkpoint) < .65, "Flat unrelated area accepted");
        });
        Test("Tree matching includes the top and side edges", () =>
        {
            using var scene = new Bitmap(240, 140, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scene)) { g.Clear(Color.DarkSlateGray); g.DrawImageUnscaled(patch, 0, 0); }
            var hit = TargetMatcher.Find(scene, new() { Templates = new() { checkpoint } });
            Check(hit != null && hit.Bounds.Left <= 3 && hit.Bounds.Top <= 3, "Edge target excluded");
        });
        Test("Tree diagnostics explain a rejected high-threshold candidate", () =>
        {
            using var scene = new Bitmap(240, 140, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scene)) { g.Clear(Color.DarkSlateGray); g.DrawImageUnscaled(patch, 100, 40); }
            var analysis = TargetMatcher.Analyze(scene, new() { Templates = new() { checkpoint }, MatchThreshold = 1.01 });
            Check(!analysis.Accepted && analysis.Best != null && analysis.Reason.Contains("유사도"), "Rejected candidate is invisible to diagnostics");
        });
        Test("Camera mouse routing ignores stale cursor bounds but menu clicks remain guarded", () =>
        {
            Check(!MouseRouting.PauseForClick(true, false, false), "Hidden camera cursor stops recording");
            Check(MouseRouting.PauseForClick(false, false, false) && !MouseRouting.PauseForClick(false, true, true), "Menu click routing invalid");
            Check(MouseRouting.RelativeMode(true, false, false) && !MouseRouting.RelativeMode(true, true, true), "Auto mode failed");
            Check(MouseRouting.RelativeMode(false, true, true) && MouseRouting.RelativeMode(true, null, true), "Manual/fallback mode lost");
        });
        Test("Recognition profile migration relaxes defaults but preserves explicit tuning", () =>
        {
            var p = Profile.Create(); p.Version = 2; var s = p.Get("login");
            s.Before = new() { Threshold = .91, SearchRadius = 8 }; s.After = new() { Threshold = .82, SearchRadius = 24 };
            p.Get("collect").Navigation.MatchThreshold = .84;
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRecognition-" + Guid.NewGuid() + ".json");
            try { ProfileStore.Save(p, path); var loaded = ProfileStore.Load(path); Check(loaded.Version == 3 && loaded.Get("login").Before!.Threshold == .86 && loaded.Get("login").Before!.SearchRadius == 18 && loaded.Get("login").After!.Threshold == .82 && loaded.Get("login").After!.SearchRadius == 24 && loaded.Get("collect").Navigation.MatchThreshold == .78, "Migration changed custom tuning or missed defaults"); }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Segment Aligned()
        {
            var s = Profile.Create().Get("battle"); s.Width = 1000; s.Height = 600;
            s.Alignment = new() { Enabled = true, Reference = new() { X = 450, Y = 250, Width = 100, Height = 100 } }; return s;
        }
        TargetHit Hit(int x) => new(new Rectangle(x, 250, 100, 100), .95, .5, 1000, 600);
        Test("Visual alignment corrects direction and requires two aligned observations", () =>
        {
            var r = new AlignmentRuntime { Hits = new(new[] { Hit(650), Hit(450), Hit(450) }) };
            ViewAligner.Align(r, Aligned(), () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 1 && r.Inputs[0].Kind == InputKind.MoveRelative && r.Inputs[0].X > 0 && r.ReleaseCount > 0, "Wrong correction or movement input");
        });
        Test("Missing alignment target stops within the search limit", () =>
        {
            var r = new AlignmentRuntime(); bool failed = false;
            try { ViewAligner.Align(r, Aligned(), () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult(); } catch (TimeoutException) { failed = true; }
            Check(failed && r.Inputs.Count == 32 && r.Inputs.All(i => i.Kind == InputKind.MoveRelative), "Unbounded alignment or forward motion");
        });
        Test("Completion and cancellation interrupt visual alignment", () =>
        {
            var r = new AlignmentRuntime(); Check(ViewAligner.Align(r, Aligned(), () => true, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult() && r.Inputs.Count == 0, "Completion still moves");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool stopped = false;
            try { ViewAligner.Align(r, Aligned(), () => false, _ => { }, cancel.Token, 1).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && r.ReleaseCount >= 2, "Cancellation did not release");
        });
        return results;
    }
    private sealed class AlignmentRuntime : IRuntime
    {
        public Queue<TargetHit> Hits = new(); public List<RecordedInput> Inputs = new(); public int ReleaseCount;
        public bool Exists => true; public void Ensure(Segment s) { }
        public double Match(Checkpoint c) => 0;
        public void Play(RecordedInput input) => Inputs.Add(input);
        public void Release() => ReleaseCount++;
        public TargetHit? LocateTarget(TargetNavigation settings, CancellationToken token) => Hits.Count == 0 ? null : Hits.Dequeue();
    }
    public static string TestUserTemplates(string profilePath)
    {
        var p = ProfileStore.Load(profilePath); var n = p.Get("collect").Navigation;
        var lines = new List<string>();
        foreach (int index in new[] { 2, 0 }.Where(i => i < n.Templates.Count))
        {
            var c = n.Templates[index]; using var patch = Vision.Decode(c);
            using var frame = new Bitmap(p.ResolutionWidth, p.ResolutionHeight, PixelFormat.Format24bppRgb);
            double scale = index == 2 ? 1 : 1.12;
            var expected = new Rectangle(index == 2 ? c.X : 510, index == 2 ? c.Y : 260, (int)(c.Width * scale), (int)(c.Height * scale));
            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(Color.FromArgb(35, 42, 57)); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                using var attributes = new ImageAttributes(); float brightness = index == 2 ? 1 : .8f;
                attributes.SetColorMatrix(new ColorMatrix(new[] { new float[] {brightness,0,0,0,0}, new float[] {0,brightness,0,0,0}, new float[] {0,0,brightness,0,0}, new float[] {0,0,0,1,0}, new float[] {0,0,0,0,1} }));
                g.DrawImage(patch, expected, 0, 0, patch.Width, patch.Height, GraphicsUnit.Pixel, attributes);
            }
            var watch = System.Diagnostics.Stopwatch.StartNew(); var result = TargetMatcher.Analyze(frame, n);
            bool correct = result.Accepted && result.Best != null && Math.Abs(result.Best.Bounds.X - expected.X) < 20 && Math.Abs(result.Best.Bounds.Y - expected.Y) < 20;
            lines.Add($"{(correct ? "PASS" : "FAIL")} real saved template {index}: scale {scale}, expected {expected}, result {result.Best?.Bounds}, {result.Reason}, {watch.ElapsedMilliseconds}ms");
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine + "Offline synthetic scenes built from saved templates. No live game input or capture.";
    }
}

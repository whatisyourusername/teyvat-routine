using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace TeyvatRoutine;
public static class VisionFlowTests
{
    public static List<string> Run()
    {
        var results = new List<string>();
        void Test(string name, Action test) { test(); results.Add("PASS  " + name); }
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        using var patch = new Bitmap(48, 64);
        using (var g = Graphics.FromImage(patch))
        {
            var random = new Random(46);
            for (int y = 0; y < 64; y += 4) for (int x = 0; x < 48; x += 4)
            { using var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256))); g.FillRectangle(brush, x, y, 4, 4); }
        }
        var checkpoint = Vision.Crop(patch, new Rectangle(0, 0, 48, 64));
        Checkpoint Copy() => System.Text.Json.JsonSerializer.Deserialize<Checkpoint>(System.Text.Json.JsonSerializer.Serialize(checkpoint))!;
        Segment Segment(string id = "enter_domain", bool early = true)
        {
            var s = Profile.Create().Get(id); s.Navigation.Enabled = false; s.StopOnCompletion = early;
            s.Width = 960; s.Height = 540; s.Before = Copy(); s.After = Copy(); s.TimeoutSeconds = 1; s.DurationMs = 1000;
            s.Inputs = new() { new() { AtMs = 0, Kind = InputKind.KeyDown, Code = 0x57 }, new() { AtMs = 1000, Kind = InputKind.KeyUp, Code = 0x57 } };
            return s;
        }
        Test("Early completion during held input releases keys and skips the remaining recording", () =>
        {
            var s = Segment(); var r = new Runtime(); r.MatchScore = c => c == s.After ? (r.Inputs.Count > 0 ? 1 : 0) : 1;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            new RoutineEngine(r, _ => { }).Run(new[] { new PlanItem(s) }, CancellationToken.None, 2, 5).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 1 && r.Held == 0 && watch.ElapsedMilliseconds < 800, "Completion did not interrupt the wait");
        });
        Test("Already completed stage skips precondition and counts a reward only once", () =>
        {
            var s = Segment("collect"); var r = new Runtime { MatchScore = c => 0 };
            r.MatchScore = c => c == s.After ? 1 : 0;
            var engine = new RoutineEngine(r, _ => { }); engine.Run(new[] { new PlanItem(s) }, CancellationToken.None, 2, 2).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 0 && engine.CompletedRewards == 1, "Already complete stage played inputs or double-counted");
        });
        Test("Early completion advances to the next stage", () =>
        {
            var first = Segment(); var second = Segment("daily", false); second.DurationMs = 1; second.Inputs[1].AtMs = 1;
            var r = new Runtime(); r.MatchScore = c => c == first.After ? (r.Inputs.Count > 0 ? 1 : 0) : 1;
            var updates = new List<RunUpdate>(); new RoutineEngine(r, updates.Add).Run(new[] { new PlanItem(first), new PlanItem(second) }, CancellationToken.None, 1, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 3 && updates[^1].FinishedStages == 2, "Next stage was not run");
        });
        Test("A single completion-image flicker does not skip the stage", () =>
        {
            var s = Segment(); s.DurationMs = 30; s.Inputs[1].AtMs = 30; var r = new Runtime(); bool flickered = false;
            r.MatchScore = c =>
            {
                if (c != s.After || r.Inputs.Count >= 2) return 1;
                if (r.Inputs.Count == 1 && !flickered) { flickered = true; return 1; }
                return 0;
            };
            new RoutineEngine(r, _ => { }).Run(new[] { new PlanItem(s) }, CancellationToken.None, 2, 2).GetAwaiter().GetResult();
            Check(flickered && r.Inputs.Count == 2 && r.Held == 0, "Flicker caused early completion");
        });
        Test("Disabling early detection preserves full recording playback", () =>
        {
            var s = Segment(early: false); s.DurationMs = 1; s.Inputs[1].AtMs = 1;
            var r = new Runtime(); new RoutineEngine(r, _ => { }).Run(new[] { new PlanItem(s) }, CancellationToken.None, 1, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 2, "Disabled option still skipped inputs");
        });
        Test("Global target matching finds a translated and resized template", () =>
        {
            using var scene = new Bitmap(320, 180);
            using (var g = Graphics.FromImage(scene)) { g.Clear(Color.DarkSlateGray); g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(patch, new Rectangle(210, 55, 60, 80)); }
            var n = new TargetNavigation { Templates = new() { checkpoint } }; var hit = TargetMatcher.Find(scene, n);
            Check(hit != null && Math.Abs(hit.Bounds.X - 210) <= 5 && Math.Abs(hit.Bounds.Y - 55) <= 5 && hit.Score >= n.MatchThreshold, "Scaled target not localized");
        });
        Test("Target matching rejects absent and ambiguous duplicate objects", () =>
        {
            var n = new TargetNavigation { Templates = new() { checkpoint } }; using var scene = new Bitmap(320, 180);
            using (var g = Graphics.FromImage(scene)) g.Clear(Color.DarkSlateGray);
            Check(TargetMatcher.Find(scene, n) == null, "Blank scene matched");
            using (var g = Graphics.FromImage(scene)) { g.DrawImageUnscaled(patch, 40, 50); g.DrawImageUnscaled(patch, 220, 50); }
            Check(TargetMatcher.Find(scene, n) == null, "Ambiguous objects accepted");
        });
        Test("Target matching supports prompt cancellation", () =>
        {
            using var scene = new Bitmap(640, 360); using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool stopped = false;
            try { TargetMatcher.Find(scene, new() { Templates = new() { checkpoint } }, cancel.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "Matcher ignored cancellation");
        });
        TargetHit Hit(int x) => new(new Rectangle(x, 120, 80, 100), .98, .3, 960, 540);
        Segment Navigator()
        {
            var s = Segment("collect"); s.Navigation.Enabled = true; s.Navigation.Templates.Add(checkpoint); s.Navigation.Arrival = Copy(); s.Navigation.ForwardPulseMs = 20; s.Navigation.MaxSearchTurns = 2;
            s.Navigation.RecenterBeforeSearch = false; // Test tracking independently of the optional initial click.
            s.Navigation.ZoomOutSteps = 0;
            return s;
        }
        Test("Camera recenter clicks the middle button once and discards the pre-click bearing", () =>
        {
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true;
            var r = new Runtime { Observations = new(new[] { new TargetObservation(Hit(700), 0), new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            r.OnPlay = i => { if (i.Kind == InputKind.KeyDown) Check(r.ObservationCount == 3, "Walked without two fresh post-click frames"); };
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 3 && r.Inputs[0].Kind == InputKind.MouseDown && r.Inputs[0].Code == 2 && r.Inputs[1].Kind == InputKind.MouseUp && r.Inputs[1].Code == 2 && r.Inputs[2].Kind == InputKind.KeyDown && r.Held == 0, "Wrong middle-click sequence or stale turn");
        });
        Test("Missing target does not repeat camera recenter or walk blindly", () =>
        {
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true; var r = new Runtime(); bool stopped = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult(); } catch (TimeoutException) { stopped = true; }
            Check(stopped && r.Inputs.Count == 4 && r.Inputs.Count(i => i.Kind == InputKind.MouseDown) == 1 && r.Inputs.Skip(2).All(i => i.Kind == InputKind.MoveRelative) && r.Held == 0, "Recenter repeated or target loss caused walking");
        });
        Test("Cancellation during recenter releases the middle button", () =>
        {
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true; var r = new Runtime(); using var cancel = new CancellationTokenSource();
            r.OnPlay = i => { if (i.Kind == InputKind.MouseDown) cancel.Cancel(); }; bool stopped = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, cancel.Token, 1, 0).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && r.Inputs.Count == 1 && r.Held == 0, "Cancellation left middle button held or played more inputs");
        });
        Test("Foreground loss during recenter releases the middle button and stops", () =>
        {
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true; var r = new Runtime { LoseFocusAfterInput = true }; bool stopped = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult(); } catch (InvalidOperationException) { stopped = true; }
            Check(stopped && r.Inputs.Count == 1 && r.Held == 0, "Foreground loss did not stop recenter");
        });
        Test("Completion while recentering releases the button and skips navigation", () =>
        {
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true; var r = new Runtime();
            bool result = TargetNavigator.Approach(r, s, () => r.Inputs.Count > 0, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(result && r.Inputs.Count == 1 && r.Held == 0, "Completion did not interrupt recenter");
        });
        Test("Already completed navigation skips camera recenter", () =>
        {
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true; var r = new Runtime();
            bool result = TargetNavigator.Approach(r, s, () => true, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(result && r.Inputs.Count == 0 && r.ObservationCount == 0, "Already completed stage received a click");
        });
        Test("Camera preparation defaults for older settings preserve explicit opt-outs", () =>
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<TargetNavigation>("{}")!;
            var disabled = System.Text.Json.JsonSerializer.Deserialize<TargetNavigation>(System.Text.Json.JsonSerializer.Serialize(new TargetNavigation { RecenterBeforeSearch = false, ZoomOutSteps = 0, AdaptivePitch = false }))!;
            Check(old.RecenterBeforeSearch && old.ZoomOutSteps == 3 && old.AdaptivePitch && !disabled.RecenterBeforeSearch && disabled.ZoomOutSteps == 0 && !disabled.AdaptivePitch, "Camera option defaults or saved opt-outs are wrong");
        });
        Test("Zoom-out precedes middle click and both require fresh target observations", () =>
        {
            var s = Navigator(); s.Navigation.ZoomOutSteps = 3; s.Navigation.RecenterBeforeSearch = true;
            var r = new Runtime { Observations = new(new[] { new TargetObservation(Hit(700), 0), new TargetObservation(Hit(700), 0), new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            r.OnPlay = i => { if (i.Kind == InputKind.KeyDown) Check(r.ObservationCount == 4, "Walked using a pre-zoom or pre-click observation"); };
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 6 && r.Inputs.Take(3).All(i => i.Kind == InputKind.Wheel && i.Code == -120) && r.Inputs[3].Kind == InputKind.MouseDown && r.Inputs[4].Kind == InputKind.MouseUp && r.Inputs[5].Kind == InputKind.KeyDown && r.Held == 0, "Camera preparation order is wrong");
        });
        Test("Zoom-out is bounded and never repeats when a target remains missing", () =>
        {
            var s = Navigator(); s.Navigation.ZoomOutSteps = 6; var r = new Runtime(); bool stopped = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult(); } catch (TimeoutException) { stopped = true; }
            Check(stopped && r.Inputs.Count == 8 && r.Inputs.Count(i => i.Kind == InputKind.Wheel) == 6 && r.Inputs.All(i => i.Kind is InputKind.Wheel or InputKind.MoveRelative) && r.Held == 0, "Zoom repeated or walked without a target");
            foreach (int invalid in new[] { -1, 7 })
            {
                s.Navigation.ZoomOutSteps = invalid;
                Check(s.Navigation.Validate().Count > 0, "Out-of-range zoom settings were accepted");
            }
        });
        Test("Cancellation and foreground loss interrupt zoom before the next wheel step", () =>
        {
            foreach (bool focusLost in new[] { false, true })
            {
                var s = Navigator(); s.Navigation.ZoomOutSteps = 3; s.Navigation.RecenterBeforeSearch = true;
                var r = new Runtime { LoseFocusAfterInput = focusLost }; using var cancel = new CancellationTokenSource(); bool stopped = false;
                if (!focusLost) r.OnPlay = _ => cancel.Cancel();
                try { TargetNavigator.Approach(r, s, () => false, _ => { }, cancel.Token, 1, 0).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { stopped = !focusLost; }
                catch (InvalidOperationException) { stopped = focusLost; }
                Check(stopped && r.Inputs.Count == 1 && r.Inputs[0].Kind == InputKind.Wheel && r.Held == 0, "Zoom continued after interruption");
            }
        });
        Test("Completion interrupts zoom and skips the later recenter click", () =>
        {
            var s = Navigator(); s.Navigation.ZoomOutSteps = 3; s.Navigation.RecenterBeforeSearch = true; var r = new Runtime();
            bool result = TargetNavigator.Approach(r, s, () => r.Inputs.Count > 0, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(result && r.Inputs.Count == 1 && r.Inputs[0].Kind == InputKind.Wheel && r.Held == 0, "Completion did not interrupt camera preparation");
        });
        Test("Arrival revealed after zoom skips recenter and stops without reward inputs", () =>
        {
            var s = Navigator(); s.Navigation.ZoomOutSteps = 3; s.Navigation.RecenterBeforeSearch = true;
            var r = new Runtime { Observations = new(new[] { new TargetObservation(null, 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 3 && r.Inputs.All(i => i.Kind == InputKind.Wheel) && r.Held == 0, "Recenter or reward inputs followed arrival");
        });
        Test("Navigation aligns, approaches in a short pulse, and stops at two arrival frames", () =>
        {
            var s = Navigator(); var r = new Runtime();
            r.Observations = new(new[] { new TargetObservation(Hit(700), 0), new TargetObservation(Hit(700), 0), new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0), new TargetObservation(null, 1), new TargetObservation(null, 1) });
            bool complete = TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(!complete && r.Inputs.Any(i => i.Kind == InputKind.MoveRelative && i.X > 0) && r.Inputs.Count(i => i.Kind == InputKind.KeyDown && i.Code == 0x57) == 1 && r.Held == 0 && r.ObservationCount == 6, "Wrong approach sequence");
        });
        Test("Missing target never walks and bounded search fails without advancing", () =>
        {
            var s = Navigator(); var r = new Runtime(); bool failed = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult(); } catch (TimeoutException) { failed = true; }
            Check(failed && r.Inputs.Count == 2 && r.Inputs.All(i => i.Kind == InputKind.MoveRelative) && r.Held == 0, "Walked without a target");
        });
        Test("Transient occlusion waits and resumes only after two fresh target observations", () =>
        {
            var s = Navigator(); var r = new Runtime();
            r.Observations = new(new[] { new TargetObservation(Hit(300), 0), new TargetObservation(Hit(300), 0), new TargetObservation(null, 0), new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0), new TargetObservation(null, 1), new TargetObservation(null, 1) });
            r.OnPlay = i => { if (i.Kind == InputKind.KeyDown) Check(r.ObservationCount == 5, "Walked before reacquiring the target"); };
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count(i => i.Kind == InputKind.MoveRelative) == 1 && r.Inputs.Count(i => i.Kind == InputKind.KeyDown) == 1 && r.Held == 0, "Occlusion caused a large search turn");
        });
        Test("Persistent loss uses bounded recovery toward the last bearing, including inverted controls", () =>
        {
            foreach (bool inverted in new[] { false, true })
            {
                var s = Navigator(); s.Navigation.InvertTurn = inverted; var r = new Runtime(); bool failed = false;
                r.Observations = new(new[] { new TargetObservation(Hit(300), 0), new TargetObservation(Hit(300), 0) });
                try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult(); } catch (TimeoutException) { failed = true; }
                Check(failed && r.Inputs.Count == 6 && r.Inputs.All(i => i.Kind == InputKind.MoveRelative && (inverted ? i.X > 0 : i.X < 0)), "Recovery reversed direction or walked blindly");
                Check(r.Inputs.Skip(1).Take(3).All(i => Math.Abs(i.X) <= 25) && r.Held == 0, "Recovery rotation was not bounded");
            }
        });
        Test("Arrival during occlusion recovery stops without replaying reward inputs", () =>
        {
            var s = Navigator(); s.Navigation.ArrivalInputs.Add(new() { Kind = InputKind.KeyDown, Code = 0x46 });
            var r = new Runtime { Observations = new(new[] { new TargetObservation(Hit(300), 0), new TargetObservation(Hit(300), 0), new TargetObservation(null, 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 1 && r.Inputs[0].Kind == InputKind.MoveRelative && r.ObservationCount == 5 && r.Held == 0, "Approach-only test played reward actions or moved past arrival");
        });
        Test("Already at the tree requires no movement", () =>
        {
            var r = new Runtime { Observations = new(new[] { new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            var s = Navigator(); s.Navigation.RecenterBeforeSearch = true; s.Navigation.ZoomOutSteps = 3;
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1, 0).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 0, "Moved after arrival");
        });
        Test("Pitch alignment corrects high and low targets before walking, with inverted controls", () =>
        {
            foreach (bool invert in new[] { false, true }) foreach (int y in new[] { 0, 400 })
            {
                var s = Navigator(); s.Navigation.InvertPitch = invert;
                var off = new TargetHit(new Rectangle(440, y, 80, 80), .98, .3, 960, 540);
                var r = new Runtime { Observations = new(new[] { new TargetObservation(off, 0), new TargetObservation(off, 0), new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
                r.OnPlay = i => { if (i.Kind == InputKind.KeyDown) Check(r.ObservationCount == 4, "Walked before pitch alignment"); };
                TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
                var turn = r.Inputs[0]; int expectedSign = (y == 0 ? -1 : 1) * (invert ? -1 : 1);
                Check(turn.Kind == InputKind.MoveRelative && turn.X == 0 && Math.Sign(turn.Y) == expectedSign && Math.Abs(turn.Y) <= 60 && r.Held == 0, "Pitch sign, limit or release is wrong");
            }
        });
        Test("Pitch search samples above and below without cumulative drift or blind walking", () =>
        {
            var s = Navigator(); s.Navigation.MaxSearchTurns = 48; var r = new Runtime(); bool failed = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult(); } catch (TimeoutException) { failed = true; }
            double height = 0, lowest = 0, highest = 0;
            foreach (var input in r.Inputs) { height += input.Y; lowest = Math.Min(lowest, height); highest = Math.Max(highest, height); }
            Check(failed && r.Inputs.Count == 48 && r.Inputs.All(i => i.Kind == InputKind.MoveRelative) && lowest == -160 && highest == 160 && height == 0 && r.Held == 0, "Pitch search drifted or walked");
        });
        Test("Disabling auto pitch keeps both search and tracking horizontal", () =>
        {
            var s = Navigator(); s.Navigation.AutoPitch = false;
            var high = new TargetHit(new Rectangle(700, 0, 80, 80), .98, .3, 960, 540);
            var r = new Runtime { Observations = new(new[] { new TargetObservation(null, 0), new TargetObservation(high, 0), new TargetObservation(high, 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 2 && r.Inputs.All(i => i.Kind == InputKind.MoveRelative && i.Y == 0), "Disabled pitch still changed height");
        });
        Test("Unresponsive pitch stops at the correction limit without walking", () =>
        {
            var s = Navigator(); var high = new TargetHit(new Rectangle(440, 0, 80, 80), .98, .3, 960, 540);
            var r = new Runtime { Observations = new(Enumerable.Repeat(new TargetObservation(high, 0), 40)) }; bool failed = false;
            try { TargetNavigator.Approach(r, s, () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult(); } catch (TimeoutException) { failed = true; }
            Check(failed && r.Inputs.Count == 16 && r.Inputs.All(i => i.Kind == InputKind.MoveRelative && i.Y < 0) && r.Held == 0, "Unresponsive pitch exceeded its bound");
        });
        Test("Pitch correction settles inside the comfort band before resuming movement", () =>
        {
            var s = Navigator();
            TargetHit At(int y) => new(new Rectangle(440, y, 80, 80), .98, .3, 960, 540);
            var r = new Runtime { Observations = new(new[] { new TargetObservation(At(70), 0), new TargetObservation(At(70), 0), new TargetObservation(At(90), 0), new TargetObservation(At(90), 0), new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0), new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            r.OnPlay = i => { if(i.Kind == InputKind.KeyDown)Check(r.ObservationCount == 6,"Resumed walking at the outer pitch boundary"); };
            TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count(i=>i.Kind==InputKind.MoveRelative && i.Y<0)==2 && r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==1 && r.Held==0,"Pitch comfort band did not settle");
        });
        Test("Successful pitch alignment resets the failure counter across a long approach", () =>
        {
            var s=Navigator(); var high=new TargetHit(new Rectangle(440,0,80,80),.98,.3,960,540);
            var r=new Runtime();
            for(int i=0;i<20;i++)
            {
                r.Observations.Enqueue(new(high,0));r.Observations.Enqueue(new(high,0));
                r.Observations.Enqueue(new(Hit(440),0));r.Observations.Enqueue(new(Hit(440),0));
            }
            r.Observations.Enqueue(new(null,1));r.Observations.Enqueue(new(null,1));
            TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count(i=>i.Kind==InputKind.MoveRelative)==20 && r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==20 && r.Held==0,"Successful corrections were counted as repeated failures");
        });
        Test("Larger tree coverage increases pitch correction at the same vertical error", () =>
        {
            var n = new TargetNavigation();
            var small = new TargetHit(new Rectangle(440,98,80,40),.98,.3,960,540);
            var large = new TargetHit(new Rectangle(330,78,300,80),.98,.3,960,540);
            int far = TargetNavigator.PitchCorrection(n,small,0), near = TargetNavigator.PitchCorrection(n,large,0);
            Check(far<0 && near<far && Math.Abs(far)<=60 && Math.Abs(near)<=96,"Large target did not receive a stronger bounded correction");
        });
        Test("Repeated pitch correction speeds up but respects both size limits", () =>
        {
            var n=new TargetNavigation();
            foreach(var h in new[]{new TargetHit(new Rectangle(440,98,80,40),.98,.3,960,540),new TargetHit(new Rectangle(330,78,300,80),.98,.3,960,540)})
            {
                int first=TargetNavigator.PitchCorrection(n,h,0), repeated=TargetNavigator.PitchCorrection(n,h,4), later=TargetNavigator.PitchCorrection(n,h,100);
                int limit=h.Bounds.Width==80?60:96;
                Check(Math.Abs(repeated)>Math.Abs(first) && repeated==later && Math.Abs(later)<=limit,"Adaptive correction did not accelerate or exceeded its bound");
            }
        });
        Test("Disabling adaptive pitch preserves the configured gain and original cap", () =>
        {
            var n=new TargetNavigation{AdaptivePitch=false};var h=new TargetHit(new Rectangle(330,78,300,80),.98,.3,960,540);
            int expected=(int)Math.Clamp(Math.Round((h.CenterY-.38)*n.PitchGain),-60,60);
            Check(TargetNavigator.PitchCorrection(n,h,0)==expected && TargetNavigator.PitchCorrection(n,h,30)==expected,"Adaptive pitch opt-out was ignored");
        });
        Test("Measured height progress can continue beyond sixteen corrections without premature walking", () =>
        {
            var s=Navigator();var r=new Runtime();
            for(int i=0;i<22;i++)
            {
                var h=new TargetHit(new Rectangle(440,14+4*i,80,80),.98,.3,960,540);
                r.Observations.Enqueue(new(h,0));r.Observations.Enqueue(new(h,0));
            }
            r.Observations.Enqueue(new(Hit(440),0));r.Observations.Enqueue(new(Hit(440),0));
            r.Observations.Enqueue(new(null,1));r.Observations.Enqueue(new(null,1));
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown)Check(r.ObservationCount==46,"Walked before height correction finished");};
            TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count(i=>i.Kind==InputKind.MoveRelative)==22 && r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==1 && r.Held==0,"Progress was treated as repeated failure");
        });
        Test("Adaptive pitch reverses after overshoot and only walks once the height settles", () =>
        {
            var s=Navigator();var high=new TargetHit(new Rectangle(330,14,300,80),.98,.3,960,540);var low=new TargetHit(new Rectangle(330,320,300,80),.98,.3,960,540);
            var r=new Runtime{Observations=new(new[]{new TargetObservation(high,0),new TargetObservation(high,0),new TargetObservation(low,0),new TargetObservation(low,0),new TargetObservation(Hit(440),0),new TargetObservation(Hit(440),0),new TargetObservation(null,1),new TargetObservation(null,1)})};
            TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count==3 && r.Inputs[0].Y<0 && r.Inputs[1].Y>0 && r.Inputs.Take(2).All(i=>Math.Abs(i.Y)<=96) && r.Inputs[2].Kind==InputKind.KeyDown && r.Held==0,"Overshoot did not reverse or caused premature walking");
        });
        Runtime ApproachedTree()
        {
            var r = new Runtime();
            for(int i = 0; i < 8; i++) r.Observations.Enqueue(new(Hit(440), 0));
            return r;
        }
        var upperTrunk = new TargetHit(new Rectangle(400,60,160,80), .98, .3, 960,540);
        Test("Established approach interleaves a bounded forward step after four pitch corrections", () =>
        {
            var r = ApproachedTree();
            for(int i=0;i<10;i++) r.Observations.Enqueue(new(upperTrunk,0));
            r.Observations.Enqueue(new(null,1));r.Observations.Enqueue(new(null,1));
            TargetNavigator.Approach(r,Navigator(),()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==5 && r.Inputs.Count(i=>i.Kind==InputKind.MoveRelative)==4 && r.Held==0,"Pitch blocked established forward progress");
        });
        Test("Forward assistance is limited to three steps and never marks arrival on its own", () =>
        {
            var r=ApproachedTree();for(int i=0;i<180;i++)r.Observations.Enqueue(new(upperTrunk,0));bool failed=false;
            try{TargetNavigator.Approach(r,Navigator(),()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();}catch(TimeoutException){failed=true;}
            Check(failed && r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==7 && r.Held==0,"Assist exceeded three steps or claimed arrival");
        });
        Test("Arrival before an assisted step prevents extra forward input", () =>
        {
            var r=ApproachedTree();for(int i=0;i<8;i++)r.Observations.Enqueue(new(upperTrunk,0));
            r.Observations.Enqueue(new(upperTrunk,1));r.Observations.Enqueue(new(upperTrunk,1));
            TargetNavigator.Approach(r,Navigator(),()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==4 && r.Held==0,"Assist ignored arrival");
        });
        Test("Missing or horizontally misaligned target cannot use forward assistance", () =>
        {
            foreach(bool missing in new[]{true,false})
            {
                var r=ApproachedTree();for(int i=0;i<8;i++)r.Observations.Enqueue(new(upperTrunk,0));
                var side=new TargetHit(new Rectangle(650,60,160,80),.98,.3,960,540);
                for(int i=0;i<60;i++)r.Observations.Enqueue(new(missing?null:side,0));bool failed=false;
                try{TargetNavigator.Approach(r,Navigator(),()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();}catch(TimeoutException){failed=true;}
                Check(failed && r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==4 && r.Held==0,"Assist walked without a current aligned target");
            }
        });
        Test("Cancellation during assisted movement releases W", () =>
        {
            var r=ApproachedTree();for(int i=0;i<10;i++)r.Observations.Enqueue(new(upperTrunk,0));
            using var cancel=new CancellationTokenSource();int moves=0;bool stopped=false;
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown && ++moves==5)cancel.Cancel();};
            try{TargetNavigator.Approach(r,Navigator(),()=>false,_=>{},cancel.Token,1).GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}
            Check(stopped && moves==5 && r.Held==0,"Assisted movement left W held");
        });
        Test("Arrival takes priority over a target requiring vertical correction", () =>
        {
            var high = new TargetHit(new Rectangle(440, 0, 80, 80), .98, .3, 960, 540);
            var r = new Runtime { Observations = new(new[] { new TargetObservation(high, 1), new TargetObservation(high, 1) }) };
            TargetNavigator.Approach(r, Navigator(), () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 0 && r.ObservationCount == 2, "Pitch adjustment took priority over arrival");
        });
        Test("Cancelling a forward pulse releases W", () =>
        {
            using var cancel = new CancellationTokenSource(); var r = new Runtime(); r.Observations = new(new[] { new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0) });
            r.OnPlay = i => { if (i.Kind == InputKind.KeyDown) cancel.Cancel(); }; bool stopped = false;
            try { TargetNavigator.Approach(r, Navigator(), () => false, _ => { }, cancel.Token, 1).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && r.Held == 0 && r.Releases > 0, "Forward key remained held");
        });
        Test("Loss of target after a sighting cannot start forward movement", () =>
        {
            var r = new Runtime { Observations = new(new[] { new TargetObservation(Hit(440), 0) }) }; bool failed = false;
            try { TargetNavigator.Approach(r, Navigator(), () => false, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult(); } catch (TimeoutException) { failed = true; }
            Check(failed && r.Inputs.All(i => i.Kind != InputKind.KeyDown), "Walked after target loss");
        });
        Test("Arrival action recording is separate from the old movement recording", () =>
        {
            var s = Navigator(); var old = s.Inputs; long oldDuration = s.DurationMs;
            s.StoreRecording(new() { new() { Kind = InputKind.KeyDown, Code = 0x46 }, new() { AtMs = 1, Kind = InputKind.KeyUp, Code = 0x46 } }, 1);
            Check(s.Inputs == old && s.DurationMs == oldDuration && s.PlaybackInputs[0].Code == 0x46 && Workflow.Validate(new[] { new PlanItem(s) }).Count == 0, "Old path was overwritten or remains active");
            s.Navigation.Enabled = false; Check(s.PlaybackInputs == old, "Old mode did not restore");
        });
        Test("Engine navigates before playing only the separate arrival actions", () =>
        {
            var s = Navigator(); s.Before = null;
            s.StoreRecording(new() { new() { Kind = InputKind.KeyDown, Code = 0x46 }, new() { AtMs = 1, Kind = InputKind.KeyUp, Code = 0x46 } }, 1);
            var r = new Runtime { Observations = new(new[] { new TargetObservation(null, 1), new TargetObservation(null, 1) }) };
            r.MatchScore = c => c == s.After && !r.Inputs.Any(i => i.Kind == InputKind.KeyUp) ? 0 : 1;
            r.OnPlay = _ => Check(r.ObservationCount >= 2, "Actions began before arrival");
            var engine = new RoutineEngine(r, _ => { }); engine.Run(new[] { new PlanItem(s) }, CancellationToken.None, 1, 1).GetAwaiter().GetResult();
            Check(r.Inputs.Count == 2 && r.Inputs.All(i => i.Code == 0x46) && engine.CompletedRewards == 1, "Replayed the old movement or counted navigation as a reward");
        });
        Test("Completion during approach releases W immediately and stops navigation", () =>
        {
            var r = new Runtime { Observations = new(new[] { new TargetObservation(Hit(440), 0), new TargetObservation(Hit(440), 0) }) };
            bool complete = false; r.OnPlay = i => { if (i.Kind == InputKind.KeyDown) complete = true; };
            bool result = TargetNavigator.Approach(r, Navigator(), () => complete, _ => { }, CancellationToken.None, 1).GetAwaiter().GetResult();
            Check(result && r.Held == 0 && r.Inputs.Count == 1, "Completion did not release movement");
        });
        Test("Old profiles migrate selected completion options while preserving recordings", () =>
        {
            var p = Profile.Create(); p.Version = 1; var s = p.Get("collect"); s.Navigation.Enabled = false;
            s.Inputs = Segment("collect").Inputs; string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-migration-" + Guid.NewGuid() + ".json");
            try
            {
                ProfileStore.Save(p, path); var loaded = ProfileStore.Load(path);
                Check(loaded.Version == 3 && loaded.Get("enter_domain").StopOnCompletion && loaded.Get("battle").StopOnCompletion && loaded.Get("collect").Navigation.Enabled && loaded.Get("collect").Inputs.Count == 2 && loaded.Get("collect").Navigation.ArrivalInputs.Count == 0, "Migration dropped input or reused the old movement path");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Fast approach uses longer pulses only for confident distant aligned targets",()=>
        {
            var n=Navigator().Navigation;n.FastApproach=true;n.ForwardPulseMs=200;
            Check(TargetNavigator.ForwardDuration(n,Hit(440),false)==600,"Distant aligned target did not accelerate");
            foreach(var hit in new[]{Hit(490),Hit(440) with{Score=.80},Hit(440) with{Bounds=new Rectangle(350,70,260,280)},Hit(440) with{Bounds=new Rectangle(440,20,80,100)}})
                Check(TargetNavigator.ForwardDuration(n,hit,false)==200,"Uncertain, nearby or misaligned target accelerated");
            Check(TargetNavigator.ForwardDuration(n,Hit(440),true)==200,"Assist ignored its short-step limit");
        });
        Test("Arrival seen during a fast pulse releases W and confirms with a fresh frame",()=>
        {
            var s=Navigator();s.Navigation.FastApproach=true;var r=new Runtime();
            r.Observations=new(new[]{new TargetObservation(Hit(440),0),new TargetObservation(Hit(440),0),new TargetObservation(null,1)});
            var clock=System.Diagnostics.Stopwatch.StartNew();long moved=0;
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown)moved=clock.ElapsedMilliseconds;};
            r.MatchScore=_=>r.Held>0?1:0;
            TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count==1 && r.Held==0 && r.ObservationCount==3 && clock.ElapsedMilliseconds-moved<400,"Arrival did not interrupt cruise");
        });
        Test("Fast approach requires a fresh matching target before each pulse",()=>
        {
            var s=Navigator();s.Navigation.FastApproach=true;s.Navigation.CruisePulseMs=200;var r=new Runtime{MatchScore=_=>0};
            r.Observations=new(new[]{new TargetObservation(Hit(440),0),new TargetObservation(Hit(440),0),new TargetObservation(Hit(440),0),new TargetObservation(null,1),new TargetObservation(null,1)});
            var counts=new List<int>();r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown)counts.Add(r.ObservationCount);};
            TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
            Check(counts.SequenceEqual(new[]{2,3}) && r.Held==0,"Reused stale frame or took redundant second frame after every pulse");
        });
        Test("Target loss or abrupt size change resets fast approach tracking",()=>
        {
            foreach(bool lost in new[]{true,false})
            {
                var s=Navigator();s.Navigation.FastApproach=true;s.Navigation.CruisePulseMs=200;var r=new Runtime{MatchScore=_=>0};
                var changed=Hit(440) with{Bounds=new Rectangle(460,165,40,50)};
                r.Observations=new(new[]{new TargetObservation(Hit(440),0),new TargetObservation(Hit(440),0),new TargetObservation(lost?null:changed,0),new TargetObservation(null,1),new TargetObservation(null,1)});
                TargetNavigator.Approach(r,s,()=>false,_=>{},CancellationToken.None,1).GetAwaiter().GetResult();
                Check(r.Inputs.Count(i=>i.Kind==InputKind.KeyDown)==1 && r.Held==0,"Walked on lost or jumping target");
            }
        });
        Test("Cancellation or focus loss during a fast pulse releases W",()=>
        {
            foreach(bool focus in new[]{true,false})
            {
                var s=Navigator();s.Navigation.FastApproach=true;using var cancel=new CancellationTokenSource();var r=new Runtime{MatchScore=_=>0};
                r.Observations=new(new[]{new TargetObservation(Hit(440),0),new TargetObservation(Hit(440),0)});
                r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown){if(focus)r.LoseFocusAfterInput=true;else cancel.Cancel();}};
                bool stopped=false;try{TargetNavigator.Approach(r,s,()=>false,_=>{},cancel.Token,1).GetAwaiter().GetResult();}catch(Exception ex)when(ex is OperationCanceledException or InvalidOperationException){stopped=true;}
                Check(stopped && r.Held==0,"Fast pulse ignored stop");
            }
        });
        return results;
    }
    private sealed class Runtime : IRuntime
    {
        public List<RecordedInput> Inputs { get; } = new();
        public Func<Checkpoint, double> MatchScore { get; set; } = _ => 1;
        public Queue<TargetObservation> Observations { get; set; } = new();
        public Action<RecordedInput>? OnPlay { get; set; }
        public bool LoseFocusAfterInput { get; set; }
        public int ObservationCount { get; private set; }
        public int Held { get; private set; }
        public int Releases { get; private set; }
        public bool Exists => true;
        public void Ensure(Segment s) { if (LoseFocusAfterInput && Inputs.Count > 0) throw new InvalidOperationException("Focus lost"); }
        public double Match(Checkpoint c) => MatchScore(c);
        public void Play(RecordedInput input) { Inputs.Add(input); if (input.Kind is InputKind.KeyDown or InputKind.MouseDown) Held++; if (input.Kind is InputKind.KeyUp or InputKind.MouseUp) Held--; OnPlay?.Invoke(input); }
        public void Release() { Held = 0; Releases++; }
        public TargetObservation ObserveTarget(TargetNavigation n, CancellationToken token) { ObservationCount++; return Observations.Count > 0 ? Observations.Dequeue() : new(null, 0); }
    }
}

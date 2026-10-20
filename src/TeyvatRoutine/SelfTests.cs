using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace TeyvatRoutine;
public static class SelfTests
{
    public static string Run()
    {
        var passed = new List<string>();
        void Test(string name, Action body) { body(); passed.Add("PASS  " + name); }
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        using var source = new Bitmap(128, 96, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(source))
        { g.Clear(Color.DarkBlue); g.FillRectangle(Brushes.Orange, 16, 16, 32, 32); g.FillRectangle(Brushes.White, 20, 20, 10, 18); g.FillRectangle(Brushes.Black, 35, 28, 9, 11); }
        var checkpoint = Vision.Crop(source, new Rectangle(16, 16, 32, 32));
        Profile Ready()
        {
            var p = Profile.Create(); p.ResolutionWidth = 960; p.ResolutionHeight = 540;
            foreach (var s in p.Segments)
            {
                s.Navigation.Enabled = false; s.StopOnCompletion = false;
                s.Width = 960; s.Height = 540; s.DurationMs = 1; s.TimeoutSeconds = 1;
                s.Before = checkpoint; s.After = checkpoint;
                s.Inputs = new() { new() { AtMs = 0, Kind = InputKind.KeyDown, Code = 0x57 }, new() { AtMs = 1, Kind = InputKind.KeyUp, Code = 0x57 } };
            }
            return p;
        }
        Test("F1-F24 and Escape record, save, reload and replay with matched releases", () =>
        {
            var keys = Enumerable.Range(0x70, 24).Append(0x1B).ToArray();
            var b = new RecordingBuffer(); long time = 0;
            foreach (int key in keys)
            {
                b.Key(++time, key, true, false, false, false);
                b.Key(++time, key, true, false, false, false); // auto-repeat must not duplicate held keys
                b.Key(++time, key, false, true, false, false);
            }
            b.Pause(++time, "Save");
            Check(b.Events.Count == keys.Length * 2 && b.Events.Where(e => e.Kind == InputKind.KeyDown).Select(e => e.Code).SequenceEqual(keys), "Function/Escape inputs missing or duplicated");
            var p = Ready(); p.Get("battle").StoreRecording(b.Events, b.Duration);
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-keys-" + Guid.NewGuid() + ".json");
            try
            {
                ProfileStore.Save(p, path); var s = ProfileStore.Load(path).Get("battle");
                Check(Workflow.Validate(new[] { new PlanItem(s) }).Count == 0, "Function/Escape recording rejected");
                var runtime = new FakeRuntime(); new RoutineEngine(runtime, _ => { }).Run(new[] { new PlanItem(s) }, CancellationToken.None, 1, 1).GetAwaiter().GetResult();
                Check(runtime.Inputs.Select(e => (e.Kind, e.Code)).SequenceEqual(b.Events.Select(e => (e.Kind, e.Code))) && runtime.Held == 0, "Function/Escape replay lost keys");
                Check(s.Inputs[0].Display.Contains("F1") && s.Inputs[^1].Display.Contains("Escape"), "Key names not shown");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Navigation, punctuation, numpad operators and extended keys are recorded", () =>
        {
            int[] keys = { 0x21, 0x22, 0x23, 0x24, 0x2D, 0x2E, 0x6A, 0x6B, 0x6D, 0x6E, 0x6F, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE, 0x90, 0x91, 0x14, 0x03 };
            var b = new RecordingBuffer(); long time = 0;
            foreach (int key in keys) { b.Key(++time, key, true, false, true, false); b.Key(++time, key, false, true, true, false); }
            Check(!b.Paused && b.Events.Count == keys.Length * 2 && b.Events.All(e => e.Extended), "Additional keyboard inputs lost");
            var s = Ready().Get("battle"); s.StoreRecording(b.Events, time);
            Check(Workflow.Validate(new[] { new PlanItem(s) }).Count == 0, "Extended/OEM input rejected");
            Check(!InputRules.AllowedKey(0) && !InputRules.AllowedKey(0xFF) && !InputRules.AllowedKey(-1), "Invalid virtual key accepted");
        });
        Test("Only recorder chords are excluded; plain and modified function keys remain", () =>
        {
            foreach (int command in new[] { InputRules.ModeKey, InputRules.StopKey })
            {
                var b = new RecordingBuffer();
                b.Key(1, 0xA2, true, false, false, false); b.Key(2, 0xA4, true, false, false, false);
                b.Key(3, command, true, false, true, true);
                b.Key(4, 0xA4, false, true, false, false); b.Key(5, 0xA2, false, true, false, false);
                b.Key(6, command, true, false, true, false); b.Key(7, command, false, true, true, false);
                Check(!b.Paused && b.Events.All(e => e.Code != command) && b.Events.Count == 4, "Recorder chord leaked or modifiers unbalanced");
                b.Key(8, command, true, false, true, false); b.Key(9, command, false, true, true, false);
                Check(b.Events.Count == 6, "Plain Home/End blocked after shortcut release");
            }
            var modified = new RecordingBuffer(); modified.Key(1, 0xA2, true, false, false, false); modified.Key(2, 0xA4, true, false, false, false);
            foreach (int key in Enumerable.Range(0x70, 12)) { modified.Key(3, key, true, false, false, true); modified.Key(4, key, false, true, false, true); }
            Check(modified.Events.Count(e => e.Kind == InputKind.KeyDown && e.Code >= 0x70 && e.Code <= 0x7B) == 12, "Modified function keys reserved");
        });
        Test("Native F1-F12 and Escape replay packets contain scan codes without sending input", () =>
        {
            foreach (int key in Enumerable.Range(0x70, 12).Append(0x1B))
            {
                var down = Native.Key(key, true).Data.Key; var up = Native.Key(key, false).Data.Key;
                Check(down.Scan != 0 && down.Flags == 8 && up.Flags == 10 && down.Scan == up.Scan, "Invalid function/Escape scan-code packet");
            }
            var extended = Native.Key(0x23, false, true).Data.Key;
            Check((extended.Flags & 3) == 3, "Extended release flag lost");
        });
        Test("Focus pause preserves input, balances held controls, and freezes duration", () =>
        {
            var b = new RecordingBuffer();
            b.Key(10, 0x57, true, false, true, false); b.Button(15, 0, true); b.RelativeMove(20, 3, -4);
            b.Pause(100, "Focus loss"); int count = b.Events.Count;
            b.Key(200, 0x45, true, false, false, false); b.Button(300, 1, true);
            b.RelativeMove(400, 8, 9); b.Add(500, InputKind.Wheel, 120); b.Pause(9000, "Save click");
            Check(b.Paused && b.Duration == 100 && b.Events.Count == count && b.PauseReason == "Focus loss", "Paused recording changed");
            Check(b.Events.Count(e => e.Kind == InputKind.KeyUp && e.Extended) == 1 && b.Events.Count(e => e.Kind == InputKind.MouseUp) == 1, "Unbalanced held input");
            var s = Ready().Get("battle"); s.StoreRecording(b.Events, b.Duration);
            Check(Workflow.Validate(new[] { new PlanItem(s) }).Count == 0, "Paused recording does not validate");
        });
        Test("Alt Tab stops recording before the window-switch shortcut is saved", () =>
        {
            var b = new RecordingBuffer();
            b.Key(10, 0xA4, true, false, false, false);
            b.Key(20, 0x09, true, false, false, true);
            b.Key(30, 0x09, false, true, false, true); b.Key(40, 0xA4, false, true, false, false);
            Check(b.Paused && b.Duration == 20 && b.Events.All(e => e.Code != 0x09), "Alt Tab leaked into playback");
            Check(b.Events.Count == 2 && b.Events[1].Kind == InputKind.KeyUp, "Held Alt was not balanced");
            var plain = new RecordingBuffer(); plain.Key(5, 0x09, true, false, false, false); plain.Key(6, 0x09, false, true, false, false);
            Check(!plain.Paused && plain.Events.Count == 2, "Ordinary game Tab was removed");
            var other = new RecordingBuffer(); other.Key(5, 0x1B, true, false, false, true);
            Check(other.Paused && other.Events.Count == 0, "Alt Esc leaked into playback");
        });
        Test("Manual save after switching windows persists input and existing screens", () =>
        {
            var p = Ready(); var s = p.Get("login"); var b = new RecordingBuffer();
            b.Add(10, InputKind.MoveAbsolute, x: .5, y: .5); b.Button(15, 0, true); b.Button(25, 0, false);
            b.Pause(1000, "App selected"); b.Button(6000, 0, true); b.Pause(8000, "Save clicked");
            s.StoreRecording(b.Events, b.Duration);
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-manual-" + Guid.NewGuid() + ".json");
            try
            {
                ProfileStore.Save(p, path); var loaded = ProfileStore.Load(path).Get("login");
                Check(loaded.Inputs.Count == 3 && loaded.DurationMs == 1000 && loaded.Before != null && loaded.After != null, "Manual save lost data or added the save-button click");
                Check(Workflow.Validate(new[] { new PlanItem(loaded) }).Count == 0, "Saved recording invalid");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Direct stop also releases held controls exactly once", () =>
        {
            var b = new RecordingBuffer(); b.Key(1, 0x57, true, false, false, false); b.Button(2, 1, true);
            b.Pause(50, "Save"); b.Pause(100, "Repeated save");
            Check(b.Events.Count == 4 && b.Duration == 50, "Duplicate releases or changed duration");
            var s = Ready().Get("battle"); s.StoreRecording(b.Events, b.Duration);
            Check(Workflow.Validate(new[] { new PlanItem(s) }).Count == 0, "Directly stopped recording invalid");
        });
        Test("Reset clears only the selected registration and preserves settings", () =>
        {
            var p = Ready(); var s = p.Get("battle"); s.Status = "완료";
            var others = System.Text.Json.JsonSerializer.Serialize(p.Segments.Where(a => a != s), ProfileStore.Options);
            s.ResetRegistration();
            Check(s.Inputs.Count == 0 && s.DurationMs == 0 && s.Width == 0 && s.Height == 0 && s.Before == null && s.After == null && !s.Ready && s.Status == "녹화 필요", "Registration not cleared");
            Check(s.RelativeInitially && s.TimeoutSeconds == 1 && p.ResolutionWidth == 960 && p.IncludeDomain, "Settings changed");
            Check(others == System.Text.Json.JsonSerializer.Serialize(p.Segments.Where(a => a != s), ProfileStore.Options), "Other segments changed");
            Check(Workflow.Validate(new[] { new PlanItem(s) }).Count > 0, "Reset segment can still run");
        });
        Test("Recording after screen registration preserves both checkpoints", () =>
        {
            var s = Ready().Get("login"); var before = s.Before; var after = s.After;
            var inputs = s.Inputs.ToList(); s.Inputs.Clear(); s.DurationMs = 0;
            Check(!s.Ready, "Screens alone count as ready");
            s.StoreRecording(inputs, 1);
            Check(s.Ready && s.Before == before && s.After == after && Workflow.Validate(new[] { new PlanItem(s) }).Count == 0, "Preregistered screens lost");
            var original = s.Inputs;
            bool rejected = false; try { s.StoreRecording(new(), 1); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && s.Inputs == original && s.After == after, "Empty recording overwrote registration");
            s.StoreRecording(inputs, 2);
            Check(s.DurationMs == 2 && s.Before == before && s.After == after, "Rerecording lost screens");
        });
        Test("03 goes directly to 05; exactly 3 battles, 3 rewards and 2 retries before 09", () =>
        {
            var plan = Workflow.Build(Profile.Create());
            string expected = "login,warp_domain,enter_domain,battle,collect,retry,battle,collect,retry,battle,collect,warp_train,craft,daily";
            Check(string.Join(',', plan.Select(s => s.Segment.Id)) == expected, "Wrong workflow");
            Check(plan.Where(s => s.Segment.Id == "collect").Select(s => s.Round).SequenceEqual(new[] { 1, 2, 3 }), "Wrong rounds");
        });
        Test("Legacy 04 data survives reload but cannot run; optional 08 remains after the last reward", () =>
        {
            var p = Ready(); p.SelectedSteps = new() { "enter_domain", "start", "battle", "collect", "retry", "exit", "warp_train" };
            string original = System.Text.Json.JsonSerializer.Serialize(p.Get("enter_domain"), ProfileStore.Options);
            string legacy = System.Text.Json.JsonSerializer.Serialize(p.Get("start"), ProfileStore.Options);
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-removed04-" + Guid.NewGuid() + ".json");
            try
            {
                ProfileStore.Save(p, path); var loaded = ProfileStore.Load(path);
                Check(loaded.RoutineSegments.Count() == 11 && loaded.RoutineSegments.All(s => s.Id != "start") && !loaded.IsStepSelected("start"), "04 remains visible or selectable");
                Check(System.Text.Json.JsonSerializer.Serialize(loaded.Get("enter_domain"), ProfileStore.Options) == original && System.Text.Json.JsonSerializer.Serialize(loaded.Get("start"), ProfileStore.Options) == legacy, "Existing registration was changed");
                loaded.Get("start").ResetRegistration();
                var plan = Workflow.Build(loaded);
                Check(string.Join(',', plan.Select(i => i.Segment.Id)) == "enter_domain,battle,collect,retry,battle,collect,retry,battle,collect,exit,warp_train", "Optional exit or loop order changed");
                Check(Workflow.Validate(plan).Count == 0, "Unused 04 registration blocks execution");
            }
            finally { foreach (var file in new[] { path, path + ".bak" }) if (File.Exists(file)) File.Delete(file); }
        });
        Test("Optional groups and quit preserve dependencies", () =>
        {
            var p = Profile.Create(); p.IncludeLogin = p.IncludeDomain = p.IncludeCraft = false; p.QuitAfter = true;
            Check(string.Join(',', Workflow.Build(p).Select(s => s.Segment.Id)) == "daily,quit", "Group skip");
        });
        Test("Individual step selection preserves order and skips unselected registration", () =>
        {
            var p = Ready(); p.SelectedSteps = new() { "daily", "enter_domain", "craft" };
            p.Get("login").ResetRegistration(); p.Get("warp_domain").ResetRegistration();
            var plan = Workflow.Build(p);
            Check(string.Join(',', plan.Select(i => i.Segment.Id)) == "enter_domain,craft,daily", "Step order or filtering failed");
            Check(Workflow.Validate(plan).Count == 0, "Unselected registration blocked the plan");
        });
        Test("Legacy partial repeat selection enables one complete group; all-off means no work", () =>
        {
            var p = Ready(); p.SelectedSteps = new() { "collect", "retry" };
            var plan = Workflow.Build(p);
            Check(string.Join(',', plan.Select(i => i.Segment.Id)) == "battle,collect,retry,battle,collect,retry,battle,collect", "Repeat group not unified");
            Check(plan.Where(i => i.Segment.Id == "collect").Select(i => i.Round).SequenceEqual(new[] { 1, 2, 3 }), "Round numbers changed");
            p.SelectedSteps.Clear(); Check(Workflow.Build(p).Count == 0, "All-off reverted to legacy defaults");
            p.SelectedSteps.Add("quit"); Check(Workflow.Build(p).Single().Segment.Id == "quit", "Quit cannot be selected independently");
        });
        Test("Step selections persist and old group settings retain their meaning", () =>
        {
            var p = Ready(); p.IncludeDomain = false; p.IncludeCraft = false;
            Check(string.Join(',', Workflow.Build(p).Select(i => i.Segment.Id)) == "login,daily", "Old group settings changed");
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-selection-" + Guid.NewGuid() + ".json");
            try
            {
                p.SelectedSteps = new() { "enter_domain", "warp_train" }; ProfileStore.Save(p, path);
                Check(string.Join(',', Workflow.Build(ProfileStore.Load(path)).Select(i => i.Segment.Id)) == "enter_domain,warp_train", "Selection not persisted");
                p.SelectedSteps.Clear(); ProfileStore.Save(p, path); Check(Workflow.Build(ProfileStore.Load(path)).Count == 0, "Empty selection not persisted");
                p.SelectedSteps.Add("unknown"); ProfileStore.Save(p, path);
                bool rejected = false; try { ProfileStore.Load(path); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Unknown selection ID accepted");
            }
            finally { foreach (var file in new[] { path, path + ".bak" }) if (File.Exists(file)) File.Delete(file); }
        });
        Test("One through ten rounds have exactly N battles and rewards and N minus one retries", () =>
        {
            for (int n = 1; n <= 10; n++)
            {
                var p = Ready(); p.DomainRuns = n; p.SelectedSteps = new() { "battle", "warp_train" };
                var plan = Workflow.Build(p);
                Check(plan.Count(i => i.Segment.Id == "battle") == n && plan.Count(i => i.Segment.Id == "collect") == n && plan.Count(i => i.Segment.Id == "retry") == n - 1, "Wrong repeat count");
                Check(plan[^2].Segment.Id == "collect" && plan[^2].Round == n && plan[^1].Segment.Id == "warp_train", "Final reward did not go straight to next task");
                if(n == 1) p.Get("retry").ResetRegistration();
                Check(Workflow.Validate(plan).Count == 0, "Unused retry prevents a one-round run");
            }
        });
        Test("Left task rows are numbered 1 to 9 and group edits preserve original segment objects", () =>
        {
            var p = Ready(); p.SelectedSteps = new() { "collect", "daily" }; var tasks = p.CreateTasks();
            try
            {
                Check(tasks.Count == 9 && tasks.Select(t => t.Name[..2]).SequenceEqual(Enumerable.Range(1, 9).Select(n => n.ToString("00"))), "Task numbering is not contiguous");
                var group = tasks[3];
                Check(group.IsSelected && group.Parts.Select(s => s.Id).SequenceEqual(new[] { "battle", "collect", "retry" }), "Missing grouped editors");
                Check(group.Parts.All(s => ReferenceEquals(s, p.Get(s.Id))), "Grouped editor copied recording objects");
                group.IsSelected = false;
                Check(string.Join(',', Workflow.Build(p).Select(i => i.Segment.Id)) == "daily", "Group disable left repeat actions selected");
                group.IsSelected = true; group.Rounds = 1; p.Get("retry").ResetRegistration();
                Check(group.Ready && Workflow.Build(p).Count == 3 && p.IsStepSelected("daily"), "Task change affected another row or one-round readiness");
                group.Rounds = 2; Check(!group.Ready, "Unregistered retry was not required for two rounds");
            }
            finally { foreach(var task in tasks) task.Dispose(); }
        });
        Test("Repeat count persists, absent old property defaults to three, invalid values are rejected", () =>
        {
            var p = Ready(); p.DomainRuns = 7;
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-rounds-" + Guid.NewGuid() + ".json");
            try
            {
                ProfileStore.Save(p, path); Check(ProfileStore.Load(path).DomainRuns == 7, "Round count not persisted");
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"DomainRuns\": 7,", ""));
                Check(ProfileStore.Load(path).DomainRuns == 3, "Old profile default changed");
                foreach(int n in new[] { 0, -1, 11, int.MaxValue })
                {
                    p.DomainRuns = n; ProfileStore.Save(p, path); bool loadFailed = false, planFailed = false;
                    try { ProfileStore.Load(path); } catch (InvalidDataException) { loadFailed = true; }
                    try { Workflow.Build(p); } catch (InvalidDataException) { planFailed = true; }
                    Check(loadFailed && planFailed, "Invalid round count accepted");
                }
            }
            finally { foreach(var f in new[] { path, path + ".bak" }) if(File.Exists(f)) File.Delete(f); }
        });
        Test("Uncalibrated default cannot run", () => Check(Workflow.Validate(Workflow.Build(Profile.Create())).Count > 10, "Default accepted"));
        Test("Fully registered profile validates", () => Check(Workflow.Validate(Workflow.Build(Ready())).Count == 0, "Valid profile rejected"));
        Test("Different client resolutions rejected", () => { var p = Ready(); p.Get("battle").Width = 1920; Check(Workflow.Validate(Workflow.Build(p)).Any(e => e.Contains("해상도")), "Resolution accepted"); });
        Test("Unbalanced keyboard and unsafe input rejected", () =>
        {
            var p = Ready(); p.Get("battle").Inputs.RemoveAt(1); Check(Workflow.Validate(Workflow.Build(p)).Count > 0, "Held key accepted");
            p = Ready(); p.Get("battle").Inputs[0].Code = 0x5B; Check(Workflow.Validate(Workflow.Build(p)).Count > 0, "Windows key accepted");
            p = Ready(); p.Get("battle").Inputs[0].Kind = (InputKind)999; Check(Workflow.Validate(Workflow.Build(p)).Count > 0, "Unknown action accepted");
        });
        Test("Non-finite and outside coordinates rejected", () =>
        {
            var p = Ready(); p.Get("battle").Inputs.Insert(0, new() { Kind = InputKind.MoveAbsolute, X = 1.1 });
            Check(Workflow.Validate(Workflow.Build(p)).Count > 0, "Out of window accepted");
            p.Get("battle").Inputs[0].X = double.NaN; Check(Workflow.Validate(Workflow.Build(p)).Count > 0, "NaN accepted");
        });
        Test("Image match: exact and translated patch", () =>
        {
            Check(Vision.Score(source, checkpoint) > .999, "Exact match failed");
            using var moved = new Bitmap(128, 96, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(moved)) { g.Clear(Color.DarkBlue); g.DrawImageUnscaled(source, 4, 2); }
            Check(Vision.Score(moved, checkpoint) > .999, "Translation failed");
        });
        Test("Image match rejects unrelated and blank captures", () =>
        {
            using var blank = new Bitmap(128, 96, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            Check(Vision.Score(blank, checkpoint) < checkpoint.Threshold, "False positive");
            bool rejected = false; try { Vision.Crop(blank, new Rectangle(0, 0, 32, 32)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Blank calibration accepted");
        });
        Test("Engine executes full queue and only verified rewards count", () =>
        {
            var runtime = new FakeRuntime(); var engine = new RoutineEngine(runtime, _ => { });
            engine.Run(Workflow.Build(Ready()), CancellationToken.None, 1, 1).GetAwaiter().GetResult();
            Check(engine.CompletedRewards == 3 && runtime.Inputs.Count == 28, "Wrong result");
            Check(runtime.Held == 0 && runtime.Releases >= 14, "Held inputs after completion");
        });
        Test("Precondition mismatch sends zero input", () =>
        {
            var runtime = new FakeRuntime { Score = 0 }; var engine = new RoutineEngine(runtime, _ => { });
            bool threw = false; try { engine.Run(new[] { new PlanItem(Ready().Get("collect")) }, CancellationToken.None, 1, 2).GetAwaiter().GetResult(); } catch (TimeoutException) { threw = true; }
            Check(threw && runtime.Inputs.Count == 0 && engine.CompletedRewards == 0, "Precondition failed open");
        });
        Test("Postcondition mismatch does not count reward or advance", () =>
        {
            var runtime = new FakeRuntime { FailAfterInput = true }; var engine = new RoutineEngine(runtime, _ => { });
            bool threw = false; var p = Ready();
            try { engine.Run(new[] { new PlanItem(p.Get("collect")), new PlanItem(p.Get("craft")) }, CancellationToken.None, 1, 2).GetAwaiter().GetResult(); } catch (TimeoutException) { threw = true; }
            Check(threw && runtime.Inputs.Count == 2 && engine.CompletedRewards == 0 && runtime.Held == 0, "Completion failed open");
        });
        Test("Cancellation releases held inputs promptly", () =>
        {
            var s = Ready().Get("battle"); s.DurationMs = 5000; s.Inputs[1].AtMs = 5000;
            using var cancel = new CancellationTokenSource(80); var runtime = new FakeRuntime(); var engine = new RoutineEngine(runtime, _ => { });
            bool stopped = false; var watch = System.Diagnostics.Stopwatch.StartNew();
            try { engine.Run(new[] { new PlanItem(s) }, cancel.Token, 1, 1).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && runtime.Inputs.Count == 1 && runtime.Held == 0 && watch.ElapsedMilliseconds < 1000, "Cancellation failed");
        });
        Test("Focus loss aborts remaining input and releases keys", () =>
        {
            var runtime = new FakeRuntime { LoseFocusAfterInput = true }; var engine = new RoutineEngine(runtime, _ => { }); bool stopped = false;
            try { engine.Run(new[] { new PlanItem(Ready().Get("battle")) }, CancellationToken.None, 1, 1).GetAwaiter().GetResult(); } catch (InvalidOperationException) { stopped = true; }
            Check(stopped && runtime.Inputs.Count == 1 && runtime.Held == 0, "Focus loss failed");
        });
        Test("Profile round trip and atomic backup", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "TeyvatRoutine-test-" + Guid.NewGuid() + ".json");
            try { ProfileStore.Save(Ready(), path); ProfileStore.Save(Ready(), path); var loaded = ProfileStore.Load(path); Check(Workflow.Validate(Workflow.Build(loaded)).Count == 0 && File.Exists(path + ".bak"), "Round trip failed"); }
            finally { foreach (var file in new[] { path, path + ".bak", path + ".tmp" }) if (File.Exists(file)) File.Delete(file); }
        });
        Test("Resolution profiles have separate validated paths", () =>
        {
            Check(ProfileStore.ResolutionPath(3840, 2160) != ProfileStore.ResolutionPath(1920, 1080), "Resolution collision");
            Check(ProfileStore.ResolutionPath(1536, 864).EndsWith("1536x864.json"), "Custom window size");
            bool rejected = false; try { ProfileStore.ResolutionPath(-1, 1080); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid resolution accepted");
        });
        Test("Flat colored patch is rejected", () =>
        {
            using var flat = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(flat)) g.Clear(Color.Orange);
            bool rejected = false; try { Vision.Crop(flat, new Rectangle(0, 0, 32, 32)); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Flat color accepted");
        });
        Test("x64 native ABI sizes match Win32", () =>
        {
            Check(Marshal.SizeOf<Native.Input>() == 40, "INPUT size"); Check(Marshal.SizeOf<Native.MouseInput>() == 32, "MOUSEINPUT size");
            Check(Marshal.SizeOf<Native.RawHeader>() == 24, "RAWINPUTHEADER size"); Check(Marshal.SizeOf<Native.RawMouse>() == 24, "RAWMOUSE size");
        });
        passed.AddRange(VisionFlowTests.Run());
        passed.AddRange(RecognitionTests.Run());
        passed.AddRange(CombatTests.Run());
        passed.AddRange(PartySlotTests.Run());
        passed.AddRange(SkillTests.Run());
        passed.AddRange(PartySwitchTests.Run());
        passed.AddRange(ResumeTests.Run());
        passed.AddRange(ScreenFlowTests.Run());
        return string.Join(Environment.NewLine, passed) + $"{Environment.NewLine}{passed.Count} / {passed.Count} tests passed. No real game input was sent.{Environment.NewLine}";
    }
    private sealed class FakeRuntime : IRuntime
    {
        public double Score { get; set; } = 1;
        public bool FailAfterInput { get; set; }
        public bool LoseFocusAfterInput { get; set; }
        public List<RecordedInput> Inputs { get; } = new();
        public int Held { get; private set; }
        public int Releases { get; private set; }
        public bool Exists => true;
        public void Ensure(Segment s) { if (LoseFocusAfterInput && Inputs.Count > 0) throw new InvalidOperationException("Focus lost"); }
        public double Match(Checkpoint c) => FailAfterInput && Inputs.Count > 0 ? 0 : Score;
        public void Play(RecordedInput i) { Inputs.Add(i); if (i.Kind == InputKind.KeyDown) Held++; if (i.Kind == InputKind.KeyUp) Held--; }
        public void Release() { Releases++; Held = 0; }
    }
}


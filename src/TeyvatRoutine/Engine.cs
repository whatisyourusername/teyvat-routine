using System.Diagnostics;
using System.IO;

namespace TeyvatRoutine;
public interface IRuntime
{
    long PausedMilliseconds => 0;
    double[] Scores(IReadOnlyList<Checkpoint> checkpoints) => checkpoints.Select(Match).ToArray();
    bool AnyMatch(IReadOnlyList<Checkpoint> checkpoints) => checkpoints.Any(c => Match(c) >= c.Threshold);
    void Ensure(Segment segment);
    bool Exists { get; }
    double Match(Checkpoint checkpoint);
    void Play(RecordedInput input);
    void Release();
    SkillObservation ObserveSkill(SkillTrigger settings) => throw new NotSupportedException("스킬 화면 인식을 지원하지 않습니다.");
    PartySlotObservation ObservePartySlot(PartySlotSettings settings) => throw new NotSupportedException("파티 번호 인식을 지원하지 않습니다.");
    TargetHit? LocateTarget(TargetNavigation settings, CancellationToken token) => throw new NotSupportedException("시점 기준 탐색을 지원하지 않습니다.");
    TargetObservation ObserveTarget(TargetNavigation navigation, CancellationToken token) => throw new NotSupportedException("대상 탐색을 지원하지 않는 실행 환경입니다.");
}
public sealed class WindowsRuntime : IRuntime
{
    private readonly GameWindow _game;
    private readonly Dictionary<int, bool> _keys = new();
    private readonly HashSet<int> _buttons = new();
    private int _width, _height;
    public WindowsRuntime(GameWindow game) => _game = game;
    public bool Exists => Native.Exists(_game);
    public void Ensure(Segment s) { _width = s.Width; _height = s.Height; Native.Ensure(_game, _width, _height); }
    public double Match(Checkpoint c) { using var screen = Native.Capture(_game); return Vision.Score(screen, c); }
    public double[] Scores(IReadOnlyList<Checkpoint> checkpoints)
    { using var screen = Native.Capture(_game); return checkpoints.Select(c => Vision.Score(screen, c)).ToArray(); }
    public bool AnyMatch(IReadOnlyList<Checkpoint> checkpoints)
    { using var screen = Native.Capture(_game); return checkpoints.Any(c => Vision.Score(screen, c) >= c.Threshold); }
    public SkillObservation ObserveSkill(SkillTrigger settings)
    { using var screen=Native.Capture(_game);return new(Vision.Score(screen,settings.ReadyIcon!),CooldownDigits.Present(screen,settings.CooldownArea!)); }
    public PartySlotObservation ObservePartySlot(PartySlotSettings settings)
    { using var screen=Native.Capture(_game);return PartySlotReader.Read(screen,settings); }
    public TargetHit? LocateTarget(TargetNavigation settings, CancellationToken token)
    { using var screen = Native.Capture(_game); return TargetMatcher.Find(screen, settings, token); }
    public TargetObservation ObserveTarget(TargetNavigation navigation, CancellationToken token)
    {
        using var screen = Native.Capture(_game);
        double arrival = Vision.Score(screen, navigation.Arrival!);
        if (arrival >= navigation.Arrival!.Threshold) return new(null, arrival, "도착 표시 확인");
        var analysis = TargetMatcher.Analyze(screen, navigation, token);
        return new(analysis.Accepted ? analysis.Best : null, arrival, analysis.Reason);
    }
    public void Play(RecordedInput e)
    {
        Native.Ensure(_game, _width, _height);
        switch (e.Kind)
        {
            case InputKind.KeyDown: _keys[e.Code] = e.Extended; Native.Send(Native.Key(e.Code, true, e.Extended)); break;
            case InputKind.KeyUp: Native.Send(Native.Key(e.Code, false, e.Extended)); _keys.Remove(e.Code); break;
            case InputKind.MoveAbsolute: Native.Send(Native.Absolute(Native.Bounds(_game.Handle), e.X, e.Y)); break;
            case InputKind.MoveRelative: Native.Send(Native.Mouse(1, (int)e.X, (int)e.Y)); break;
            case InputKind.MouseDown: _buttons.Add(e.Code); Native.Send(Native.Mouse(Native.ButtonFlag(e.Code, true))); break;
            case InputKind.MouseUp: Native.Send(Native.Mouse(Native.ButtonFlag(e.Code, false))); _buttons.Remove(e.Code); break;
            case InputKind.Wheel: Native.Send(Native.Mouse(0x800, data: e.Code)); break;
        }
    }
    public void Release()
    {
        foreach (var (k, e) in _keys) { try { Native.Send(Native.Key(k, false, e)); } catch { } }
        foreach (int b in _buttons) { try { Native.Send(Native.Mouse(Native.ButtonFlag(b, false))); } catch { } }
        _keys.Clear(); _buttons.Clear();
    }
}
public sealed record RunUpdate(string StageId, string Message, int CompletedRewards, int FinishedStages, int TotalStages);
public sealed class RoutineEngine
{
    private IRuntime _runtime;
    private readonly ScreenFlowSettings _screenFlow;
    private readonly Action<RunUpdate> _report;
    public int CompletedRewards { get; private set; }
    public RoutineEngine(IRuntime runtime, Action<RunUpdate> report, ScreenFlowSettings? screenFlow = null)
    { _runtime = runtime; _report = report; _screenFlow = screenFlow ?? new(); }
    public async Task Run(IReadOnlyList<PlanItem> plan, CancellationToken token, int stableSamples = 2, int pollMs = 150)
    {
        var errors = Workflow.Validate(plan, _screenFlow);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        if (plan.Count == 0) throw new InvalidDataException("실행할 작업을 선택하세요.");
        CompletedRewards = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45)); token = deadline.Token;
        int completed = 0;
        var originalRuntime = _runtime;
        Segment? current = null;
        _runtime = new LoadingRuntime(originalRuntime, _screenFlow, token, message => { if (current != null) Report(current, message); });
        try
        {
            foreach (var item in plan)
            {
                var s = item.Segment;
                current = s;
                string round = item.Round > 0 ? $" ({item.Round}/{plan.Max(p => p.Round)})" : "";
                token.ThrowIfCancellationRequested(); _runtime.Ensure(s);
                if (s.Id == "login" && _screenFlow.LoginEnabled)
                {
                    await LoginFlow.Run(_runtime, s, _screenFlow, message => Report(s, message), token);
                    completed++; Report(s, "필드 확인 · 로그인 완료"); continue;
                }
                var completion = new CompletionProbe(_runtime, s, token, Math.Max(1, stableSamples), Math.Max(1, pollMs));
                bool detected = false;
                if(item.CombatInProgress)Report(s,"전투 도중 재개 · 시작 장치 입력 생략"+round);
                else if (!s.Navigation.Enabled)
                {
                    if (s.Alignment.Enabled) detected = await ViewAligner.Align(_runtime, s, () => completion.Check(), message => Report(s, message + round), token);
                    if (!detected) { Report(s, "시작 화면 확인" + round); detected = await WaitFor(s, s.Before!, token, stableSamples, pollMs, completion); }
                }
                else
                {
                    Report(s, "보상 나무 탐색" + round);
                    detected = await TargetNavigator.Approach(_runtime, s, () => completion.Check(), message => Report(s, message + round), token);
                }
                if (!detected) Report(s, (s.Navigation.Enabled ? "나무 도착 후 보상 조작 재생" : "입력 재생") + round);
                var watch = RuntimeClock.StartNew(_runtime);
                try
                {
                    if (!detected && s.Combat.Enabled)
                        detected = await ContinueCombat(s, completion, message => Report(s, message + round), token,item.CombatInProgress);
                    foreach (var input in s.PlaybackInputs)
                    {
                        if (detected) break;
                        // Quit may close the target before the last recorded neutral delay or release.
                        if (s.Id == "quit" && !_runtime.Exists) break;
                        detected = await WaitUntil(input.AtMs, watch, s, token, completion);
                        if (detected) break;
                        _runtime.Play(input);
                    }
                    if (s.Id != "quit" && !detected) detected = await WaitUntil(s.PlaybackDuration, watch, s, token, completion);
                }
                finally { _runtime.Release(); }
                if (s.Id == "quit")
                {
                    var quit = Stopwatch.StartNew();
                    while (_runtime.Exists)
                    {
                        token.ThrowIfCancellationRequested();
                        if (quit.ElapsedMilliseconds > 30000) throw new TimeoutException("게임 종료를 확인하지 못했습니다. 강제 종료하지 않습니다.");
                        await Task.Delay(100, token);
                    }
                }
                else if (!detected)
                {
                    Report(s, "완료 화면 확인" + round);
                    await WaitFor(s, s.After!, token, stableSamples, pollMs);
                }
                else Report(s, "완료 화면 감지 · 남은 입력 생략" + round);
                if (s.Id == "collect") CompletedRewards++;
                completed++; Report(s, "완료" + round);
            }
        }
        finally { _runtime.Release(); _runtime = originalRuntime; }
        void Report(Segment segment, string message) => _report(new(segment.Id, message, CompletedRewards, completed, plan.Count));
    }
    private async Task<bool> ContinueCombat(Segment s, CompletionProbe completion, Action<string> report, CancellationToken token,bool resume=false)
    {
        var c = s.Combat;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(c.TimeoutSeconds));
        var watch = RuntimeClock.StartNew(_runtime);
        long cycleStart = resume?0:c.StartMs;
        try
        {
            for (int cycle = 0; cycle < c.MaxCycles; cycle++)
            {
                if (cycle > 0)
                {
                    if (await WaitUntil(cycleStart, watch, s, budget.Token, completion)) return true;
                    // A short normal attack lets the game turn toward a nearby enemy.
                    // Middle-click then requests the game's own camera recentering.
                    if (c.RecenterBetweenCycles)
                    {
                        report("일반 공격 후 가운데 버튼으로 시점 정렬");
                        foreach (var input in new[] {
                            new RecordedInput { Kind = InputKind.MouseDown, Code = 0 },
                            new RecordedInput { AtMs = 70, Kind = InputKind.MouseUp, Code = 0 },
                            new RecordedInput { AtMs = 300, Kind = InputKind.MouseDown, Code = 2 },
                            new RecordedInput { AtMs = 370, Kind = InputKind.MouseUp, Code = 2 } })
                        {
                            if (await WaitUntil(cycleStart + input.AtMs, watch, s, budget.Token, completion)) return true;
                            _runtime.Play(input);
                        }
                        if (await WaitUntil(cycleStart + 650, watch, s, budget.Token, completion)) return true;
                    }
                    cycleStart = Math.Max(cycleStart, watch.ElapsedMilliseconds);
                }
                report($"클리어 확인하며 전투 사이클 {cycle + 1}/{c.MaxCycles}");
                bool opening=cycle==0 && !resume;
                var inputs = opening ? c.Opening(s) : c.ExecutionCycle(s);
                long shift=0;
                bool firstAlreadyReady=false;
                foreach (var input in inputs)
                {
                    long relative=opening?input.AtMs-c.StartMs:input.AtMs;
                    bool chain=c.Rotation==CombatRotation.SupportOrder2341;
                    if(chain && relative>=c.SecondSkillAt && relative<=c.SecondSkillAt+100 && input.Code==0x45)continue;
                    long at = (opening ? input.AtMs : cycleStart + input.AtMs)+shift;
                    if (await WaitUntil(at, watch, s, budget.Token, completion)) return true;
                    if(resume && cycle==0 && chain && c.SkillTrigger.Enabled && relative==300 && input.Kind==InputKind.KeyDown && input.Code==0x45)
                    {
                        var state=await SecondSkillChain.PrepareResume(_runtime,c.SkillTrigger,ms=>WaitUntil(watch.ElapsedMilliseconds+ms,watch,s,budget.Token,completion),report);
                        if(state.Completed)return true;firstAlreadyReady=state.AlreadyReady;
                        shift=watch.ElapsedMilliseconds-cycleStart-relative;
                    }
                    if(!(firstAlreadyReady && input.Code==0x45 && relative is 300 or 400))_runtime.Play(input);
                    if(chain && relative==400 && input.Kind==InputKind.KeyUp && input.Code==0x45)
                    {
                        if(await SecondSkillChain.Run(_runtime,c,ms=>WaitUntil(watch.ElapsedMilliseconds+ms,watch,s,budget.Token,completion),report))return true;
                        if(await PartySwitchGate.Run(_runtime,c,ms=>WaitUntil(watch.ElapsedMilliseconds+ms,watch,s,budget.Token,completion),report))return true;
                        shift=watch.ElapsedMilliseconds-cycleStart-c.ThirdSlotAt;
                    }
                    // Capture or input latency must not consume the swap-to-skill interval.
                    if(chain && relative==c.ThirdSlotAt && input.Kind==InputKind.KeyDown && input.Code==0x33)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds-cycleStart-c.ThirdSlotAt);
                    if(chain && relative==c.ThirdSlotAt+100 && input.Kind==InputKind.KeyUp && input.Code==0x33)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds+250-cycleStart-(c.ThirdSlotAt+350));
                    if(chain && relative==c.ThirdSlotAt+350 && input.Kind==InputKind.KeyDown && input.Code==0x45)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds-cycleStart-relative);
                    if(chain && relative==c.ThirdSlotAt+350+c.ThirdSkillHoldMs && input.Kind==InputKind.KeyUp && input.Code==0x45)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds+500-cycleStart-c.PlungeAt);
                    if(chain && relative==c.PlungeAt && input.Kind==InputKind.MouseDown)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds-cycleStart-c.PlungeAt);
                    if(chain && relative==c.PlungeAt+80 && input.Kind==InputKind.MouseUp)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds+c.LandingWaitMs-cycleStart-c.FourthSlotAt);
                    if(chain && relative==c.FourthSlotAt && input.Kind==InputKind.KeyDown && input.Code==0x34)
                        shift=Math.Max(shift,watch.ElapsedMilliseconds-cycleStart-c.FourthSlotAt);
                    if(chain && relative==c.FourthSlotAt+100 && input.Kind==InputKind.KeyUp && input.Code==0x34)
                    {
                        long skillEarliest=watch.ElapsedMilliseconds+200;
                        if(c.FourthPartySwitch.Enabled && await FourthPartySwitch.Confirm(_runtime,c.FourthPartySwitch,
                            ms=>WaitUntil(watch.ElapsedMilliseconds+ms,watch,s,budget.Token,completion),report))return true;
                        shift=Math.Max(shift,Math.Max(skillEarliest,watch.ElapsedMilliseconds)-cycleStart-(c.FourthSlotAt+300));
                    }
                }
                _runtime.Release();
                cycleStart += c.PeriodMs+Math.Max(0,shift);
                report("클리어 미확인 · 다음 사이클 간격 대기");
                if (await WaitUntil(cycleStart, watch, s, budget.Token, completion)) return true;
            }
            throw new TimeoutException($"클리어를 확인하지 못해 전투 {c.MaxCycles}사이클에서 중단했습니다. 보상 나무 탐색은 시작하지 않습니다.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
        { throw new TimeoutException($"클리어 미확인 · 전투 제한 {c.TimeoutSeconds}초에서 중단했습니다. 보상 나무 탐색은 시작하지 않습니다."); }
        finally { _runtime.Release(); }
    }
    private async Task<bool> WaitUntil(long atMs, RuntimeClock watch, Segment s, CancellationToken token, CompletionProbe completion)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); _runtime.Ensure(s);
            if (completion.Check()) return true;
            long remaining = atMs - watch.ElapsedMilliseconds;
            if (remaining <= 0 && !completion.Candidate) return false;
            await Task.Delay((int)Math.Max(1, Math.Min(20, remaining > 0 ? remaining : 20)), token);
        }
    }
    private async Task<bool> WaitFor(Segment s, Checkpoint c, CancellationToken token, int needed, int pollMs, CompletionProbe? completion = null)
    {
        int stable = 0; var clock = RuntimeClock.StartNew(_runtime); double score = 0; long pause = _runtime.PausedMilliseconds;
        while (clock.ElapsedMilliseconds <= s.TimeoutSeconds * 1000L)
        {
            token.ThrowIfCancellationRequested(); _runtime.Ensure(s);
            if (_runtime.PausedMilliseconds != pause) { stable = 0; pause = _runtime.PausedMilliseconds; }
            if (completion?.Check() == true) return true;
            if (completion?.Candidate == true) { await Task.Delay(pollMs, token); continue; }
            score = _runtime.Match(c); token.ThrowIfCancellationRequested();
            if (_runtime.PausedMilliseconds != pause) { stable = 0; pause = _runtime.PausedMilliseconds; }
            stable = score >= c.Threshold ? stable + 1 : 0;
            if (stable >= needed) return false;
            await Task.Delay(pollMs, token);
        }
        throw new TimeoutException($"{s.Name}: 화면 확인 시간 초과 (일치도 {score:P0}, 기준 {c.Threshold:P0}). 다음 작업을 실행하지 않았습니다.");
    }
    private sealed class CompletionProbe
    {
        private readonly IRuntime _runtime;
        private readonly Segment _segment;
        private readonly CancellationToken _token;
        private readonly int _needed, _interval;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _next;
        private int _stable;
        private bool _done;
        private long _pause;
        public bool Candidate => _stable > 0 && !_done;
        public CompletionProbe(IRuntime runtime, Segment segment, CancellationToken token, int needed, int interval)
        { _runtime = runtime; _segment = segment; _token = token; _needed = needed; _interval = interval; }
        public bool Check()
        {
            if (_done) return true;
            if (_segment.Id == "quit" || !(_segment.StopOnCompletion || _segment.Navigation.Enabled || _segment.Combat.Enabled) || _segment.After == null || _clock.ElapsedMilliseconds < _next) return false;
            _token.ThrowIfCancellationRequested(); _runtime.Ensure(_segment);
            double score = _runtime.Match(_segment.After); _token.ThrowIfCancellationRequested();
            if (_runtime.PausedMilliseconds != _pause) { _stable = 0; _pause = _runtime.PausedMilliseconds; }
            _next = _clock.ElapsedMilliseconds + _interval;
            _stable = score >= _segment.After.Threshold ? _stable + 1 : 0;
            if (_stable < _needed) return false;
            _done = true; _runtime.Release(); return true;
        }
    }
}

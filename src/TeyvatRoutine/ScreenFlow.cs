using System.Diagnostics;
using System.IO;

namespace TeyvatRoutine;

public sealed class ScreenFlowSettings
{
    public bool LoginEnabled { get; set; }
    public bool LoadingEnabled { get; set; }
    public bool WelkinEnabled { get; set; }
    public Checkpoint? Start { get; set; }
    public Checkpoint? Field { get; set; }
    public Checkpoint? DomainField { get; set; }
    public Checkpoint? DomainNotice { get; set; }
    public Checkpoint? Door { get; set; }
    public Checkpoint? Welkin { get; set; }
    public List<Checkpoint> LoadingScreens { get; set; } = new();
    public int UpdateTimeoutSeconds { get; set; } = 900;
    public int LoadingTimeoutSeconds { get; set; } = 180;
    public int FieldQuietMs { get; set; } = 5000;
    public List<string> Validate(int width, int height, bool includesLogin)
    {
        var errors = new List<string>();
        if (UpdateTimeoutSeconds is < 30 or > 1800 || LoadingTimeoutSeconds is < 5 or > 600 || FieldQuietMs is < 1000 or > 15000)
            errors.Add("로그인·로딩 대기 시간이 허용 범위를 벗어났습니다.");
        if (LoadingScreens == null || LoadingScreens.Count > 12 || LoadingScreens.Any(c => c == null))
        { errors.Add("로딩 화면 목록이 유효하지 않습니다."); return errors; }
        if (LoadingEnabled && LoadingScreens.Count == 0) errors.Add("공통 로딩 화면을 먼저 등록하세요.");
        if (LoadingEnabled && Field == null) errors.Add("로딩 종료를 확인할 필드 UI를 등록하세요.");
        if (includesLogin && LoginEnabled && Door == null) errors.Add("로그인 문 화면을 먼저 등록하세요.");
        if (includesLogin && LoginEnabled && WelkinEnabled && Welkin == null) errors.Add("공월 화면을 등록하거나 공월 자동 닫기를 해제하세요.");
        foreach (var c in LoadingScreens.Concat(new[] { Start, Field, DomainField, DomainNotice, Door, Welkin }.Where(c => c != null).Cast<Checkpoint>()))
        {
            if (c.X < 0 || c.Y < 0 || c.Width < 16 || c.Height < 16 || c.X + (long)c.Width > width || c.Y + (long)c.Height > height
                || !double.IsFinite(c.Threshold) || c.Threshold is < .65 or > 1 || c.SearchRadius is < 0 or > 100 || c.WhiteBrightness is < 0 or > 255)
                errors.Add("로그인·로딩 인식 영역이 현재 해상도와 맞지 않습니다.");
            try { Vision.Validate(c); } catch { errors.Add("로그인·로딩 이미지가 유효하지 않습니다."); }
        }
        return errors.Distinct().ToList();
    }
}

// Replay time excludes loading so overdue input events never rush out together.
public sealed class RuntimeClock
{
    private readonly IRuntime _runtime;
    private readonly Stopwatch _watch = new();
    private long _paused;
    private RuntimeClock(IRuntime runtime) { _runtime = runtime; Restart(); }
    public static RuntimeClock StartNew(IRuntime runtime) => new(runtime);
    public long ElapsedMilliseconds => Math.Max(0, _watch.ElapsedMilliseconds - (_runtime.PausedMilliseconds - _paused));
    public void Restart() { _paused = _runtime.PausedMilliseconds; _watch.Restart(); }
}

public sealed class LoadingRuntime : IRuntime
{
    private readonly IRuntime _inner;
    private readonly ScreenFlowSettings _settings;
    private readonly CancellationToken _token;
    private readonly Action<string> _report;
    private readonly int _pollMs, _clearSamples;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, RecordedInput> _keys = new();
    private readonly HashSet<int> _buttons = new();
    private readonly HashSet<int> _releasedButtons = new();
    private Segment? _segment;
    private long _next, _paused;
    public LoadingRuntime(IRuntime inner, ScreenFlowSettings settings, CancellationToken token, Action<string> report, int pollMs = 120, int clearSamples = 3)
    { _inner = inner; _settings = settings; _token = token; _report = report; _pollMs = Math.Max(1, pollMs); _clearSamples = Math.Max(1, clearSamples); }
    public long PausedMilliseconds => _inner.PausedMilliseconds + _paused;
    public bool Exists => _inner.Exists;
    public void Ensure(Segment segment) { _segment = segment; _token.ThrowIfCancellationRequested(); _inner.Ensure(segment); Guard(); }
    private void Guard()
    {
        _token.ThrowIfCancellationRequested();
        if (!_settings.LoadingEnabled || _segment == null || _clock.ElapsedMilliseconds < _next) return;
        _next = _clock.ElapsedMilliseconds + _pollMs;
        if (!_inner.AnyMatch(_settings.LoadingScreens)) return;
        var pause = Stopwatch.StartNew();
        // Only movement can safely be held again. Never repeat a menu click or skill.
        var movement = _keys.Values.Where(k => k.Code is 0x57 or 0x41 or 0x53 or 0x44).ToArray();
        _releasedButtons.UnionWith(_buttons);
        _inner.Release(); _keys.Clear(); _buttons.Clear();
        _report("로딩 감지 · 입력 해제 후 대기");
        try
        {
            int clear = 0;
            while (clear < _clearSamples)
            {
                Task.Delay(_pollMs, _token).GetAwaiter().GetResult();
                _inner.Ensure(_segment);
                if (pause.ElapsedMilliseconds >= _settings.LoadingTimeoutSeconds * 1000L)
                    throw new TimeoutException("로딩 대기 시간 초과. 다음 입력을 실행하지 않았습니다.");
                // The domain notice hides the HUD. Resume the recording so its dismiss click can run.
                var ready = new[] { _settings.Field, _settings.DomainField, _settings.DomainNotice, _settings.Start, _settings.Door, _settings.WelkinEnabled ? _settings.Welkin : null }
                    .Where(c => c != null).Cast<Checkpoint>().ToArray();
                clear = !_inner.AnyMatch(_settings.LoadingScreens) && _inner.AnyMatch(ready) ? clear + 1 : 0;
            }
            _token.ThrowIfCancellationRequested();
            foreach (var key in movement) { _inner.Play(key); _keys[key.Code] = key; }
            _report("로딩 종료 확인 · 남은 입력 이어서 실행");
        }
        catch { _inner.Release(); _keys.Clear(); _buttons.Clear(); throw; }
        finally { _paused += pause.ElapsedMilliseconds; _next = _clock.ElapsedMilliseconds + _pollMs; }
    }
    public double Match(Checkpoint c) { Guard(); return _inner.Match(c); }
    public bool AnyMatch(IReadOnlyList<Checkpoint> checkpoints) { Guard(); return _inner.AnyMatch(checkpoints); }
    public double[] Scores(IReadOnlyList<Checkpoint> checkpoints) { Guard(); return _inner.Scores(checkpoints); }
    public SkillObservation ObserveSkill(SkillTrigger settings) { Guard(); return _inner.ObserveSkill(settings); }
    public PartySlotObservation ObservePartySlot(PartySlotSettings settings) { Guard(); return _inner.ObservePartySlot(settings); }
    public TargetHit? LocateTarget(TargetNavigation settings, CancellationToken token) { Guard(); return _inner.LocateTarget(settings, token); }
    public TargetObservation ObserveTarget(TargetNavigation settings, CancellationToken token) { Guard(); return _inner.ObserveTarget(settings, token); }
    public void Play(RecordedInput input)
    {
        Guard();
        if (input.Kind == InputKind.MouseUp && _releasedButtons.Remove(input.Code)) return;
        if (input.Kind == InputKind.MouseDown) { _releasedButtons.Remove(input.Code); _buttons.Add(input.Code); }
        if (input.Kind == InputKind.MouseUp) _buttons.Remove(input.Code);
        if (input.Kind == InputKind.KeyDown) _keys[input.Code] = input;
        if (input.Kind == InputKind.KeyUp) _keys.Remove(input.Code);
        _inner.Play(input);
    }
    public void Release() { _inner.Release(); _keys.Clear(); _buttons.Clear(); _releasedButtons.Clear(); }
}

public static class LoginFlow
{
    public static async Task Run(IRuntime runtime, Segment login, ScreenFlowSettings settings, Action<string> report,
        CancellationToken token, int pollMs = 150, int? quietMs = null, int? timeoutMs = null)
    {
        var clock = Stopwatch.StartNew();
        bool firstClicked = false, doorClicked = false, popupSeen = false;
        int startStable = 0, doorStable = 0, popupStable = 0, fieldStable = 0, popupClicks = 0;
        long fieldSince = -1, nextPopupClick = 0, doorAt = -1;
        var checks = new List<Checkpoint> { settings.Door!, settings.Start ?? login.Before!, settings.Field ?? login.After! };
        if (settings.WelkinEnabled && settings.Welkin != null) checks.Add(settings.Welkin);
        report("로그인 화면 확인 · 업데이트 중에는 문이 나올 때까지 대기");
        try
        {
            while (clock.ElapsedMilliseconds < (timeoutMs ?? settings.UpdateTimeoutSeconds * 1000L))
            {
                token.ThrowIfCancellationRequested(); runtime.Ensure(login);
                var scores = runtime.Scores(checks);
                bool door = scores[0] >= checks[0].Threshold;
                bool start = scores[1] >= checks[1].Threshold && !door;
                bool field = scores[2] >= checks[2].Threshold;
                bool popup = checks.Count > 3 && scores[3] >= checks[3].Threshold;
                startStable = start ? startStable + 1 : 0;
                doorStable = door ? doorStable + 1 : 0;
                popupStable = popup ? popupStable + 1 : 0;
                fieldStable = field && !popup ? fieldStable + 1 : 0;
                if (fieldStable >= 2)
                {
                    if (fieldSince < 0) { fieldSince = clock.ElapsedMilliseconds; report("필드 UI 확인 · 늦게 뜨는 공월 화면 대기"); }
                    if (clock.ElapsedMilliseconds - fieldSince >= (quietMs ?? settings.FieldQuietMs)) return;
                }
                else fieldSince = -1;
                if (popupStable >= 2) popupSeen = true;
                if (popupSeen && (popup || !field) && !door && clock.ElapsedMilliseconds >= nextPopupClick)
                {
                    if (++popupClicks > 6) throw new TimeoutException("공월 화면을 닫지 못했습니다. 화면을 확인하세요.");
                    report($"공월 화면 닫기 · {popupClicks}/6");
                    await Click(runtime, .5, .78, token);
                    nextPopupClick = clock.ElapsedMilliseconds + 1000;
                }
                else if (!doorClicked && doorStable >= 2)
                {
                    report("문 화면 확인 · 게임 진입");
                    await Click(runtime, .5, .5, token);
                    doorClicked = true; doorAt = clock.ElapsedMilliseconds;
                }
                else if (!firstClicked && !doorClicked && startStable >= 2 && !field && !popup)
                {
                    report("첫 진입 클릭 · 업데이트 및 문 화면 대기");
                    await Click(runtime, .5, .5, token); firstClicked = true;
                }
                if (doorAt >= 0 && clock.ElapsedMilliseconds - doorAt > settings.LoadingTimeoutSeconds * 1000L)
                    throw new TimeoutException("게임 진입 후 필드 UI를 확인하지 못했습니다. 다음 작업을 실행하지 않습니다.");
                await Task.Delay(Math.Max(1, pollMs), token);
            }
            throw new TimeoutException("업데이트·로그인 대기 시간 초과. 문 또는 필드 화면을 확인하세요.");
        }
        finally { runtime.Release(); }
    }
    private static async Task Click(IRuntime runtime, double x, double y, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        runtime.Play(new() { Kind = InputKind.MoveAbsolute, X = x, Y = y });
        runtime.Play(new() { Kind = InputKind.MouseDown, Code = 0 });
        try { await Task.Delay(80, token); runtime.Play(new() { Kind = InputKind.MouseUp, Code = 0 }); }
        finally { runtime.Release(); }
    }
}

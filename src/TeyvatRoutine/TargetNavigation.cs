using System.Diagnostics;
using System.Drawing;

namespace TeyvatRoutine;
public sealed class TargetNavigation
{
    public bool Enabled { get; set; }
    public List<Checkpoint> Templates { get; set; } = new();
    public Checkpoint? Arrival { get; set; }
    public List<RecordedInput> ArrivalInputs { get; set; } = new();
    public long ArrivalDurationMs { get; set; }
    public bool ArrivalRelativeInitially { get; set; }
    public double MatchThreshold { get; set; } = .78;
    public double AmbiguityMargin { get; set; } = .03;
    public int ForwardPulseMs { get; set; } = 120;
    public bool FastApproach { get; set; }
    public int CruisePulseMs { get; set; } = 600;
    public int TurnGain { get; set; } = 240;
    public int SearchTurn { get; set; } = 100;
    public int MaxSearchTurns { get; set; } = 48;
    public int TimeoutSeconds { get; set; } = 60;
    public bool InvertTurn { get; set; }
    public bool RecenterBeforeSearch { get; set; } = true;
    public int ZoomOutSteps { get; set; } = 3;
    public bool AutoPitch { get; set; } = true;
    public bool AdaptivePitch { get; set; } = true;
    public bool InvertPitch { get; set; }
    public int PitchGain { get; set; } = 180;
    public int SearchPitch { get; set; } = 80;
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Templates.Count is < 1 or > 5) errors.Add("나무 모습 1~5개를 등록하세요.");
        if (Arrival == null) errors.Add("나무 옆의 F 상호작용 표시를 도착 화면으로 등록하세요.");
        if (!double.IsFinite(MatchThreshold) || MatchThreshold is < .65 or > .99 || !double.IsFinite(AmbiguityMargin) || AmbiguityMargin is < .01 or > .25) errors.Add("나무 인식 기준이 유효하지 않습니다.");
        if (ForwardPulseMs is < 20 or > 400 || TurnGain is < 20 or > 1000 || SearchTurn is < 10 or > 500 || MaxSearchTurns is < 2 or > 120 || TimeoutSeconds is < 1 or > 180) errors.Add("접근 설정이 허용 범위를 벗어났습니다.");
        if (CruisePulseMs is < 200 or > 800) errors.Add("빠른 접근 전진은 200~800ms 범위로 설정하세요.");
        if (PitchGain is < 20 or > 600 || SearchPitch is < 10 or > 160) errors.Add("상하 보정량은 20~600, 상하 탐색량은 10~160 범위로 설정하세요.");
        if (ZoomOutSteps is < 0 or > 6) errors.Add("시작 시 축소량은 0~6칸으로 설정하세요. 0은 축소하지 않습니다.");
        return errors;
    }
}
public sealed record TargetHit(Rectangle Bounds, double Score, double RunnerUp, int ScreenWidth, int ScreenHeight)
{
    public double OffsetX => (Bounds.Left + Bounds.Width / 2.0) / ScreenWidth - .5;
    public double CenterY => (Bounds.Top + Bounds.Height / 2.0) / ScreenHeight;
}
public sealed record TargetObservation(TargetHit? Hit, double ArrivalScore, string Detail = "");
public static class TargetNavigator
{
    public static int ForwardDuration(TargetNavigation n,TargetHit hit,bool assist)
    {
        double coverage=Math.Max((double)hit.Bounds.Width/hit.ScreenWidth,(double)hit.Bounds.Height/hit.ScreenHeight);
        bool cruise=n.FastApproach && !assist && hit.Score>=Math.Max(.82,n.MatchThreshold+.04)
            && Math.Abs(hit.OffsetX)<=.045 && hit.CenterY is >=.24 and <=.52 && coverage<.28;
        return assist?Math.Min(200,n.ForwardPulseMs):cruise?Math.Max(n.ForwardPulseMs,n.CruisePulseMs):n.ForwardPulseMs;
    }
    public static int PitchCorrection(TargetNavigation n, TargetHit hit, int consecutiveCorrections)
    {
        double size = Math.Max((double)hit.Bounds.Width / hit.ScreenWidth, (double)hit.Bounds.Height / hit.ScreenHeight);
        // Screen coverage is a proximity cue, not a world-space distance measurement.
        double sizeGain = n.AdaptivePitch ? Math.Clamp(1 + (size - .10) * 4, 1, 2.5) : 1;
        double repeatGain = n.AdaptivePitch ? 1 + Math.Min(4, Math.Max(0, consecutiveCorrections)) * .5 : 1;
        int limit = n.AdaptivePitch && size >= .22 ? 96 : 60;
        return (int)Math.Clamp(Math.Round((hit.CenterY - .38) * n.PitchGain * sizeGain * repeatGain), -limit, limit);
    }
    // Returns true only when the stage's completion image was detected during navigation.
    public static async Task<bool> Approach(IRuntime runtime, Segment segment, Func<bool> completed, Action<string> report, CancellationToken token, int settleMs = 180, int cameraSettleMs = 600)
    {
        var n = segment.Navigation; var clock = RuntimeClock.StartNew(runtime);
        if(n.FastApproach)settleMs=Math.Min(settleMs,90);
        int arrived = 0, tracked = 0, searches = 0, missing = 0, searchDirection = 1;
        int pitchSearchLevel = 0, pitchCorrections = 0;
        int pitchBurst = 0;
        int forwardSteps = 0, pitchSinceAdvance = 0, assistedSteps = 0;
        long lastForwardAt = long.MinValue / 2;
        double bestPitchError = double.PositiveInfinity;
        bool pitchAligning = false;
        bool recenterPending = n.RecenterBeforeSearch;
        bool zoomPending = n.ZoomOutSteps > 0;
        TargetHit? previous = null, lastConfirmed = null;
        try
        {
            if (n.ZoomOutSteps is < 0 or > 6) throw new InvalidOperationException("시작 시 축소량은 0~6칸이어야 합니다.");
            while (clock.ElapsedMilliseconds < n.TimeoutSeconds * 1000L)
            {
                token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                if (completed()) return true;
                var observation = runtime.ObserveTarget(n, token);
                token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                if (clock.ElapsedMilliseconds >= n.TimeoutSeconds * 1000L) break;
                if (observation.ArrivalScore >= n.Arrival!.Threshold)
                {
                    runtime.Release(); arrived++;
                    if (arrived >= 2) { report("F 상호작용 표시 확인 · 나무 도착"); return false; }
                    await Task.Delay(settleMs, token); continue;
                }
                arrived = 0;
                if (zoomPending)
                {
                    zoomPending = false; runtime.Release();
                    report($"시야 확보 · 휠 아래로 {n.ZoomOutSteps}칸 후 화면 재확인");
                    for (int step = 0; step < n.ZoomOutSteps; step++)
                    {
                        token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                        if (completed()) return true;
                        if (clock.ElapsedMilliseconds >= n.TimeoutSeconds * 1000L) throw new TimeoutException("시야 준비 시간 초과. 입력을 멈췄습니다.");
                        runtime.Play(new() { Kind = InputKind.Wheel, Code = -120 });
                        await Task.Delay(80, token);
                    }
                    await Task.Delay(cameraSettleMs, token);
                    continue; // Recheck arrival before the optional middle click.
                }
                if (recenterPending)
                {
                    // Normalize the starting camera once. Never reset a bearing already being tracked.
                    // Arrival/completion checks above take priority over this click.
                    recenterPending = false; runtime.Release();
                    report("시점 기준 복원 · 휠 버튼 클릭 1회 후 화면 재확인");
                    try
                    {
                        token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                        runtime.Play(new() { Kind = InputKind.MouseDown, Code = 2 });
                        await Task.Delay(60, token);
                        token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                        if (completed()) return true;
                        runtime.Play(new() { Kind = InputKind.MouseUp, Code = 2 });
                    }
                    finally { runtime.Release(); }
                    await Task.Delay(cameraSettleMs, token);
                    continue; // Discard the pre-click observation and capture a fresh frame.
                }
                var hit = observation.Hit;
                if (hit == null)
                {
                    runtime.Release(); previous = null; tracked = 0;
                    // A pet or character can briefly cover the trunk near the screen center.
                    // Wait first, then make bounded corrections toward its last verified bearing.
                    // Never walk using a stale detection.
                    if (lastConfirmed != null && ++missing <= 5)
                    {
                        if (missing <= 2)
                            report($"나무 재확인 · 가림 대기 {missing}/2 · {observation.Detail}");
                        else
                        {
                            int limit = Math.Max(1, Math.Min(25, n.SearchTurn / 4));
                            int correction = (int)Math.Clamp(Math.Round(lastConfirmed.OffsetX * n.TurnGain * .5), -limit, limit);
                            if (correction != 0)
                                runtime.Play(new() { Kind = InputKind.MoveRelative, X = correction * (n.InvertTurn ? -1 : 1) });
                            report($"나무 재탐색 · 마지막 방향 {lastConfirmed.OffsetX:P1}, 보정 {correction}px ({missing - 2}/3) · {observation.Detail}");
                        }
                        await Task.Delay(settleMs, token); continue;
                    }
                    lastConfirmed = null;
                    forwardSteps = 0;
                    if (++searches > n.MaxSearchTurns) throw new TimeoutException("보상 나무를 확실하게 찾지 못했습니다. 나무 이미지를 추가하거나 시점을 확인하세요.");
                    int pitch = 0;
                    if (n.AutoPitch && (searches - 1) % 8 == 0)
                    {
                        // Sample heights around the starting view, without accumulating a pitch drift.
                        int level = new[] { -1, -2, 0, 1, 2, 0 }[((searches - 1) / 8) % 6];
                        pitch = (level - pitchSearchLevel) * n.SearchPitch; pitchSearchLevel = level;
                    }
                    report($"나무 탐색 · 제자리 회전 {searches}/{n.MaxSearchTurns}" + (pitch == 0 ? "" : $" · 시점 높이 탐색 {pitch}px") + $" · {observation.Detail}");
                    runtime.Play(new() { Kind = InputKind.MoveRelative, X = n.SearchTurn * searchDirection * (n.InvertTurn ? -1 : 1), Y = pitch * (n.InvertPitch ? -1 : 1) });
                }
                else
                {
                    tracked = previous != null && Math.Abs(previous.OffsetX - hit.OffsetX) < .09 && Math.Abs(previous.CenterY - hit.CenterY) < .12
                        && (!n.FastApproach || hit.Bounds.Width>=previous.Bounds.Width*.65 && hit.Bounds.Width<=previous.Bounds.Width*1.5 && hit.Bounds.Height>=previous.Bounds.Height*.65 && hit.Bounds.Height<=previous.Bounds.Height*1.5) ? tracked + 1 : 1;
                    previous = hit;
                    if (tracked < 2) { await Task.Delay(settleMs, token); continue; }
                    lastConfirmed = hit; missing = 0;
                    pitchSearchLevel = 0;
                    if (Math.Abs(hit.OffsetX) > .055) searchDirection = Math.Sign(hit.OffsetX);
                    double pitchError = Math.Abs(hit.CenterY - .38);
                    if (n.AutoPitch && pitchError > .15) pitchAligning = true;
                    if (!n.AutoPitch || pitchError <= .08) pitchAligning = false;
                    bool pitchNeeded = pitchAligning;
                    // Reset the failure counter on measurable progress, including slow but responsive cameras.
                    // Once correction starts, settle inside a narrower band before resuming movement.
                    if (!pitchNeeded) { pitchCorrections = pitchBurst = 0; bestPitchError = double.PositiveInfinity; }
                    else if (pitchError < bestPitchError - .005) { bestPitchError = pitchError; pitchCorrections = 0; }
                    // A tracked trunk can fill the upper view before F appears. Interleave a few
                    // bounded steps only after established forward progress, with fresh stable hits.
                    bool assist = pitchNeeded && pitchSinceAdvance >= 4 && forwardSteps >= 4 && assistedSteps < 3
                        && clock.ElapsedMilliseconds - lastForwardAt <= 12000
                        && Math.Abs(hit.OffsetX) <= .055 && hit.CenterY is >= .10 and <= .30
                        && (double)hit.Bounds.Width / hit.ScreenWidth >= .12;
                    if ((Math.Abs(hit.OffsetX) > .055 || pitchNeeded) && !assist)
                    {
                        runtime.Release();
                        double turnBoost=n.FastApproach && Math.Abs(hit.OffsetX)>.12 && hit.Score>=Math.Max(.82,n.MatchThreshold+.04)?1.4:1;
                        int turn = Math.Abs(hit.OffsetX) > .055 ? (int)Math.Clamp(Math.Round(hit.OffsetX * n.TurnGain*turnBoost), -160, 160) : 0;
                        int pitch = pitchNeeded ? PitchCorrection(n, hit, pitchBurst++) : 0;
                        if (pitchNeeded) pitchSinceAdvance++;
                        if (pitchNeeded && ++pitchCorrections > 16) throw new TimeoutException("연속 16회 보정 동안 시점 높이가 개선되지 않았습니다. 상하 반전·보정량과 나무 이미지를 확인하세요.");
                        runtime.Play(new() { Kind = InputKind.MoveRelative, X = turn * (n.InvertTurn ? -1 : 1), Y = pitch * (n.InvertPitch ? -1 : 1) });
                        tracked = 0; previous = null; report($"나무 방향으로 시점 정렬 · 일치도 {hit.Score:P0}, 가로 오차 {hit.OffsetX:P1}, 높이 {hit.CenterY:P0}, 회전 X {turn}px / Y {pitch}px");
                    }
                    else
                    {
                        if (assist) { assistedSteps++; report($"상하 보정 사이 전진 {assistedSteps}/3 · 방향 재확인 후 최대 200ms"); }
                        int duration=ForwardDuration(n,hit,assist);
                        if(!assist)report($"나무 접근 · {duration}ms 전진 후 다시 확인");
                        try
                        {
                            runtime.Play(new() { Kind = InputKind.KeyDown, Code = 0x57 });
                            var pulse = RuntimeClock.StartNew(runtime);
                            long nextArrival=0;
                            while (pulse.ElapsedMilliseconds < duration)
                            {
                                token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                                if (completed()) return true;
                                if(clock.ElapsedMilliseconds>=n.TimeoutSeconds*1000L)throw new TimeoutException("보상 나무 접근 시간 초과. 이동을 멈췄습니다.");
                                if(n.FastApproach && pulse.ElapsedMilliseconds>=nextArrival)
                                {
                                    bool candidate=runtime.Match(n.Arrival!)>=n.Arrival!.Threshold;
                                    token.ThrowIfCancellationRequested();runtime.Ensure(segment);
                                    if(candidate){runtime.Release();arrived=1;break;}
                                    nextArrival=pulse.ElapsedMilliseconds+80;
                                }
                                await Task.Delay(10, token);
                            }
                        }
                        finally { runtime.Release(); }
                        forwardSteps++; lastForwardAt = clock.ElapsedMilliseconds;
                        pitchSinceAdvance = pitchCorrections = pitchBurst = 0;
                        bestPitchError = double.PositiveInfinity;
                        // Only reuse the last hit as a comparison anchor. A fresh frame is still
                        // mandatory before every movement; missing or jumping detections reset it.
                        if(n.FastApproach && !assist){tracked=1;previous=hit;}
                        else{tracked = 0; previous = null;}
                    }
                }
                await Task.Delay(settleMs, token);
            }
            throw new TimeoutException("보상 나무 접근 시간 초과. 이동을 멈췄습니다. 나무/도착 이미지와 카메라 감도를 확인하세요.");
        }
        finally { runtime.Release(); }
    }
}

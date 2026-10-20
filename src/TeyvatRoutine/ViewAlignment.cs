using System.Diagnostics;

namespace TeyvatRoutine;
public sealed class ViewAlignment
{
    public bool Enabled { get; set; }
    public Checkpoint? Reference { get; set; }
}
public static class ViewAligner
{
    // Visual landmark alignment; it does not know world coordinates or absolute compass heading.
    public static async Task<bool> Align(IRuntime runtime, Segment segment, Func<bool> completed, Action<string> report, CancellationToken token, int settleMs = 180)
    {
        var reference = segment.Alignment.Reference ?? throw new InvalidOperationException("시점 기준 이미지를 등록하세요.");
        var matcher = new TargetNavigation { Templates = new() { reference } };
        double desiredX = (reference.X + reference.Width / 2.0) / segment.Width;
        double desiredY = (reference.Y + reference.Height / 2.0) / segment.Height;
        int stable = 0; var clock = RuntimeClock.StartNew(runtime);
        try
        {
            for (int attempt = 0; attempt < 32 && clock.ElapsedMilliseconds < 20000; attempt++)
            {
                token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                if (completed()) return true;
                var hit = runtime.LocateTarget(matcher, token);
                token.ThrowIfCancellationRequested(); runtime.Ensure(segment);
                if (clock.ElapsedMilliseconds >= 20000) break;
                if (hit == null)
                {
                    stable = 0; report("시점 기준 탐색 · 제자리 회전");
                    runtime.Play(new() { Kind = InputKind.MoveRelative, X = 80 });
                }
                else
                {
                    double dx = (hit.Bounds.Left + hit.Bounds.Width / 2.0) / hit.ScreenWidth - desiredX;
                    double dy = (hit.Bounds.Top + hit.Bounds.Height / 2.0) / hit.ScreenHeight - desiredY;
                    if (Math.Abs(dx) < .035 && Math.Abs(dy) < .045)
                    { if (++stable >= 2) { report("시점 기준 정렬 완료"); return false; } }
                    else
                    {
                        stable = 0; report($"시점 정렬 · 오차 X {dx:P0}, Y {dy:P0}");
                        runtime.Play(new() { Kind = InputKind.MoveRelative, X = Math.Clamp(Math.Round(dx * 240), -100, 100), Y = Math.Clamp(Math.Round(dy * 180), -60, 60) });
                    }
                }
                await Task.Delay(settleMs, token);
            }
            throw new TimeoutException("시점 기준 정렬 실패. 기준물이 보이는 위치에서 시작하거나 다른 기준 이미지를 등록하세요.");
        }
        finally { runtime.Release(); }
    }
}

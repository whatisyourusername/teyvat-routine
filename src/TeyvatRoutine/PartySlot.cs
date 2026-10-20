using System.Drawing;
using System.Drawing.Imaging;

namespace TeyvatRoutine;
public sealed class PartySlotSettings
{
    public bool Enabled { get; set; }
    // Register each keycap while its character is NOT selected (opaque white).
    public List<Checkpoint?> Keycaps { get; set; } = new() { null, null, null, null };
}
public sealed record PartySlotObservation(int Slot, double Confidence);
public static class PartySlotReader
{
    public static PartySlotObservation Read(Bitmap screen, PartySlotSettings settings)
    {
        if (settings.Keycaps?.Count != 4 || settings.Keycaps.Any(c => c == null)) return new(0, 0);
        int selected = 0; double confidence = 1;
        for (int i = 0; i < 4; i++)
        {
            var c = settings.Keycaps[i]!;
            using var template = Vision.Decode(c);
            var target = Luma(template); double mean = target.Average();
            double variance = target.Sum(v => (v - mean) * (v - mean));
            if (variance < target.Length * 100) return new(0, 0);
            double best = 0, gain = 0;
            for (int dy = -c.SearchRadius; dy <= c.SearchRadius; dy++)
            for (int dx = -c.SearchRadius; dx <= c.SearchRadius; dx++)
            {
                var area = new Rectangle(c.X + dx, c.Y + dy, c.Width, c.Height);
                if (!new Rectangle(0, 0, screen.Width, screen.Height).Contains(area)) continue;
                using var crop = screen.Clone(area, PixelFormat.Format24bppRgb);
                var scene = Luma(crop); double average = scene.Average(), dot = 0, sceneVariance = 0;
                for (int n = 0; n < target.Length; n++)
                { double value = scene[n] - average; dot += value * (target[n] - mean); sceneVariance += value * value; }
                if (sceneVariance < target.Length * 9) continue;
                double shape = dot / Math.Sqrt(variance * sceneVariance);
                if (shape > best) { best = shape; gain = dot / variance; }
            }
            confidence = Math.Min(confidence, best);
            // Require the numeral's shape as well as reduced contrast. A missing,
            // covered or globally dimmed HUD must not count as a successful swap.
            if (best < .72) return new(0, confidence);
            if (gain is >= .15 and <= .65)
            {
                if (selected != 0) return new(0, confidence);
                selected = i + 1;
            }
            else if (gain is < .78 or > 1.35) return new(0, confidence);
        }
        return new(selected, confidence);
    }
    private static double[] Luma(Bitmap bitmap)
    {
        var bytes = Vision.Pixels(bitmap); var result = new double[bytes.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = (bytes[3*i] + bytes[3*i+1] + bytes[3*i+2]) / 3.0;
        return result;
    }
}
public static class FourthPartySwitch
{
    // The first 4 key press/release was already sent by the cycle. Never send E
    // here: the engine resumes it only after two fresh frames confirm slot 4.
    public static async Task<bool> Confirm(IRuntime runtime, PartySlotSettings settings, Func<int,Task<bool>> wait, Action<string> report)
    {
        var clock = RuntimeClock.StartNew(runtime);
        int attempts = 1, stable = 0, other = 0; long released = 0, pause = runtime.PausedMilliseconds;
        try
        {
            report("3→4 교체 · 활성 파티 번호 확인");
            while (clock.ElapsedMilliseconds < 2500)
            {
                if (await wait(0)) return true;
                var seen = runtime.ObservePartySlot(settings);
                if (await wait(0)) return true;
                if (pause != runtime.PausedMilliseconds) { stable = 0; other = 0; pause = runtime.PausedMilliseconds; }
                stable = seen.Slot == 4 ? stable + 1 : 0;
                other = seen.Slot is >= 1 and <= 3 ? other + 1 : 0;
                if (stable >= 2 && clock.ElapsedMilliseconds - released >= 200)
                {
                    report($"4번 교체 확인 · E 진행 (입력 {attempts}회, 형태 {seen.Confidence:P0})");
                    return false;
                }
                // Retry only while another character is visibly active. Unknown
                // or disappearing UI is a reason to wait, not to guess.
                if (other >= 2 && attempts < 4 && clock.ElapsedMilliseconds - released >= 250)
                {
                    report($"4번 교체 재시도 {++attempts}/4 · 현재 {seen.Slot}번");
                    runtime.Play(new() { Kind = InputKind.KeyDown, Code = 0x34 });
                    if (await wait(100)) return true;
                    runtime.Play(new() { Kind = InputKind.KeyUp, Code = 0x34 });
                    released = clock.ElapsedMilliseconds; other = 0;
                }
                if (await wait(60)) return true;
            }
            throw new TimeoutException("4번 교체를 확인하지 못해 E 입력 전에 중단했습니다. 낙하공격 착지·교체 가능 상태와 파티 번호 등록을 확인하세요.");
        }
        finally { runtime.Release(); }
    }
}

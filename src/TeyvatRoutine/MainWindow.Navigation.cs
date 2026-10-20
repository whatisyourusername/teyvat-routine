using System.Windows;
using System.Globalization;

namespace TeyvatRoutine;
public partial class MainWindow
{
    private void DetectionOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _changing || _busy || Selected is not Segment s) return;
        s.StopOnCompletion = s.Id != "quit" && EarlyCompletionCheck.IsChecked == true;
        if (s.Id == "collect") s.Navigation.Enabled = NavigationCheck.IsChecked == true;
        try { Save(); RefreshEditor(); }
        catch (Exception ex) { Error(ex); }
    }
    private void NavigationSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _changing || _busy || Selected is not Segment s) return;
        try
        {
            if (!double.TryParse(NavThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold) || !double.IsFinite(threshold) || threshold is < .65 or > .99 ||
                !int.TryParse(ForwardPulseBox.Text, out int pulse) || pulse is < 20 or > 400 ||
                !int.TryParse(CruisePulseBox.Text,out int cruise) || cruise is <200 or >800 ||
                !int.TryParse(NavTimeoutBox.Text, out int timeout) || timeout is < 1 or > 180 ||
                !int.TryParse(TurnGainBox.Text, out int turn) || turn is < 20 or > 1000 ||
                !int.TryParse(SearchTurnBox.Text, out int search) || search is < 10 or > 500 ||
                !int.TryParse(PitchGainBox.Text, out int pitchGain) || pitchGain is < 20 or > 600 ||
                !int.TryParse(SearchPitchBox.Text, out int pitchSearch) || pitchSearch is < 10 or > 160 ||
                !int.TryParse(ZoomOutStepsBox.Text, out int zoomSteps) || zoomSteps is < 0 or > 6)
                throw new InvalidOperationException("접근 설정 범위: 인식 0.65~0.99, 전진 20~400ms, 제한 1~180초, 좌우 보정 20~1000, 좌우 탐색 10~500, 상하 보정 20~600, 상하 탐색 10~160, 시작 축소 0~6칸.");
            var n = s.Navigation; n.MatchThreshold = threshold; n.ForwardPulseMs = pulse; n.TimeoutSeconds = timeout;
            n.FastApproach=FastApproachCheck.IsChecked==true;n.CruisePulseMs=cruise;
            n.TurnGain = turn; n.SearchTurn = search; n.InvertTurn = InvertTurnCheck.IsChecked == true;
            n.AutoPitch = AutoPitchCheck.IsChecked == true; n.InvertPitch = InvertPitchCheck.IsChecked == true;
            n.AdaptivePitch = AdaptivePitchCheck.IsChecked == true;
            n.RecenterBeforeSearch = RecenterCameraCheck.IsChecked == true;
            n.ZoomOutSteps = zoomSteps;
            n.PitchGain = pitchGain; n.SearchPitch = pitchSearch; Save();
        }
        catch (Exception ex) { Error(ex); RefreshEditor(); }
    }
    private async void AddTargetClick(object sender, RoutedEventArgs e) => await CaptureNavigation(true);
    private async Task CaptureNavigation(bool target)
    {
        if (_busy || Selected is not Segment s) return;
        if (target && s.Navigation.Templates.Count >= 5) { Error(new InvalidOperationException("나무 모습은 최대 5개입니다. 이미지를 비운 뒤 다시 등록할 수 있습니다.")); return; }
        SetBusy(true); _operation = new();
        try
        {
            var game = Game; await Countdown(game, target ? "나무 모습 캡처" : "도착 F 표시 캡처", _operation.Token);
            using var screen = Native.Capture(game); CheckSize(s, screen); Activate();
            var dialog = new CaptureWindow(screen, target ? "보상 나무 모습 등록" : "나무 도착 표시 등록", instructions: target ? "화면 중앙 부근의 나무 줄기·가지 모양을 선택하세요. 배경은 적게 포함하세요." : "나무 옆에서만 나타나는 F 키와 상호작용 이름을 함께 선택하세요.") { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (target) s.Navigation.Templates.Add(dialog.Result!); else s.Navigation.Arrival = dialog.Result;
            Save(); RefreshEditor();
            RunState.Text = target ? "나무 모습 등록 완료" : "도착 F 표시 등록 완료";
            RunDetail.Text = target ? "다른 거리에서도 나무 모습을 추가할 수 있습니다. ‘인식만 테스트’로 확인하세요." : "이제 나무 옆에서 F를 누르기 직전부터 보상 조작을 녹화하세요.";
            Log(RunState.Text + " · " + s.Name);
        }
        catch (OperationCanceledException) { Log("나무 화면 등록 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private void ClearTargetsClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s || s.Navigation.Templates.Count == 0) return;
        try
        {
            string backup = ProfileStore.BackupBeforeReset(_profile, s.Id);
            s.Navigation.Templates.Clear(); Save(); RefreshEditor();
            Log("나무 이미지만 비웠습니다. 이전 프로필: " + backup);
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void TestTargetClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s) return;
        if (s.Navigation.Templates.Count == 0) { Error(new InvalidOperationException("나무 모습을 먼저 등록하세요.")); return; }
        SetBusy(true); _operation = new();
        try
        {
            var game = Game; var token = _operation.Token;
            await Countdown(game, "나무 인식만 테스트", token);
            using var screen = Native.Capture(game); CheckSize(s, screen);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var analysis = await Task.Run(() => TargetMatcher.Analyze(screen, s.Navigation, token), token);
            var hit = analysis.Best;
            double arrival = s.Navigation.Arrival == null ? 0 : Vision.Score(screen, s.Navigation.Arrival);
            token.ThrowIfCancellationRequested();
            string result = analysis.Reason + (hit == null ? "" : $" · 가로 오차 {hit.OffsetX:P0}");
            if (hit != null)
            {
                using var g = System.Drawing.Graphics.FromImage(screen); using var pen = new System.Drawing.Pen(analysis.Accepted ? System.Drawing.Color.Lime : System.Drawing.Color.Orange, Math.Max(2, screen.Width / 500));
                g.DrawRectangle(pen, hit.Bounds);
            }
            string detail = $"{result} · 도착 표시 {arrival:P0} · {watch.ElapsedMilliseconds}ms";
            SaveDiagnostic(screen, detail);
            Activate(); new CaptureWindow(screen, "이동 없는 나무 인식 테스트", true, detail) { Owner = this }.ShowDialog();
            RunState.Text = "나무 인식 테스트 완료"; RunDetail.Text = result + ". 이동/클릭 입력은 보내지 않았습니다."; Log(RunDetail.Text);
        }
        catch (OperationCanceledException) { Log("나무 인식 테스트 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void TestApproachClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s) return;
        if (!_hotkeys) { Error(new InvalidOperationException("비상 정지 단축키를 등록하지 못했습니다.")); return; }
        var errors = s.Navigation.Validate();
        errors.AddRange(_profile.ScreenFlow.Validate(s.Width, s.Height, false));
        if (errors.Count > 0) { ShowText("접근 테스트 등록 확인", string.Join(Environment.NewLine, errors)); return; }
        SetBusy(true); _operation = new();
        try
        {
            Save(); var game = Game; var token = _operation.Token;
            await Countdown(game, "나무 접근만 테스트 · F 표시에서 자동 정지", token);
            var runtime = new LoadingRuntime(new WindowsRuntime(game), _profile.ScreenFlow, token, message => Dispatcher.Invoke(() => Log(message)));
            await Task.Run(() => TargetNavigator.Approach(runtime, s, () => false, message => Dispatcher.Invoke(() =>
            {
                s.Status = message; RunState.Text = "나무 접근만 테스트"; RunDetail.Text = message;
                Log("[접근 테스트] " + message);
            }), token));
            RunState.Text = "나무 접근 테스트 완료";
            RunDetail.Text = "F 상호작용 표시를 두 번 확인하고 정지했습니다. 보상 조작 녹화는 재생하지 않았습니다.";
            Log(RunDetail.Text);
            using var screen = Native.Capture(game);
            SaveDiagnostic(screen, RunDetail.Text);
        }
        catch (OperationCanceledException) { RunState.Text = "접근 테스트 중단"; RunDetail.Text = "누르고 있던 이동 입력을 해제했습니다."; Log(RunState.Text); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
}

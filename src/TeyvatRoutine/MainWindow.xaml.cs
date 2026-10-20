using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using MessageBox = System.Windows.MessageBox;

namespace TeyvatRoutine;
public partial class MainWindow : Window
{
    private Profile _profile = Profile.Create();
    private readonly Recorder _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private CancellationTokenSource? _operation;
    private Segment? _recordSegment;
    private GameWindow? _recordGame;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _loaded, _busy, _hotkeys, _changing, _closeAfter;
    private bool _pauseShown;
    private readonly List<string> _hotkeyRegistration = new();
    private string? _logPath;
    private List<RoutineTask> _tasks = new();
    private RoutineTask? SelectedTask => StepList.SelectedItem as RoutineTask;
    private Segment? Selected => SelectedTask?.IsGroup == true ? GroupPartPicker.SelectedItem as Segment : SelectedTask?.Parts[0];
    private GameWindow Game => WindowPicker.SelectedItem as GameWindow ?? throw new InvalidOperationException("원신을 실행하고 ‘새로고침’에서 원신 창을 선택하세요.");
    public MainWindow()
    {
        InitializeComponent();
        Height = Math.Min(1100, SystemParameters.WorkArea.Height - 60);
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle; _source = HwndSource.FromHwnd(_handle); _source.AddHook(Messages);
            bool Register(int id, uint key, string name)
            {
                bool ok = Native.RegisterHotKey(_handle, id, InputRules.RecorderModifiers, key);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                _hotkeyRegistration.Add($"{name}: {(ok ? "등록 성공" : "등록 실패")} (Windows 오류 {error})");
                return ok;
            }
            _hotkeys = Register(9, InputRules.StopKey, "Ctrl+Alt+End"); Register(6, InputRules.ModeKey, "Ctrl+Alt+Home");
        };
        Loaded += (_, _) =>
        {
            try { if (File.Exists(ProfileStore.DefaultPath)) _profile = ProfileStore.Load(ProfileStore.DefaultPath); }
            catch (Exception ex) { Log("프로필을 읽지 못했습니다: " + ex.Message); }
            _loaded = true; BindProfile(); RefreshWindows(this, new RoutedEventArgs());
            Log("준비 완료. 녹화 종료: 모든 키를 놓고 Alt+Tab → ‘녹화 종료 · 저장’ 클릭. 창을 전환해도 녹화 내용은 유지됩니다.");
            Log("단축키 진단 · " + string.Join(" / ", _hotkeyRegistration));
            if (!_hotkeys) Log("Ctrl+Alt+End 비상 정지 등록 실패로 실제 재생을 잠갔습니다. 수동 녹화와 저장 버튼은 사용할 수 있습니다.");
            _timer.Start(); SetBusy(false);
        };
        _timer.Tick += (_, _) =>
        {
            if (!_recorder.Active) return;
            try
            {
                if (_recordSegment?.Id == "quit" && _recordGame != null && !Native.Exists(_recordGame)) { _ = FinishRecording(true); return; }
                if (_recordGame != null && Native.GetForegroundWindow() != _recordGame.Handle) _recorder.Pause("원신 밖으로 창 전환");
                if (_recorder.Paused) { ShowRecordingPaused(); return; }
                Native.Ensure(_recordGame!, _recordSegment!.Width, _recordSegment.Height);
                if (Native.Bounds(_recordGame!.Handle).Location != _recorder.Origin) throw new InvalidOperationException("녹화 중 게임 창 위치가 바뀌었습니다. 시작 상태에서 다시 녹화하세요.");
                if (_recorder.ElapsedMs > 600000 || _recorder.Count >= 200000) { _recorder.Pause("녹화 제한(10분 또는 20만 입력) 도달"); ShowRecordingPaused(); return; }
                RunDetail.Text = $"{_recorder.ElapsedMs / 1000.0:0.0}초  ·  {_recorder.Count:N0}개 입력  ·  {(_recorder.Relative ? "카메라 모드" : "메뉴 모드")}  ·  모드 전환: Ctrl+Alt+Home  ·  종료: Alt+Tab → ‘녹화 종료 · 저장’";
            }
            catch (Exception ex) { _recorder.Pause(ex.Message); ShowRecordingPaused(); }
        };
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; _closeAfter = true; StopAll(); return; }
            _timer.Stop(); _recorder.Dispose();
            foreach (int id in new[] { 6, 9 }) Native.UnregisterHotKey(_handle, id);
            _source?.RemoveHook(Messages);
        };
    }
    private IntPtr Messages(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x00FF) _recorder.RawInput(l);
        if (msg == 0x0312)
        {
            handled = true;
            switch (w.ToInt32())
            {
                case 9: StopAll(); break;
                case 6: _recorder.ToggleMode(); break;
            }
        }
        return IntPtr.Zero;
    }
    private void BindProfile()
    {
        _changing = true;
        ResolutionPicker.Text = $"{_profile.ResolutionWidth} x {_profile.ResolutionHeight}";
        foreach (var task in _tasks) task.Dispose();
        _tasks = _profile.CreateTasks();
        StepList.ItemsSource = _tasks; StepList.SelectedIndex = 0;
        _changing = false; SelectTaskEditor(); RefreshTaskSummary();
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; EditorPanel.IsEnabled = !busy; StepList.IsEnabled = !busy;
        RunButton.IsEnabled = !busy && _hotkeys; SimulateButton.IsEnabled = !busy; StopButton.IsEnabled = busy;
        ResumeButton.IsEnabled=!busy;
        RefreshButton.IsEnabled = !busy; PreviewButton.IsEnabled = !busy; WindowPicker.IsEnabled = !busy; ResolutionPanel.IsEnabled = !busy;
        ElevateButton.IsEnabled = !busy;
        UpdateRecordingControls();
        if (!busy && _closeAfter) Dispatcher.BeginInvoke(Close);
    }
    private void UpdateRecordingControls()
    {
        SaveRecordingButton.Visibility = _recorder.Active ? Visibility.Visible : Visibility.Collapsed;
        SaveRecordingButton.IsEnabled = _recorder.Active;
        RunButton.Visibility = _recorder.Active ? Visibility.Collapsed : Visibility.Visible;
        SimulateButton.Visibility = _recorder.Active ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Content = _recorder.Active ? "녹화 취소" : "■  중단  Ctrl+Alt+End";
    }
    private void ShowRecordingPaused()
    {
        if (_pauseShown) return;
        _pauseShown = true;
        RunState.Text = "녹화 멈춤 · 저장 대기";
        RunDetail.Text = $"{_recorder.ElapsedMs / 1000.0:0.0}초 · {_recorder.Count:N0}개 입력 보관 중. ‘녹화 종료 · 저장’을 클릭하세요. 원신으로 돌아가도 기록은 자동 재개되지 않습니다.";
        Log($"녹화 보관: {_recorder.PauseReason} · {_recorder.Count}개 입력. 저장 또는 취소를 선택하세요.");
        UpdateRecordingControls();
    }
    private void Save()
    {
        ProfileStore.Save(_profile);
        ProfileStore.Save(_profile, ProfileStore.ResolutionPath(_profile.ResolutionWidth, _profile.ResolutionHeight));
        foreach (var s in _profile.Segments) s.Refresh();
        RefreshTaskSummary();
    }
    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(text)); return; }
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd();
        if (LogBox.Text.Length > 60000) LogBox.Text = LogBox.Text[^45000..];
        try
        {
            string dir = Path.Combine(ProfileStore.Root, "logs"); Directory.CreateDirectory(dir);
            _logPath ??= Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch { LogLabel.Text = "로그 파일 저장 실패 · 화면 로그 확인"; }
    }
    private void Error(Exception ex)
    {
        RunState.Text = "중단 · 확인 필요"; RunDetail.Text = ex.Message; Log("중단: " + ex.Message);
    }
    private void StepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == StepList && _loaded && !_changing) SelectTaskEditor();
    }
    private void SelectTaskEditor()
    {
        _changing = true;
        GroupEditor.Visibility = SelectedTask?.IsGroup == true ? Visibility.Visible : Visibility.Collapsed;
        GroupPartPicker.ItemsSource = SelectedTask?.IsGroup == true ? SelectedTask.Parts : null;
        GroupPartPicker.SelectedIndex = SelectedTask?.IsGroup == true ? 0 : -1;
        _changing = false; RefreshEditor();
    }
    private void GroupPartChanged(object sender, SelectionChangedEventArgs e) { if (_loaded && !_changing) RefreshEditor(); }
    private void TaskSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _changing || _busy) return;
        if ((sender as CheckBox)?.DataContext is RoutineTask task) StepList.SelectedItem = task;
        try { Save(); } catch (Exception ex) { Error(ex); }
    }
    private void RoundsChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loaded || _changing || _busy || e.RemovedItems.Count == 0 || sender is not ComboBox box || box.SelectedItem is not int rounds) return;
        if (box.DataContext is RoutineTask task) task.Rounds = rounds;
        try { Save(); } catch (Exception ex) { Error(ex); }
        RoundLabel.Text = $"0 / {(_profile.IsStepSelected("collect") ? rounds : 0)}";
    }
    private void RefreshTaskSummary()
    {
        ReadyLabel.Text = $"등록된 작업 {_tasks.Count(t => t.Ready)} / {_tasks.Count}";
        RoutineSummary.Text = $"비경 {_profile.DomainRuns}회 → 합성 3개 → 일일 보상";
        if (!_busy) RoundLabel.Text = $"0 / {(_profile.IsStepSelected("collect") ? _profile.DomainRuns : 0)}";
    }
    private void RefreshEditor()
    {
        if (Selected is not Segment s) return;
        _changing = true;
        StepTitle.Text = s.Name; StepHint.Text = s.Navigation.Enabled ? "전투 후 보상 나무를 찾고 F 상호작용 표시까지 접근합니다. 보상 조작 녹화는 나무 옆에서 F를 누르기 직전부터 시작하세요." : s.Hint;
        ModePicker.SelectedIndex = (s.Navigation.Enabled ? s.Navigation.ArrivalRelativeInitially : s.RelativeInitially) ? 1 : 0;
        EarlyCompletionCheck.IsChecked = s.StopOnCompletion || s.Combat.Enabled; EarlyCompletionCheck.IsEnabled = s.Id != "quit" && !s.Combat.Enabled;
        CombatSection.Visibility = s.Id == "battle" ? Visibility.Visible : Visibility.Collapsed;
        FillCombatSettings(s.Combat);
        CombatGoldTextCheck.IsChecked = s.After?.GoldTextOnly == true;
        CombatSummary.Text = s.Combat.Enabled ? s.Combat.Summary(s) + $" · 최대 {s.Combat.MaxCycles}사이클 / {s.Combat.TimeoutSeconds}초" : "꺼짐 · 현재 녹화를 분석해 반복 구간을 설정할 수 있습니다.";
        AutoMouseCheck.IsChecked = s.AutoMouseMode;
        AlignmentCheck.IsChecked = s.Alignment.Enabled;
        AlignmentSummary.Text = s.Alignment.Reference == null ? "시점 기준 미등록 · 전투 시작 위치에서 원하는 방향을 바라보고 등록하세요." : "시점 기준 등록됨 · 이동 없이 기준물의 화면 위치를 맞춥니다. 나무 접근 모드에는 적용하지 않습니다.";
        NavigationSection.Visibility = s.Id == "collect" ? Visibility.Visible : Visibility.Collapsed;
        NavigationCheck.IsChecked = s.Navigation.Enabled;
        BeforeButton.Content = s.Navigation.Enabled ? "도착 F 표시 등록" : "시작 화면 등록";
        RecordButton.Content = s.Navigation.Enabled ? "●  보상 조작 녹화" : "●  구간 녹화";
        var n = s.Navigation;
        TargetSummary.Text = $"나무 모습 {n.Templates.Count}/5개 · 도착 F 표시 {(n.Arrival == null ? "미등록" : "등록됨")} · 기존 구간 녹화 {s.Inputs.Count}개 입력 보관";
        NavThresholdBox.Text = n.MatchThreshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        ForwardPulseBox.Text = n.ForwardPulseMs.ToString(); NavTimeoutBox.Text = n.TimeoutSeconds.ToString();
        FastApproachCheck.IsChecked=n.FastApproach;CruisePulseBox.Text=n.CruisePulseMs.ToString();
        TurnGainBox.Text = n.TurnGain.ToString(); SearchTurnBox.Text = n.SearchTurn.ToString(); InvertTurnCheck.IsChecked = n.InvertTurn;
        AutoPitchCheck.IsChecked = n.AutoPitch; InvertPitchCheck.IsChecked = n.InvertPitch;
        AdaptivePitchCheck.IsChecked = n.AdaptivePitch;
        RecenterCameraCheck.IsChecked = n.RecenterBeforeSearch;
        ZoomOutStepsBox.Text = n.ZoomOutSteps.ToString();
        PitchGainBox.Text = n.PitchGain.ToString(); SearchPitchBox.Text = n.SearchPitch.ToString();
        TimeoutBox.Text = s.TimeoutSeconds.ToString(); InputSummary.Text = s.Detail + (s.Width > 0 ? $"  ·  {s.Width}×{s.Height}" : "");
        BeforeLabel.Text = s.Navigation.Enabled ? (n.Arrival == null ? "도착 F 표시 · 미등록" : "도착 F 표시 · 등록됨") : s.Before == null ? "시작 화면 · 미등록" : "시작 화면 · 등록됨";
        AfterLabel.Text = s.Id == "quit" ? "완료 · 프로세스 종료 확인" : s.After == null ? "완료 화면 · 미등록" : "완료 화면 · 등록됨";
        try
        {
            BeforeImage.Source = null; AfterImage.Source = null;
            var before = s.Navigation.Enabled ? n.Arrival : s.Before;
            if (before != null) { using var b = Vision.Decode(before); BeforeImage.Source = Vision.Image(b); }
            if (s.After != null) { using var b = Vision.Decode(s.After); AfterImage.Source = Vision.Image(b); }
            TargetImages.Children.Clear();
            foreach (var target in n.Templates) { using var b = Vision.Decode(target); TargetImages.Children.Add(new System.Windows.Controls.Image { Source = Vision.Image(b), Width = 90, Height = 65, Margin = new Thickness(0, 0, 8, 0), Stretch = System.Windows.Media.Stretch.Uniform }); }
        }
        catch (Exception ex) { Log("이미지 확인 실패: " + ex.Message); }
        RefreshTaskSummary();
        _changing = false;
    }
    private void RefreshWindows(object sender, RoutedEventArgs e)
    {
        var previous = (WindowPicker.SelectedItem as GameWindow)?.ProcessId;
        var windows = Native.FindGames(); WindowPicker.ItemsSource = windows;
        WindowPicker.SelectedItem = windows.FirstOrDefault(w => w.ProcessId == previous) ?? (windows.Count == 1 ? windows[0] : null);
        if (windows.Count == 0) WindowStatus.Text = "원신 실행 창을 찾지 못했습니다";
    }
    private void WindowChanged(object sender, SelectionChangedEventArgs e)
    {
        PermissionPanel.Visibility = Visibility.Collapsed;
        try
        {
            if (WindowPicker.SelectedItem is GameWindow game)
            {
                var r = Native.Bounds(game.Handle); WindowStatus.Text = $"{r.Width} × {r.Height} 실제 px · DPI {Native.GetDpiForWindow(game.Handle)}";
                if (Native.Elevation(Environment.ProcessId) == "일반" && Native.Elevation(game.ProcessId) == "관리자") PermissionPanel.Visibility = Visibility.Visible;
            }
        }
        catch { WindowStatus.Text = "창을 다시 선택하세요"; }
    }
    private void ElevateClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            Save();
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!, WorkingDirectory = AppContext.BaseDirectory,
                Arguments = "--wait-for-process " + Environment.ProcessId,
                UseShellExecute = true, Verb = "runas"
            });
            if (process != null) { process.Dispose(); Close(); }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("관리자 권한 재실행을 취소했습니다. 현재 프로그램을 유지합니다."); }
        catch (Exception ex) { Error(ex); }
    }
    private void SwitchResolution(int width, int height)
    {
        if (_busy) return;
        string path = ProfileStore.ResolutionPath(width, height);
        if (width == _profile.ResolutionWidth && height == _profile.ResolutionHeight) { Log($"현재 프로필: {width}×{height}"); return; }
        Save();
        var next = File.Exists(path) ? ProfileStore.Load(path) : Profile.Create();
        next.ResolutionWidth = width; next.ResolutionHeight = height;
        _profile = next; Save(); BindProfile();
        RunState.Text = $"{width}×{height} 프로필"; RunDetail.Text = "이 해상도의 녹화와 화면 확인 정보를 별도로 저장합니다. 창 위치는 바뀌어도 됩니다.";
        Log($"해상도 프로필 전환: {width}×{height}");
    }
    private void ApplyResolutionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var match = Regex.Match(ResolutionPicker.Text, @"^\s*(\d{3,5})\s*[xX×]\s*(\d{3,5})\s*$");
            if (!match.Success) throw new InvalidDataException("예: 3840 x 2160. 가로와 세로를 입력하세요.");
            SwitchResolution(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
        catch (Exception ex) { Error(ex); }
    }
    private void CurrentResolutionClick(object sender, RoutedEventArgs e)
    {
        try { var r = Native.Bounds(Game.Handle); SwitchResolution(r.Width, r.Height); ResolutionPicker.Text = $"{r.Width} x {r.Height}"; }
        catch (Exception ex) { Error(ex); }
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _changing || Selected == null) return;
        if (Selected.Navigation.Enabled) Selected.Navigation.ArrivalRelativeInitially = ModePicker.SelectedIndex == 1;
        else Selected.RelativeInitially = ModePicker.SelectedIndex == 1;
        try { Save(); } catch (Exception ex) { Error(ex); }
    }
    private void TimeoutChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _changing || Selected == null) return;
        if (!int.TryParse(TimeoutBox.Text, out int n) || n is < 1 or > 180) { TimeoutBox.Text = Selected.TimeoutSeconds.ToString(); return; }
        Selected.TimeoutSeconds = n; try { Save(); } catch (Exception ex) { Error(ex); }
    }
    private async Task Countdown(GameWindow game, string action, CancellationToken token)
    {
        Native.Activate(game);
        for (int i = 3; i > 0; i--) { RunState.Text = $"{action} · {i}초"; await Task.Delay(1000, token); }
        token.ThrowIfCancellationRequested(); Native.Ensure(game);
    }
    private void CheckSize(Segment s, System.Drawing.Bitmap b)
    {
        if (b.Width != _profile.ResolutionWidth || b.Height != _profile.ResolutionHeight) throw new InvalidOperationException($"현재 게임은 {b.Width}×{b.Height}, 선택한 프로필은 {_profile.ResolutionWidth}×{_profile.ResolutionHeight}입니다. ‘현재 창 크기’를 눌러 해당 프로필로 전환하세요.");
        if (s.Width > 0 && (s.Width != b.Width || s.Height != b.Height)) throw new InvalidOperationException($"이 구간은 {s.Width}×{s.Height}에서 등록되었습니다. 게임 해상도를 맞춰 주세요.");
        s.Width = b.Width; s.Height = b.Height;
    }
    private async Task CaptureFor(Segment s, bool before, GameWindow game, CancellationToken token)
    {
        await Countdown(game, before ? "시작 화면 캡처" : "완료 화면 캡처", token);
        using var b = Native.Capture(game); CheckSize(s, b);
        Activate();
        var dialog = new CaptureWindow(b, s.Name + (before ? " · 시작 화면" : " · 완료 화면")) { Owner = this };
        if (dialog.ShowDialog() == true) { if (before) s.Before = dialog.Result; else s.After = dialog.Result; Save(); RefreshEditor(); Log(s.Name + (before ? " 시작" : " 완료") + " 화면 등록"); }
    }
    private async Task CaptureSelected(bool before)
    {
        if (_busy || Selected == null) return;
        if (!before && Selected.Id == "quit") { Log("게임 종료 구간은 프로세스 종료를 확인합니다. 완료 이미지가 필요하지 않습니다."); return; }
        SetBusy(true); _operation = new();
        try { await CaptureFor(Selected, before, Game, _operation.Token); RunState.Text = "화면 등록 완료"; RunDetail.Text = "시작과 완료 화면은 해당 상태에서 각각 등록하세요."; }
        catch (OperationCanceledException) { Log("캡처 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void BeforeClick(object s, RoutedEventArgs e) { if (Selected?.Navigation.Enabled == true) await CaptureNavigation(false); else await CaptureSelected(true); }
    private async void AfterClick(object s, RoutedEventArgs e) => await CaptureSelected(false);
    private async void PreviewClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true); _operation = new();
        try
        {
            var game = Game; await Countdown(game, "화면 확인", _operation.Token);
            using var b = Native.Capture(game); Activate(); new CaptureWindow(b, "원신 화면 확인", true) { Owner = this }.ShowDialog();
            RunState.Text = "화면 연결 확인"; RunDetail.Text = $"{b.Width}×{b.Height} 캡처 완료. 게임 입력은 보내지 않았습니다."; Log(RunDetail.Text);
        }
        catch (OperationCanceledException) { Log("화면 확인 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void RecordClick(object sender, RoutedEventArgs e) => await StartRecording();
    private void ResetSegmentClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s) return;
        try
        {
            var backup = ProfileStore.BackupBeforeReset(_profile, s.Id);
            s.ResetRegistration(); Save(); RefreshEditor();
            RunState.Text = s.Name + " · 초기화 완료";
            RunDetail.Text = "선택 구간의 녹화와 시작·완료 화면을 지웠습니다. 게임을 시작 상태로 맞추고 ‘구간 녹화’를 누르세요.";
            Log($"구간 초기화: {s.Name} · 이전 프로필 백업: {backup}");
        }
        catch (Exception ex) { Error(ex); }
    }
    private async Task StartRecording()
    {
        if (_busy || Selected == null) return;
        SetBusy(true); _operation = new();
        try
        {
            var game = Game; var s = Selected;
            Log($"실행 권한 진단 · 프로그램={Native.Elevation(Environment.ProcessId)}, 원신={Native.Elevation(game.ProcessId)}");
            RunDetail.Text = "게임을 시작 상태로 맞추세요. 완료 후 모든 키를 놓고 Alt+Tab으로 돌아와 ‘녹화 종료 · 저장’을 클릭하세요.";
            if (!s.Navigation.Enabled && s.Before == null) { await CaptureFor(s, true, game, _operation.Token); if (s.Before == null) throw new OperationCanceledException(); }
            if (s.Navigation.Enabled && s.Navigation.Arrival == null) throw new InvalidOperationException("먼저 나무 옆의 ‘도착 F 표시’를 등록하세요. 이동 없이 F 조작부터 녹화합니다.");
            await Countdown(game, "녹화 시작", _operation.Token);
            if (s.Navigation.Enabled)
            {
                using var screen = Native.Capture(game); CheckSize(s, screen);
                double score = Vision.Score(screen, s.Navigation.Arrival!);
                string detail = $"보상 녹화 시작 · 도착 F 일치도 {score:P1} / 기준 {s.Navigation.Arrival!.Threshold:P1}";
                if (score < s.Navigation.Arrival.Threshold)
                {
                    // A transient visual miss must not prevent the user from teaching the action.
                    // Automated navigation still requires two matching arrival observations.
                    detail += " · 기준 미달이지만 수동 녹화는 시작합니다. F를 누르기 직전부터 조작하세요.";
                    try { SaveDiagnostic(screen, detail); }
                    catch (Exception ex) { Log("녹화 시작 진단 저장 실패: " + ex.Message); }
                }
                Log(detail);
            }
            var r = Native.Bounds(game.Handle);
            if (s.Width != r.Width || s.Height != r.Height) throw new InvalidOperationException("시작 화면 등록 때와 해상도가 다릅니다.");
            _recordGame = game; _recordSegment = s; _recorder.Start(game, _handle, s.Navigation.Enabled ? s.Navigation.ArrivalRelativeInitially : s.RelativeInitially, s.AutoMouseMode);
            _pauseShown = false; UpdateRecordingControls();
            RunState.Text = "● 녹화 중 · " + s.Name; Log("녹화 시작: " + s.Name + " · 종료: Alt+Tab → ‘녹화 종료 · 저장’. 다른 창으로 전환하면 입력 기록을 멈추고 보관합니다.");
        }
        catch (OperationCanceledException) { Log("녹화 준비 취소"); _operation?.Dispose(); _operation = null; SetBusy(false); }
        catch (Exception ex) { Error(ex); _recorder.Dispose(); _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void SaveRecordingClick(object sender, RoutedEventArgs e)
    {
        if (!_recorder.Active) return;
        Log("수동 녹화 저장 버튼 클릭");
        await FinishRecording(true);
    }
    private async Task FinishRecording(bool save)
    {
        if (!_recorder.Active) return;
        var recording = _recorder.Stop();
        Log(_recorder.Diagnostics);
        UpdateRecordingControls();
        bool inputsSaved = false;
        try
        {
            var s = _recordSegment!;
            if (!save) { Log("녹화 취소 · 기존 구간 유지"); RunState.Text = "녹화를 취소했습니다"; RunDetail.Text = "이번 녹화를 버렸습니다. 기존에 저장한 구간은 유지됩니다."; return; }
            s.StoreRecording(recording.Inputs, recording.Duration);
            Save(); inputsSaved = true;
            if (s.Id != "quit" && s.After == null)
            {
                // Clicking Save activates this app. Return to the game before capturing it.
                await Countdown(_recordGame!, "입력 저장 완료 · 완료 화면 캡처", _operation!.Token);
                using var b = Native.Capture(_recordGame!); CheckSize(s, b); Activate();
                var dialog = new CaptureWindow(b, s.Name + " · 완료 화면 선택") { Owner = this };
                if (dialog.ShowDialog() == true) s.After = dialog.Result;
            }
            Save(); s.Status = ""; RefreshEditor(); Log($"녹화 저장: {s.Name} · {s.Detail}");
            RunState.Text = "녹화 저장 완료"; RunDetail.Text = s.After == null && s.Id != "quit" ? "완료 화면을 등록하면 이 구간을 테스트할 수 있습니다." : "해당 구간의 시작 상태에서 ‘선택 구간 테스트’로 확인하세요.";
        }
        catch (Exception ex)
        {
            if (inputsSaved)
            {
                RunState.Text = "입력 녹화 저장 완료 · 완료 화면 확인 필요";
                RunDetail.Text = "입력은 저장했습니다. 게임을 완료 상태로 맞춘 뒤 ‘완료 화면 등록’을 눌러 주세요.";
                Log(RunDetail.Text + " 원인: " + ex.Message);
            }
            else Error(ex);
        }
        finally { _operation?.Dispose(); _operation = null; _recordSegment = null; _recordGame = null; SetBusy(false); RefreshEditor(); Activate(); }
    }
    private void StopAll()
    {
        if (_recorder.Active) _ = FinishRecording(false);
        else _operation?.Cancel();
    }
    private void StopClick(object sender, RoutedEventArgs e) => StopAll();
    private async void RunClick(object sender, RoutedEventArgs e) => await RunPlan(Workflow.Build(_profile));
    private async void TestClick(object sender, RoutedEventArgs e) { if (Selected != null) await RunPlan(new() { new(Selected) }); }
    private async Task RunPlan(List<PlanItem> plan)
    {
        if (_busy) return;
        if (!_hotkeys) { Error(new InvalidOperationException("비상 정지 단축키를 등록하지 못했습니다.")); return; }
        var errors = Workflow.Validate(plan, _profile.ScreenFlow);
        if (plan.Count == 0) errors.Add("실행할 작업이 없습니다.");
        if (errors.Count > 0) { ShowText("실행 전 등록 확인", string.Join(Environment.NewLine, errors)); Log("등록되지 않은 구간이 있어 실제 실행하지 않았습니다."); return; }
        SetBusy(true); _operation = new();
        foreach (var s in _profile.Segments) s.Status = "";
        var engine = default(RoutineEngine);
        try
        {
            Save(); var game = Game; await Countdown(game, "루틴 시작", _operation.Token);
            engine = new(new WindowsRuntime(game), update => Dispatcher.Invoke(() =>
            {
                var segment = _profile.Get(update.StageId); segment.Status = update.Message;
                RunState.Text = segment.Name; RunDetail.Text = update.Message;
                RoundLabel.Text = $"{update.CompletedRewards} / {plan.Count(i => i.Segment.Id == "collect")}";
                Progress.Value = update.FinishedStages * 100.0 / update.TotalStages;
                Log(segment.Name + " · " + update.Message);
            }), _profile.ScreenFlow);
            RoundLabel.Text = $"0 / {plan.Count(i => i.Segment.Id == "collect")}"; Progress.Value = 0;
            var token = _operation.Token;
            await Task.Run(() => engine.Run(plan, token));
            RunState.Text = "선택한 작업 완료"; RunDetail.Text = $"보상 화면 확인 {engine.CompletedRewards}회 · 모든 구간의 완료 조건을 통과했습니다."; Log(RunDetail.Text);
        }
        catch (OperationCanceledException) { RunState.Text = "실행 중단"; RunDetail.Text = "중단 요청 또는 실행 시간 제한. 누르고 있던 입력을 해제했습니다."; Log(RunDetail.Text); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void SimulateClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return; SetBusy(true); _operation = new();
        var plan = Workflow.Build(_profile); int count = 0, done = 0;
        foreach (var s in _profile.Segments) s.Status = "";
        try
        {
            Log("시뮬레이션 시작 · 화면 캡처, 게임 입력, 자원 소비 없음");
            foreach (var item in plan)
            {
                _operation.Token.ThrowIfCancellationRequested();
                item.Segment.Status = "시뮬레이션" + (item.Round > 0 ? $" ({item.Round}/{_profile.DomainRuns})" : "");
                RunState.Text = "순서 확인 · " + item.Segment.Name; RunDetail.Text = "예정된 순서만 표시합니다. 실제 게임은 조작하지 않습니다.";
                await Task.Delay(350, _operation.Token);
                if (item.Segment.Id == "collect") count++;
                RoundLabel.Text = $"예상 {count} / {plan.Count(i => i.Segment.Id == "collect")}"; Progress.Value = ++done * 100.0 / Math.Max(1, plan.Count);
                Log("[시뮬레이션] " + item.Segment.Name + (item.Round > 0 ? $" · {item.Round}회차" : ""));
            }
            RunState.Text = "순서 시뮬레이션 완료"; RunDetail.Text = "실제 수행 결과가 아닙니다. 구간 등록 후 실제 실행할 수 있습니다.";
        }
        catch (OperationCanceledException) { Log("시뮬레이션 중단"); RunState.Text = "시뮬레이션 중단"; }
        finally { _operation.Dispose(); _operation = null; SetBusy(false); }
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "루틴 프로필 (*.json)|*.json", FileName = "snezhnaya-routine.json" };
        if (dialog.ShowDialog(this) == true) try { ProfileStore.Save(_profile, dialog.FileName); Log("프로필 내보내기 완료"); } catch (Exception ex) { Error(ex); }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "루틴 프로필 (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) try { var loaded = ProfileStore.Load(dialog.FileName); ProfileStore.Save(loaded); _profile = loaded; BindProfile(); Log("프로필 불러오기 완료. 시작 화면을 먼저 확인하세요."); } catch (Exception ex) { Error(ex); }
    }
    private void InputsClick(object sender, RoutedEventArgs e) { if (Selected != null) ShowText(Selected.Name + " · 입력 목록", string.Join(Environment.NewLine, Selected.PlaybackInputs.Select(i => i.Display))); }
    private void GuideClick(object sender, RoutedEventArgs e) => ShowText("Teyvat Routine · 사용 가이드", Guide.Text);
    private void ShowText(string title, string text)
    {
        var box = new System.Windows.Controls.TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20), Padding = new Thickness(12) };
        new Window { Owner = this, Title = title, Width = 760, Height = 620, MinHeight = 300, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
}


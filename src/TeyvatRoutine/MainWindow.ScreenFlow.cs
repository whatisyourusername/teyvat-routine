using System.Windows;
using System.Windows.Controls;

namespace TeyvatRoutine;
public partial class MainWindow
{
    private async void ScreenFlowClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var flow = _profile.ScreenFlow;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Owner = this, Title = "로그인·로딩 설정", Width = 600, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MaxHeight = SystemParameters.WorkArea.Height - 60,
            Style = (Style)FindResource(typeof(Window)), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        panel.Children.Add(new TextBlock { Text = "이 해상도 프로필에 저장됩니다. 로그인 자동 처리는 기존 녹화 대신 첫 화면 → 문 → 필드 순서로 확인합니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        CheckBox Toggle(string text, bool value)
        { var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) }; panel.Children.Add(box); return box; }
        var login = Toggle("문 화면을 확인한 뒤 로그인", flow.LoginEnabled);
        var loading = Toggle("모든 구간에서 로딩 중 입력·재생 시간 일시 정지", flow.LoadingEnabled);
        var welkin = Toggle("공월이 나타나면 빈 곳을 클릭해 닫기", flow.WelkinEnabled);
        panel.Children.Add(new TextBlock { Text = $"문: {(flow.Door == null ? "미등록" : "등록됨")} · 필드: {(flow.Field == null ? "미등록" : "등록됨")} · 입장 안내: {(flow.DomainNotice == null ? "미등록" : "등록됨")} · 공월: {(flow.Welkin == null ? "미등록 (실제 팝업에서 등록)" : "등록됨")} · 로딩 {flow.LoadingScreens.Count}/12개",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        string? capture = null; bool test = false;
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        foreach (var (label, kind) in new[] { ("첫 게임 시작 등록", "start"), ("문 화면 등록", "door"), ("필드 UI 등록", "field"), ("비경 UI 등록", "domain"), ("입장 안내 등록", "notice"), ("공월 화면 등록", "welkin"), ("로딩 화면 추가", "loading") })
        {
            var button = new Button { Content = label }; buttons.Children.Add(button);
            button.Click += (_, _) => { capture = kind; dialog.DialogResult = true; };
        }
        panel.Children.Add(new TextBlock { Text = "문: 중앙 문짝만 선택. 입장 안내: ‘지맥 이상’ 제목 선택. 공월: 고유한 제목·아이콘 선택. 로딩: 변하는 설명 문구를 빼고 고유 문양·원소 아이콘을 선택하세요.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 12) });
        var list = new ComboBox { ItemsSource = flow.LoadingScreens.Select((c, i) => $"로딩 {i + 1} · ({c.X}, {c.Y}) {c.Width}×{c.Height} · 기준 {c.Threshold:P0}").ToArray(),
            SelectedIndex = flow.LoadingScreens.Count > 0 ? 0 : -1 };
        panel.Children.Add(list);
        var remove = new Button { Content = "선택한 로딩 이미지 삭제", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 10) };
        panel.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            ProfileStore.BackupBeforeReset(_profile, "login");
            flow.LoadingScreens.RemoveAt(list.SelectedIndex);
            if (flow.LoadingScreens.Count == 0) flow.LoadingEnabled = false;
            Save(); dialog.Close(); ScreenFlowClick(sender, e);
        };
        TextBox Field(string label, int value)
        { panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(box); return box; }
        var update = Field("업데이트·문 대기 제한 (30~1800초)", flow.UpdateTimeoutSeconds);
        var load = Field("로딩·게임 진입 제한 (5~600초)", flow.LoadingTimeoutSeconds);
        var quiet = Field("필드 확인 뒤 공월 표시 대기 (1~15초)", flow.FieldQuietMs / 1000);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var footer = new WrapPanel(); panel.Children.Add(footer);
        var save = new Button { Content = "설정 저장" }; footer.Children.Add(save);
        var check = new Button { Content = "현재 화면 인식 테스트 · 입력 없음" }; footer.Children.Add(check);
        bool Store()
        {
            if (!int.TryParse(update.Text, out int u) || u is < 30 or > 1800 || !int.TryParse(load.Text, out int l) || l is < 5 or > 600
                || !int.TryParse(quiet.Text, out int q) || q is < 1 or > 15)
            { status.Text = "대기 시간을 표시된 범위로 입력하세요."; return false; }
            if (login.IsChecked == true && flow.Door == null || welkin.IsChecked == true && flow.Welkin == null || loading.IsChecked == true && (flow.LoadingScreens.Count == 0 || flow.Field == null))
            { status.Text = "켜려는 기능의 화면을 먼저 등록하세요."; return false; }
            flow.LoginEnabled = login.IsChecked == true; flow.LoadingEnabled = loading.IsChecked == true; flow.WelkinEnabled = welkin.IsChecked == true;
            flow.UpdateTimeoutSeconds = u; flow.LoadingTimeoutSeconds = l; flow.FieldQuietMs = q * 1000;
            Save(); BindProfile(); return true;
        }
        save.Click += (_, _) => { try { if (Store()) dialog.DialogResult = true; } catch (Exception ex) { status.Text = ex.Message; } };
        check.Click += (_, _) => { test = true; dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true || capture == null && !test) return;
        SetBusy(true); _operation = new();
        try
        {
            var game = Game; await Countdown(game, test ? "로그인·로딩 인식 확인" : "로그인·로딩 화면 캡처", _operation.Token);
            using var screen = Native.Capture(game);
            CheckSize(_profile.Get("login"), screen); Activate();
            if (test)
            {
                var entries = new List<(string, Checkpoint?)> { ("첫 시작", flow.Start), ("문", flow.Door), ("공월", flow.Welkin), ("필드", flow.Field ?? _profile.Get("login").After), ("비경 UI", flow.DomainField), ("입장 안내", flow.DomainNotice) };
                entries.AddRange(flow.LoadingScreens.Select((c, i) => ($"로딩 {i + 1}", (Checkpoint?)c)));
                var lines = entries.Where(x => x.Item2 != null).Select(x =>
                { double score = Vision.Score(screen, x.Item2!); return $"{x.Item1}: {score:P1} / 기준 {x.Item2!.Threshold:P1} · {(score >= x.Item2.Threshold ? "통과" : "미달")}"; });
                string detail = string.Join("\n", lines); SaveDiagnostic(screen, detail);
                new CaptureWindow(screen, "로그인·로딩 인식 테스트", true, detail) { Owner = this }.ShowDialog();
            }
            else
            {
                if (capture == "loading" && flow.LoadingScreens.Count >= 12) throw new InvalidOperationException("로딩 이미지는 최대 12개입니다. 이전 이미지를 선택해 삭제하세요.");
                var crop = new CaptureWindow(screen, capture switch { "door" => "문 화면 등록", "welkin" => "공월 화면 등록", "field" => "필드 UI 등록 · 좌측 위 페이몬 아이콘", "domain" => "비경 UI 등록 · 좌측 아래 Enter 표시", "notice" => "입장 안내 등록 · ‘지맥 이상’ 제목만 선택", "start" => "첫 게임 시작 글자 등록", _ => "로딩 화면 추가" }) { Owner = this };
                if (crop.ShowDialog() == true)
                {
                    crop.Result!.SearchRadius = 4;
                    if (capture == "door") { flow.Door = crop.Result; flow.LoginEnabled = true; }
                    else if (capture == "welkin") { flow.Welkin = crop.Result; flow.WelkinEnabled = true; }
                    else if (capture == "start") flow.Start = crop.Result;
                    else if (capture == "field") { flow.Field = crop.Result; flow.LoadingEnabled = flow.LoadingScreens.Count > 0; }
                    else if (capture == "domain") flow.DomainField = crop.Result;
                    else if (capture == "notice") { flow.DomainNotice = crop.Result; flow.DomainNotice.WhiteForegroundOnly = true; }
                    else { flow.LoadingScreens.Add(crop.Result); flow.LoadingEnabled = flow.Field != null; }
                    Save(); BindProfile(); Log("로그인·로딩 화면 등록: " + capture);
                }
            }
        }
        catch (OperationCanceledException) { Log("로그인·로딩 화면 작업 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
}

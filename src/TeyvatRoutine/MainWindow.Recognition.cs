using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace TeyvatRoutine;
public partial class MainWindow
{
    private void RecognitionOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _changing || _busy || Selected is not Segment s) return;
        s.AutoMouseMode = AutoMouseCheck.IsChecked == true;
        s.Alignment.Enabled = AlignmentCheck.IsChecked == true;
        try { Save(); RefreshEditor(); } catch (Exception ex) { Error(ex); }
    }
    private void RecognitionSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s) return;
        var dialog = new Window { Owner = this, Title = "화면 인식 기준", Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource(typeof(Window)) };
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = "낮출수록 쉽게 통과합니다. ‘화면 인식 테스트’ 점수를 보고 조절하세요. 서로 다른 화면이 통과하지 않는지도 확인하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var fields = new List<(Checkpoint Checkpoint, TextBox Box)>();
        foreach (var (name, checkpoint) in new[] { ("시작", s.Before), ("완료", s.After), ("나무 도착 F", s.Navigation.Arrival) })
        {
            if (checkpoint == null) continue;
            panel.Children.Add(new TextBlock { Text = name + " 기준 (0.65~0.99)" });
            var box = new TextBox { Text = checkpoint.Threshold.ToString("0.00", CultureInfo.InvariantCulture), Margin = new Thickness(0, 4, 0, 8) };
            fields.Add((checkpoint, box)); panel.Children.Add(box);
        }
        panel.Children.Add(new TextBlock { Text = "위치 오차 허용 (0~100 px, 기본 18)" });
        var radius = new TextBox { Text = (s.Before ?? s.After ?? s.Navigation.Arrival)?.SearchRadius.ToString() ?? "18", Margin = new Thickness(0, 4, 0, 12) }; panel.Children.Add(radius);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(status);
        var save = new Button { Content = "저장", Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            var values = new List<double>();
            foreach (var field in fields)
            {
                if (!double.TryParse(field.Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value is < .65 or > .99)
                { status.Text = "기준은 0.65~0.99 사이로 입력하세요."; return; }
                values.Add(value);
            }
            if (!int.TryParse(radius.Text, out int r) || r is < 0 or > 100) { status.Text = "위치 오차는 0~100 정수입니다."; return; }
            for (int i = 0; i < fields.Count; i++) { fields[i].Checkpoint.Threshold = values[i]; fields[i].Checkpoint.SearchRadius = r; }
            try { Save(); dialog.DialogResult = true; } catch (Exception ex) { status.Text = ex.Message; }
        };
        dialog.ShowDialog();
    }
    private void SaveDiagnostic(System.Drawing.Bitmap screen, string detail)
    {
        string root = Path.Combine(ProfileStore.Root, "diagnostics"); Directory.CreateDirectory(root);
        string stem = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        screen.Save(stem + ".png", System.Drawing.Imaging.ImageFormat.Png); File.WriteAllText(stem + ".txt", detail);
        Log("인식 진단 저장: " + stem + ".png");
    }
    private async void CheckScreensClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s) return;
        SetBusy(true); _operation = new();
        try
        {
            var game = Game; await Countdown(game, "화면 인식 점수 확인", _operation.Token);
            using var screen = Native.Capture(game); CheckSize(s, screen);
            var results = new List<string>();
            foreach (var (name, checkpoint) in new[] { ("시작", s.Before), ("완료", s.After), ("도착 F", s.Navigation.Arrival) })
            {
                if (checkpoint == null) continue;
                double score = await Task.Run(() => Vision.Score(screen, checkpoint), _operation.Token);
                results.Add($"{name}: {score:P1} / 기준 {checkpoint.Threshold:P1} · {(score >= checkpoint.Threshold ? "통과" : "미달")}");
            }
            string detail = results.Count == 0 ? "등록된 확인 이미지가 없습니다." : string.Join("\n", results);
            SaveDiagnostic(screen, detail); Activate(); new CaptureWindow(screen, "입력 없는 화면 인식 테스트", true, detail) { Owner = this }.ShowDialog();
            Log(detail.Replace('\n', ' '));
        }
        catch (OperationCanceledException) { Log("인식 테스트 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
    private async void CaptureAlignmentClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s) return;
        SetBusy(true); _operation = new();
        try
        {
            var game = Game; await Countdown(game, "원하는 시점의 기준물 캡처", _operation.Token);
            using var screen = Native.Capture(game); CheckSize(s, screen); Activate();
            var dialog = new CaptureWindow(screen, "시점 기준물 등록", instructions: "원하는 방향을 바라본 상태에서 움직이지 않는 문양·기둥 등을 선택하세요. 캐릭터·적·HUD는 피하세요.") { Owner = this };
            if (dialog.ShowDialog() == true) { s.Alignment.Reference = dialog.Result; s.Alignment.Enabled = true; Save(); RefreshEditor(); Log("시점 기준 등록: " + s.Name); }
        }
        catch (OperationCanceledException) { Log("시점 기준 등록 취소"); }
        catch (Exception ex) { Error(ex); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }
}

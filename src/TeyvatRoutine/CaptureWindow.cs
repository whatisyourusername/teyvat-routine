using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WPoint = System.Windows.Point;
using Image = System.Windows.Controls.Image;

namespace TeyvatRoutine;
public sealed class CaptureWindow : Window
{
    private readonly Bitmap _bitmap;
    private readonly Canvas _canvas;
    private readonly System.Windows.Shapes.Rectangle _selection;
    private readonly TextBlock _status;
    private readonly double _scale;
    private WPoint? _start;
    public Checkpoint? Result { get; private set; }
    public CaptureWindow(Bitmap bitmap, string title, bool viewOnly = false, string? instructions = null)
    {
        _bitmap = bitmap; Title = title; Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window)); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _scale = Math.Min(1, Math.Min(Math.Min(1100, SystemParameters.WorkArea.Width - 130) / bitmap.Width, Math.Min(650, SystemParameters.WorkArea.Height - 280) / bitmap.Height));
        Width = bitmap.Width * _scale + 60; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 40; ResizeMode = ResizeMode.NoResize;
        var root = new StackPanel { Margin = new Thickness(20) }; Content = root;
        root.Children.Add(new TextBlock { Text = instructions ?? (viewOnly ? "게임 창 캡처" : "변하지 않는 글자·아이콘을 드래그해서 선택하세요"), TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new TextBlock { Text = viewOnly ? $"{bitmap.Width} × {bitmap.Height} px · 실제 입력 없음" : "16×16 이상, 800×500 이하. 완료 화면에는 해당 단계의 성공을 판별할 수 있는 부분을 선택하세요.", FontSize = 11, Margin = new Thickness(0, 0, 0, 12) });
        _canvas = new Canvas { Width = bitmap.Width * _scale, Height = bitmap.Height * _scale, Background = System.Windows.Media.Brushes.Black, ClipToBounds = true };
        _canvas.Children.Add(new Image { Source = Vision.Image(bitmap), Width = _canvas.Width, Height = _canvas.Height, Stretch = Stretch.Fill, IsHitTestVisible = false });
        _selection = new() { Width = 0, Height = 0, Stroke = System.Windows.Media.Brushes.Gold, StrokeThickness = 2, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 242, 206, 134)), IsHitTestVisible = false };
        Canvas.SetLeft(_selection, 0); Canvas.SetTop(_selection, 0);
        _canvas.Children.Add(_selection); root.Children.Add(_canvas);
        _status = new TextBlock { Text = viewOnly ? "창 모드 또는 테두리 없는 창 모드를 권장합니다." : "아직 선택한 영역이 없습니다.", Margin = new Thickness(0, 8, 0, 8), FontSize = 11 }; root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; root.Children.Add(buttons);
        var cancel = new System.Windows.Controls.Button { Content = viewOnly ? "닫기" : "취소", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) }; buttons.Children.Add(cancel);
        if (viewOnly) return;
        var save = new System.Windows.Controls.Button { Content = "이 영역 등록", Padding = new Thickness(14, 6, 14, 6), Style = (Style)FindResource("Primary") }; buttons.Children.Add(save);
        _canvas.MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _canvas.CaptureMouse(); Draw(_start.Value); };
        _canvas.MouseMove += (_, e) => { if (_start != null && e.LeftButton == MouseButtonState.Pressed) Draw(e.GetPosition(_canvas)); };
        _canvas.MouseLeftButtonUp += (_, _) => { _start = null; _canvas.ReleaseMouseCapture(); };
        save.Click += (_, _) =>
        {
            int x = Math.Max(0, (int)(Canvas.GetLeft(_selection) / _scale)), y = Math.Max(0, (int)(Canvas.GetTop(_selection) / _scale));
            int w = Math.Min(bitmap.Width - x, (int)(_selection.Width / _scale)), h = Math.Min(bitmap.Height - y, (int)(_selection.Height / _scale));
            if (w < 16 || h < 16 || w > 800 || h > 500) { _status.Text = "16×16 이상, 800×500 이하 영역을 선택하세요."; return; }
            try { Result = Vision.Crop(bitmap, new Rectangle(x, y, w, h)); DialogResult = true; }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
    }
    private void Draw(WPoint p)
    {
        if (_start == null) return;
        p.X = Math.Clamp(p.X, 0, _canvas.Width); p.Y = Math.Clamp(p.Y, 0, _canvas.Height);
        Canvas.SetLeft(_selection, Math.Min(p.X, _start.Value.X)); Canvas.SetTop(_selection, Math.Min(p.Y, _start.Value.Y));
        _selection.Width = Math.Abs(p.X - _start.Value.X); _selection.Height = Math.Abs(p.Y - _start.Value.Y);
        _status.Text = $"선택 영역  {(int)(_selection.Width / _scale)} × {(int)(_selection.Height / _scale)} px";
    }
}

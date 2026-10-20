using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TeyvatRoutine;
public sealed class Recorder : IDisposable
{
    private readonly Native.HookProc _keyboard, _mouse;
    private IntPtr _kh, _mh, _sink;
    private GameWindow? _game;
    private readonly Stopwatch _clock = new();
    private RecordingBuffer _buffer = new();
    private System.Drawing.Rectangle _bounds;
    private long _lastMove;
    private long _lastModeCheck;
    private bool _automatic, _manualRelative, _relative;
    private int _rawPackets, _rawAbsolute, _rawErrors, _outsideMenu, _modeChanges;
    public string Diagnostics => $"마우스 진단 · Raw {_rawPackets}개 / 절대형 Raw {_rawAbsolute}개 / Raw 오류 {_rawErrors}개 / 메뉴 영역 밖 {_outsideMenu}개 / 모드 변경 {_modeChanges}회 · {(_automatic ? "자동 감지" : "수동 모드")}";
    public bool Active { get; private set; }
    public bool Relative
    {
        get
        {
            if (Active && _automatic && _clock.ElapsedMilliseconds - _lastModeCheck >= 20)
            {
                bool next = MouseRouting.RelativeMode(true, Native.CursorVisible(), _manualRelative);
                if (next != _relative) _modeChanges++;
                _relative = next; _lastModeCheck = _clock.ElapsedMilliseconds;
            }
            return _relative;
        }
    }
    public int Count => _buffer.Events.Count;
    public bool Paused => _buffer.Paused;
    public string PauseReason => _buffer.PauseReason;
    public long ElapsedMs => Paused ? _buffer.Duration : _clock.ElapsedMilliseconds;
    public System.Drawing.Point Origin => _bounds.Location;
    public Recorder() { _keyboard = Keyboard; _mouse = Mouse; }
    public void Start(GameWindow game, IntPtr sink, bool relative, bool automatic = false)
    {
        if (Active) throw new InvalidOperationException("이미 녹화 중입니다.");
        Native.Ensure(game); _game = game; _sink = sink; _bounds = Native.Bounds(game.Handle);
        _buffer = new(); _lastMove = -100;
        _automatic = automatic; _manualRelative = relative;
        _relative = MouseRouting.RelativeMode(automatic, Native.CursorVisible(), relative); _lastModeCheck = -100;
        _rawPackets = _rawAbsolute = _rawErrors = _outsideMenu = _modeChanges = 0;
        var module = Native.GetModuleHandle(null);
        _kh = Native.SetWindowsHookEx(13, _keyboard, module, 0);
        int keyError = _kh == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        _mh = Native.SetWindowsHookEx(14, _mouse, module, 0);
        int mouseError = _mh == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        var raw = new Native.RawDevice { Page = 1, Usage = 2, Flags = 0x100, Target = sink };
        bool rawOk = Native.RegisterRawInputDevices(new[] { raw }, 1, (uint)Marshal.SizeOf<Native.RawDevice>());
        int rawError = rawOk ? 0 : Marshal.GetLastWin32Error();
        if (_kh == IntPtr.Zero || _mh == IntPtr.Zero || !rawOk)
        { Dispose(); throw new InvalidOperationException($"입력 녹화 등록 실패: 키보드={keyError}, 마우스={mouseError}, RawInput={rawError}. 게임과 프로그램의 실행 권한을 확인하세요."); }
        Active = true; _clock.Restart();
    }
    public void ToggleMode() { if (Active && !Paused) { _manualRelative = !Relative; _relative = _manualRelative; _automatic = false; _modeChanges++; } }
    public void Pause(string reason)
    {
        if (!Active || Paused) return;
        _buffer.Pause(_clock.ElapsedMilliseconds, reason); _clock.Stop();
    }
    private bool Accept
    {
        get
        {
            if (!Active || Paused || _game == null) return false;
            if (Native.GetForegroundWindow() != _game.Handle) { Pause("원신 밖으로 창 전환"); return false; }
            if (Count >= 200000 || ElapsedMs >= 600000) { Pause("녹화 길이 제한 도달"); return false; }
            return true;
        }
    }
    private void Add(InputKind kind, int code = 0, double x = 0, double y = 0, bool extended = false)
        => _buffer.Add(_clock.ElapsedMilliseconds, kind, code, x, y, extended);
    private IntPtr Keyboard(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0 && Accept)
        {
            var k = Marshal.PtrToStructure<Native.KeyboardHook>(l);
            int vk = (int)k.Vk;
            if ((k.Flags & 0x10) == 0)
            {
                bool down = w.ToInt32() is 0x100 or 0x104;
                bool up = w.ToInt32() is 0x101 or 0x105;
                bool ext = (k.Flags & 1) != 0;
                _buffer.Key(_clock.ElapsedMilliseconds, vk, down, up, ext, (k.Flags & 0x20) != 0);
                if (Paused) _clock.Stop();
            }
        }
        return Native.CallNextHookEx(_kh, n, w, l);
    }
    private void Position(Native.Point p)
    {
        if (!_bounds.Contains(p.X, p.Y)) { _outsideMenu++; return; }
        Add(InputKind.MoveAbsolute, x: (p.X - _bounds.Left) / (double)(_bounds.Width - 1), y: (p.Y - _bounds.Top) / (double)(_bounds.Height - 1));
        _lastMove = _clock.ElapsedMilliseconds;
    }
    private IntPtr Mouse(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0 && Accept)
        {
            var m = Marshal.PtrToStructure<Native.MouseHook>(l);
            if ((m.Flags & 1) == 0)
            {
                int message = w.ToInt32();
                if (message == 0x200 && !Relative && _clock.ElapsedMilliseconds - _lastMove >= 12) Position(m.Point);
                int button = message switch { 0x201 or 0x202 => 0, 0x204 or 0x205 => 1, 0x207 or 0x208 => 2, _ => -1 };
                if (button >= 0)
                {
                    bool down = message is 0x201 or 0x204 or 0x207;
                    if (down && MouseRouting.PauseForClick(Relative, _bounds.Contains(m.Point.X, m.Point.Y), Relative || Native.PointBelongsToGame(_game!, m.Point)))
                    { Pause("프로그램 또는 다른 창 클릭"); return Native.CallNextHookEx(_mh, n, w, l); }
                    if (!Relative && !_bounds.Contains(m.Point.X, m.Point.Y)) return Native.CallNextHookEx(_mh, n, w, l);
                    if (!Relative) Position(m.Point);
                    _buffer.Button(_clock.ElapsedMilliseconds, button, down);
                }
                if (message == 0x20A) { if (!Relative) Position(m.Point); Add(InputKind.Wheel, unchecked((short)(m.MouseData >> 16))); }
            }
        }
        return Native.CallNextHookEx(_mh, n, w, l);
    }
    public void RawInput(IntPtr handle)
    {
        if (!Accept || !Relative) return;
        uint size = 0; uint header = (uint)Marshal.SizeOf<Native.RawHeader>();
        if (Native.GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, header) == uint.MaxValue || size > 4096 || size < header + 24) { _rawErrors++; return; }
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(handle, 0x10000003, ptr, ref size, header) != size) { _rawErrors++; return; }
            var h = Marshal.PtrToStructure<Native.RawHeader>(ptr); if (h.Type != 0) return;
            var mouse = Marshal.PtrToStructure<Native.RawMouse>(IntPtr.Add(ptr, (int)header));
            _rawPackets++;
            if ((mouse.Flags & 1) != 0) { _rawAbsolute++; return; }
            if (mouse.X == 0 && mouse.Y == 0) return;
            _buffer.RelativeMove(_clock.ElapsedMilliseconds, mouse.X, mouse.Y);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public (List<RecordedInput> Inputs, long Duration) Stop()
    {
        Pause("사용자가 녹화 종료");
        long duration = _buffer.Duration;
        Dispose();
        return (_buffer.Events.ToList(), duration);
    }
    public void Dispose()
    {
        Active = false; _clock.Stop();
        if (_kh != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kh); _kh = IntPtr.Zero; }
        if (_mh != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mh); _mh = IntPtr.Zero; }
        if (_sink != IntPtr.Zero) { Native.RegisterRawInputDevices(new[] { new Native.RawDevice { Page = 1, Usage = 2, Flags = 1, Target = IntPtr.Zero } }, 1, (uint)Marshal.SizeOf<Native.RawDevice>()); _sink = IntPtr.Zero; }
    }
}

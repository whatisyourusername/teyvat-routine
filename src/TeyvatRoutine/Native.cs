using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace TeyvatRoutine;
public sealed record GameWindow(IntPtr Handle, int ProcessId, long StartTicks, string Title)
{
    public override string ToString() => $"{Title}  ·  PID {ProcessId}";
}
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CursorInfo { public uint Size, Flags; public IntPtr Cursor; public Point Position; }
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CursorInfo info);
    public static bool? CursorVisible()
    { var info = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() }; return GetCursorInfo(ref info) ? (info.Flags & 1) != 0 : null; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyboardHook { public uint Vk, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseHook { public Point Point; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] public struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct RawMouse { [FieldOffset(0)] public ushort Flags; [FieldOffset(4)] public uint Buttons; [FieldOffset(12)] public int X; [FieldOffset(16)] public int Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc fn, IntPtr data);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref Point point);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    public static bool PointBelongsToGame(GameWindow game, Point point)
        => GetAncestor(WindowFromPoint(point), 2) == game.Handle;
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int size, out int returned);
    public static string Elevation(int processId)
    {
        IntPtr process = OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) return "확인 불가 (오류 " + Marshal.GetLastWin32Error() + ")";
        try
        {
            if (!OpenProcessToken(process, 0x8, out var token)) return "확인 불가 (오류 " + Marshal.GetLastWin32Error() + ")";
            try { return GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) ? (elevated != 0 ? "관리자" : "일반") : "확인 불가 (오류 " + Marshal.GetLastWin32Error() + ")"; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    public static List<GameWindow> FindGames()
    {
        var result = new List<GameWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint id);
            try
            {
                using var p = Process.GetProcessById((int)id);
                if (!new[] { "GenshinImpact", "YuanShen" }.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) return true;
                var title = new StringBuilder(512); GetWindowText(h, title, title.Capacity);
                var rect = Bounds(h); if (rect.Width < 320 || rect.Height < 240) return true;
                long ticks; try { ticks = p.StartTime.Ticks; } catch { ticks = 0; }
                result.Add(new(h, (int)id, ticks, title.ToString()));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static Rectangle Bounds(IntPtr h)
    {
        if (!GetClientRect(h, out var r)) throw new InvalidOperationException("게임 창 크기를 읽지 못했습니다.");
        var p = new Point();
        if (!ClientToScreen(h, ref p)) throw new InvalidOperationException("게임 화면 위치를 읽지 못했습니다.");
        return new(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    }
    public static bool Exists(GameWindow game)
    {
        if (!IsWindow(game.Handle)) return false;
        GetWindowThreadProcessId(game.Handle, out uint id);
        if (id != game.ProcessId) return false;
        try { using var p = Process.GetProcessById(game.ProcessId); return !p.HasExited && (game.StartTicks == 0 || p.StartTime.Ticks == game.StartTicks); }
        catch (Win32Exception) { return true; } catch { return false; }
    }
    public static void Ensure(GameWindow game, int width = 0, int height = 0)
    {
        if (!Exists(game)) throw new InvalidOperationException("원신 창이 닫혔거나 변경되었습니다.");
        if (IsIconic(game.Handle) || GetForegroundWindow() != game.Handle) throw new InvalidOperationException("원신이 앞쪽 창이 아닙니다. 입력을 중단했습니다.");
        var r = Bounds(game.Handle);
        if (width > 0 && (r.Width != width || r.Height != height)) throw new InvalidOperationException($"해상도가 달라졌습니다. 녹화 {width}×{height}, 현재 {r.Width}×{r.Height}.");
    }
    public static Bitmap Capture(GameWindow game)
    {
        Ensure(game);
        var r = Bounds(game.Handle);
        var b = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        try { using var g = Graphics.FromImage(b); g.CopyFromScreen(r.Location, System.Drawing.Point.Empty, r.Size, CopyPixelOperation.SourceCopy); return b; }
        catch { b.Dispose(); throw; }
    }
    public static void Activate(GameWindow game)
    {
        if (IsIconic(game.Handle)) ShowWindow(game.Handle, 9);
        SetForegroundWindow(game.Handle);
    }
    public static Input Key(int vk, bool down, bool extended = false)
    {
        uint scan = MapVirtualKey((uint)vk, 0);
        return new() { Type = 1, Data = new() { Key = new() { Vk = scan == 0 ? (ushort)vk : (ushort)0, Scan = (ushort)scan, Flags = (scan == 0 ? 0u : 0x8u) | (down ? 0u : 2u) | (extended ? 1u : 0u) } } };
    }
    public static Input Mouse(uint flags, int x = 0, int y = 0, int data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Dx = x, Dy = y, MouseData = unchecked((uint)data), Flags = flags } } };
    public static Input Absolute(Rectangle r, double x, double y)
    {
        var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77); var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
        int px = r.Left + (int)Math.Round(x * (r.Width - 1)); int py = r.Top + (int)Math.Round(y * (r.Height - 1));
        return Mouse(0x0001 | 0x8000 | 0x4000, (int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1)), (int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1)));
    }
    public static uint ButtonFlag(int button, bool down) => button switch { 0 => down ? 2u : 4u, 1 => down ? 8u : 16u, 2 => down ? 32u : 64u, _ => throw new InvalidDataException("마우스 버튼 오류") };
    public static void Send(Input i)
    {
        if (SendInput(1, new[] { i }, Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Windows가 입력을 전달하지 못했습니다. 게임과 프로그램의 실행 권한을 확인하세요. 오류 " + Marshal.GetLastWin32Error());
    }
}

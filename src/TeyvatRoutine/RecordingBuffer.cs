namespace TeyvatRoutine;

// Pure recording state, shared by native recording and regression tests.
public sealed class RecordingBuffer
{
    private readonly Dictionary<int, bool> _keys = new();
    private readonly HashSet<int> _buttons = new();
    private readonly HashSet<int> _shortcutKeys = new();
    public List<RecordedInput> Events { get; } = new();
    public bool Paused { get; private set; }
    public long Duration { get; private set; }
    public string PauseReason { get; private set; } = "";
    public void Add(long time, InputKind kind, int code = 0, double x = 0, double y = 0, bool extended = false)
    {
        if (!Paused) Events.Add(new() { AtMs = time, Kind = kind, Code = code, X = x, Y = y, Extended = extended });
    }
    public void Key(long time, int key, bool down, bool up, bool extended, bool altDown)
    {
        if (Paused) return;
        // Do not replay the shortcut used to switch back to the recorder.
        if (down && (key is 0x5B or 0x5C || (altDown && (key is 0x09 or 0x1B))))
        { Pause(time, "창 전환 단축키"); return; }
        // Keep recorder commands out of playback; ordinary F1-F24 and Esc are game input.
        if (_shortcutKeys.Contains(key)) { if (up) _shortcutKeys.Remove(key); return; }
        bool control = _keys.ContainsKey(0x11) || _keys.ContainsKey(0xA2) || _keys.ContainsKey(0xA3);
        bool alt = altDown || _keys.ContainsKey(0x12) || _keys.ContainsKey(0xA4) || _keys.ContainsKey(0xA5);
        if (down && !_keys.ContainsKey(key) && InputRules.IsRecorderShortcut(key, control, alt))
        { _shortcutKeys.Add(key); return; }
        if (!InputRules.AllowedKey(key)) return;
        if (down && _keys.TryAdd(key, extended)) Add(time, InputKind.KeyDown, key, extended: extended);
        if (up && _keys.Remove(key)) Add(time, InputKind.KeyUp, key, extended: extended);
    }
    public void Button(long time, int button, bool down)
    {
        if (Paused) return;
        if (down && _buttons.Add(button)) Add(time, InputKind.MouseDown, button);
        if (!down && _buttons.Remove(button)) Add(time, InputKind.MouseUp, button);
    }
    public void RelativeMove(long time, int x, int y)
    {
        if (Paused) return;
        if (Events.Count > 0 && Events[^1].Kind == InputKind.MoveRelative && time - Events[^1].AtMs < 8)
        { Events[^1].X += x; Events[^1].Y += y; }
        else Add(time, InputKind.MoveRelative, x: x, y: y);
    }
    public void Pause(long time, string reason)
    {
        if (Paused) return;
        Duration = Math.Max(Math.Max(1, time), Events.Count == 0 ? 0 : Events[^1].AtMs);
        // Balance the saved sequence only; no key or mouse input is sent to Windows.
        foreach (var key in _keys) Add(Duration, InputKind.KeyUp, key.Key, extended: key.Value);
        foreach (var button in _buttons) Add(Duration, InputKind.MouseUp, button);
        _keys.Clear(); _buttons.Clear(); _shortcutKeys.Clear(); PauseReason = reason; Paused = true;
    }
}

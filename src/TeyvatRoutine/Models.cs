using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeyvatRoutine;
public enum InputKind { KeyDown, KeyUp, MoveAbsolute, MoveRelative, MouseDown, MouseUp, Wheel }
public sealed class RecordedInput
{
    public long AtMs { get; set; }
    public InputKind Kind { get; set; }
    public int Code { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool Extended { get; set; }
    [JsonIgnore] public string Display => $"{AtMs / 1000.0,7:0.000}s  {Kind,-14}  {(Kind is InputKind.KeyDown or InputKind.KeyUp ? System.Windows.Input.KeyInterop.KeyFromVirtualKey(Code).ToString() + " (" + Code + ")" : Code.ToString())}  {X:0.###}, {Y:0.###}";
}
public sealed class Checkpoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string PngBase64 { get; set; } = "";
    public double Threshold { get; set; } = .86;
    public int SearchRadius { get; set; } = 18;
    public bool GoldTextOnly { get; set; }
    public bool WhiteForegroundOnly { get; set; }
    public int WhiteBrightness { get; set; } = 175;
}
public sealed class Segment : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hint { get; set; } = "";
    [JsonIgnore] public string EditorLabel => Id switch { "battle" => "전투", "collect" => "보상 수령", "retry" => "재도전", _ => Name };
    public bool RelativeInitially { get; set; }
    public bool AutoMouseMode { get; set; } = true;
    public ViewAlignment Alignment { get; set; } = new();
    public int Width { get; set; }
    public int Height { get; set; }
    public long DurationMs { get; set; }
    public int TimeoutSeconds { get; set; } = 45;
    public List<RecordedInput> Inputs { get; set; } = new();
    public Checkpoint? Before { get; set; }
    public Checkpoint? After { get; set; }
    public bool StopOnCompletion { get; set; }
    public CombatLoop Combat { get; set; } = new();
    public TargetNavigation Navigation { get; set; } = new();
    [JsonIgnore] public List<RecordedInput> PlaybackInputs => Navigation.Enabled ? Navigation.ArrivalInputs : Inputs;
    [JsonIgnore] public long PlaybackDuration => Navigation.Enabled ? Navigation.ArrivalDurationMs : DurationMs;
    [JsonIgnore] public bool Ready => PlaybackInputs.Count > 0 && (Navigation.Enabled ? Navigation.Templates.Count > 0 && Navigation.Arrival != null : Before != null) && (After != null || Id == "quit") && Width > 0 && (!Alignment.Enabled || Alignment.Reference != null);
    [JsonIgnore] public string SetupLabel => Ready ? "등록 완료" : Alignment.Enabled && Alignment.Reference == null ? "시점 기준 등록 필요" : Navigation.Enabled && (Navigation.Templates.Count == 0 || Navigation.Arrival == null) ? "나무·도착 화면 등록 필요" : PlaybackInputs.Count == 0 ? (Navigation.Enabled ? "도착 후 조작 녹화 필요" : "녹화 필요") : "화면 등록 필요";
    [JsonIgnore] public string Detail => $"{PlaybackInputs.Count:N0}개 입력  ·  {PlaybackDuration / 1000.0:0.0}초" + (Navigation.Enabled ? " · 나무 도착 후 조작" : "");
    private string _status = "";
    [JsonIgnore] public string Status { get => string.IsNullOrEmpty(_status) ? SetupLabel : _status; set { _status = value; Changed(); } }
    public void ResetRegistration()
    {
        Inputs = new(); DurationMs = 0; Width = Height = 0;
        Combat = new();
        Before = After = null; Status = ""; Refresh();
        Navigation.Templates.Clear(); Navigation.Arrival = null; Navigation.ArrivalInputs.Clear(); Navigation.ArrivalDurationMs = 0; Alignment.Reference = null; Refresh();
    }
    public void StoreRecording(List<RecordedInput> inputs, long duration)
    {
        if (inputs.Count == 0) throw new InvalidOperationException("녹화된 입력이 없습니다. 원신 창에서 직접 키보드·마우스를 조작하세요.");
        // Screenshots may be registered before or after the input recording.
        if (Navigation.Enabled) { Navigation.ArrivalInputs = inputs.ToList(); Navigation.ArrivalDurationMs = duration; }
        else { Inputs = inputs.ToList(); DurationMs = duration; Combat = new(); }
        Status = ""; Refresh();
    }
    public void Refresh() { Changed(nameof(Ready)); Changed(nameof(SetupLabel)); Changed(nameof(Detail)); Changed(nameof(Status)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class Profile
{
    public ScreenFlowSettings ScreenFlow { get; set; } = new();
    public int Version { get; set; } = 3;
    public string Name { get; set; } = "스네즈나야 · 성유물 루틴";
    public int ResolutionWidth { get; set; } = 3840;
    public int ResolutionHeight { get; set; } = 2160;
    public bool IncludeLogin { get; set; } = true;
    public bool IncludeDomain { get; set; } = true;
    public bool IncludeCraft { get; set; } = true;
    public bool IncludeDaily { get; set; } = true;
    public bool QuitAfter { get; set; }
    public int DomainRuns { get; set; } = 3;
    // Null retains selections from older group-based profiles; an empty list selects nothing.
    public List<string>? SelectedSteps { get; set; }
    public bool IsStepSelected(string id) => id != "start" && (SelectedSteps != null ?
        (id is "battle" or "collect" or "retry" ? SelectedSteps.Any(s => s is "battle" or "collect" or "retry") : SelectedSteps.Contains(id)) : id switch
    {
        "login" => IncludeLogin,
        "warp_domain" or "enter_domain" or "battle" or "collect" or "retry" => IncludeDomain,
        "exit" => false,
        "warp_train" or "craft" => IncludeCraft,
        "daily" => IncludeDaily,
        "quit" => QuitAfter,
        _ => false
    });
    public List<Segment> Segments { get; set; } = new();
    // Retain the old 04 payload for profile compatibility, but never display or schedule it.
    [JsonIgnore] public IEnumerable<Segment> RoutineSegments => Segments.Where(s => s.Id != "start");
    public List<RoutineTask> CreateTasks() => new()
    {
        new(this, "login", Get("login").Name, "login"),
        new(this, "warp_domain", Get("warp_domain").Name, "warp_domain"),
        new(this, "enter_domain", Get("enter_domain").Name, "enter_domain"),
        new(this, "battle", "04  비경 반복", "battle", "collect", "retry"),
        new(this, "exit", Get("exit").Name, "exit"),
        new(this, "warp_train", Get("warp_train").Name, "warp_train"),
        new(this, "craft", Get("craft").Name, "craft"),
        new(this, "daily", Get("daily").Name, "daily"),
        new(this, "quit", Get("quit").Name, "quit")
    };
    public Segment Get(string id) => Segments.Single(s => s.Id == id);
    public static Profile Create() => new() { Segments = new()
    {
        New("login", "01  게임 진입", "시작: 게임 진입 화면. 빈 공간을 눌러 로딩을 기다립니다. 완료: 캐릭터 조작 화면. 계정·비밀번호 입력은 직접 마친 상태여야 합니다."),
        New("warp_domain", "02  성유물 비경 워프", "시작: 필드. M → 지역 선택/지도 이동 → 원하는 비경 → 우측 아래 워프. 로딩이 끝난 뒤 녹화를 종료합니다. 지도는 매번 같은 축척·상태에서 시작하세요."),
        New("enter_domain", "03  비경 입구 진입", "시작: 비경 앞의 일정한 위치. 앞으로 이동 → F → 난이도·파티 확인 → 입장 → 비경 내부 초기 위치까지 녹화합니다. 완료 후 04 비경 반복의 전투로 바로 이어집니다.", true),
        New("start", "04  도전 시작", "시작: 비경 선택 화면. 난이도와 파티를 확인하고 도전을 시작합니다. 완료: 비경 내부의 초기 위치. 반복 도전 뒤에도 같은 위치·시점이어야 합니다."),
        New("battle", "04 · 전투", "시작: 비경 내부 초기 위치. 도전 장치로 이동하여 시작한 뒤 전투합니다. 완료: 도전 성공. 이동/전투 카메라 모드로 녹화하고, 모든 키를 놓고 Alt+Tab 후 녹화 종료 · 저장 버튼으로 끝내세요.", true),
        New("collect", "04 · 보상 수령", "시작: 전투 성공. 보상 나무로 이동 → F → Ctrl+Alt+Home으로 메뉴 모드 전환 → 농축레진 사용 → 아이템 보상 화면. 완료 확인에는 보상 아이템/결과 화면의 고유 부분을 선택하세요.", true),
        New("retry", "04 · 재도전", "시작: 아이템 보상 화면. 다시 도전 → 로딩 완료까지. 완료: 03 구간이 끝난 것과 같은 비경 초기 위치·시점. 마지막 회차에는 생략하고, 나머지 회차에서는 다음 전투로 이어집니다."),
        New("exit", "05  비경 나가기 · 선택", "06번이 보상 화면에서 직접 이동할 수 있으면 체크를 끕니다. 별도 탈출이 필요한 경우에만 마지막 회차 보상 화면 → 비경 나가기 → 필드 로딩 완료까지 녹화해 선택하세요."),
        New("warp_train", "06  몬드 워프포인트", "시작: 등록한 마지막 보상 화면 또는 필드. 비경 나가기 → M → 몬드 워프포인트 → 워프 → 로딩 완료. 기존에 녹화한 이동을 사용합니다."),
        New("craft", "07  농축레진 3개 합성", "시작: 몬드 워프 후 위치. 합성대로 이동/F → 메뉴 모드(Ctrl+Alt+Home) → 농축레진 선택 → 수량 3 확인 → 합성 → 결과 확인 → 메뉴 닫기. 수량과 재료는 녹화 전에 확인하세요.", true),
        New("daily", "08  일일 보상 수령", "시작: 합성 후 필드. 일일 보상을 받는 전체 동선을 녹화합니다. 수령 가능한 상태에서 진행하고, 완료 확인에는 수령 완료를 판별하는 고유 표시를 등록하세요."),
        New("quit", "09  게임 종료 · 선택", "게임 메뉴를 통한 정상 종료 구간을 녹화합니다. 완료 화면 대신 게임 프로세스 종료를 최대 30초 확인합니다. 강제 종료는 사용하지 않습니다.")
    }};
    private static Segment New(string id, string name, string hint, bool relative = false) => new()
    { Id = id, Name = name, Hint = hint, RelativeInitially = relative, StopOnCompletion = id is "enter_domain" or "battle" or "collect", Navigation = new() { Enabled = id == "collect" } };
}
public sealed record PlanItem(Segment Segment, int Round = 0, bool CombatInProgress = false);
public static class Workflow
{
    public static List<PlanItem> Build(Profile p)
    {
        if (p.DomainRuns is < 1 or > 10) throw new InvalidDataException("전투 횟수는 1~10회여야 합니다.");
        var items = new List<PlanItem>();
        void Add(string id, int round = 0) { if (p.IsStepSelected(id)) items.Add(new(p.Get(id), round)); }
        foreach (var id in new[] { "login", "warp_domain", "enter_domain" }) Add(id);
        for (int round = 1; round <= p.DomainRuns; round++) { Add("battle", round); Add("collect", round); if (round < p.DomainRuns) Add("retry", round); }
        foreach (var id in new[] { "exit", "warp_train", "craft", "daily", "quit" }) Add(id);
        return items;
    }
    public static List<string> Validate(IEnumerable<PlanItem> plan, ScreenFlowSettings? flow = null)
    {
        var errors = new List<string>();
        var first = plan.FirstOrDefault()?.Segment;
        if (flow != null && first != null) errors.AddRange(flow.Validate(first.Width, first.Height, plan.Any(i => i.Segment.Id == "login")));
        foreach(var item in plan.Where(i=>i.CombatInProgress))
            if(item.Segment.Id!="battle" || !item.Segment.Combat.Enabled)errors.Add("전투 도중 재개에는 전투 반복 설정이 필요합니다.");
        int width = 0, height = 0;
        foreach (var s in plan.Select(p => p.Segment).Distinct())
        {
            bool screenLogin = s.Id == "login" && flow?.LoginEnabled == true;
            if (!screenLogin && s.PlaybackInputs.Count == 0) errors.Add($"{s.Name}: {(s.Navigation.Enabled ? "나무 도착 후 보상 조작" : "구간")} 녹화가 없습니다.");
            if (!s.Navigation.Enabled && s.Before == null) errors.Add($"{s.Name}: 시작 화면을 등록하세요.");
            if (s.Navigation.Enabled) errors.AddRange(s.Navigation.Validate().Select(e => s.Name + ": " + e));
            if (s.Combat == null) errors.Add(s.Name + ": 전투 반복 설정이 없습니다.");
            else errors.AddRange(s.Combat.Validate(s).Select(e => s.Name + ": " + e));
            if (s.Alignment.Enabled && s.Alignment.Reference == null) errors.Add($"{s.Name}: 시점 기준 이미지를 등록하세요.");
            if (s.After == null && s.Id != "quit") errors.Add($"{s.Name}: 완료 화면을 등록하세요.");
            if (s.Width < 320 || s.Height < 240) errors.Add($"{s.Name}: 해상도 정보가 없습니다.");
            if (width == 0) { width = s.Width; height = s.Height; }
            else if (width != s.Width || height != s.Height) errors.Add($"{s.Name}: 다른 구간과 녹화 해상도가 다릅니다.");
            if (!screenLogin && s.PlaybackDuration is < 1 or > 600000 || s.TimeoutSeconds is < 1 or > 180) errors.Add($"{s.Name}: 시간이 허용 범위를 벗어났습니다.");
            long last = -1;
            var heldKeys = new HashSet<int>(); var heldButtons = new HashSet<int>();
            foreach (var e in s.PlaybackInputs)
            {
                if (!Enum.IsDefined(typeof(InputKind), e.Kind) || e.AtMs < last || e.AtMs > s.PlaybackDuration || e.AtMs < 0) { errors.Add($"{s.Name}: 잘못된 입력 순서입니다."); break; }
                last = e.AtMs;
                if (!double.IsFinite(e.X) || !double.IsFinite(e.Y)) { errors.Add($"{s.Name}: 좌표가 유효하지 않습니다."); break; }
                if (e.Kind == InputKind.MoveAbsolute && (e.X < 0 || e.X > 1 || e.Y < 0 || e.Y > 1)) { errors.Add($"{s.Name}: 창 밖 좌표입니다."); break; }
                if (e.Kind is InputKind.KeyDown or InputKind.KeyUp)
                {
                    if (!InputRules.AllowedKey(e.Code)) { errors.Add($"{s.Name}: 허용되지 않은 키가 있습니다 ({e.Code})."); break; }
                    if (e.Kind == InputKind.KeyDown) heldKeys.Add(e.Code);
                    else if (!heldKeys.Remove(e.Code)) { errors.Add($"{s.Name}: 눌림 없는 키 해제입니다."); break; }
                }
                if (e.Kind is InputKind.MouseDown or InputKind.MouseUp)
                {
                    if (e.Code is < 0 or > 2) { errors.Add($"{s.Name}: 지원하지 않는 마우스 버튼입니다."); break; }
                    if (e.Kind == InputKind.MouseDown) heldButtons.Add(e.Code);
                    else if (!heldButtons.Remove(e.Code)) { errors.Add($"{s.Name}: 눌림 없는 버튼 해제입니다."); break; }
                }
                if (e.Kind == InputKind.MoveRelative && (Math.Abs(e.X) > 10000 || Math.Abs(e.Y) > 10000)) { errors.Add($"{s.Name}: 과도한 마우스 이동입니다."); break; }
                if (e.Kind == InputKind.Wheel && Math.Abs((long)e.Code) > 12000) { errors.Add($"{s.Name}: 잘못된 스크롤 값입니다."); break; }
            }
            if (heldKeys.Count + heldButtons.Count > 0) errors.Add($"{s.Name}: 해제되지 않은 입력이 있습니다.");
            foreach (var c in new[] { s.Before, s.After, s.Navigation.Enabled ? s.Navigation.Arrival : null, s.Alignment.Enabled ? s.Alignment.Reference : null,
                s.Combat?.Enabled==true && s.Combat.SkillTrigger?.Enabled==true?s.Combat.SkillTrigger.ReadyIcon:null,
                s.Combat?.Enabled==true && s.Combat.SkillTrigger?.Enabled==true?s.Combat.SkillTrigger.CooldownArea:null,
                s.Combat?.Enabled==true && s.Combat.WaitForThirdPartyReady?s.Combat.ThirdPartyReady:null,
                s.Combat?.InProgress }.Concat(s.Navigation.Enabled ? s.Navigation.Templates : new List<Checkpoint>())
                .Concat(s.Combat?.Enabled==true && s.Combat.FourthPartySwitch?.Enabled==true ? s.Combat.FourthPartySwitch.Keycaps ?? new() : new List<Checkpoint?>()).Where(c => c != null))
            {
                if (c!.X < 0 || c.Y < 0 || c.Width < 16 || c.Height < 16 || c.X + (long)c.Width > s.Width || c.Y + (long)c.Height > s.Height || !double.IsFinite(c.Threshold) || c.Threshold is < .65 or > 1 || c.SearchRadius is < 0 or > 100)
                    errors.Add($"{s.Name}: 화면 확인 영역이 유효하지 않습니다.");
                if(c.WhiteBrightness is < 0 or > 255)errors.Add($"{s.Name}: 흰색 글자 밝기 기준이 유효하지 않습니다.");
                try { Vision.Validate(c); } catch { errors.Add($"{s.Name}: 화면 이미지가 유효하지 않습니다."); }
            }
        }
        return errors.Distinct().ToList();
    }
}
public static class InputRules
{
    // Accept keyboard virtual keys broadly, including function, OEM and navigation keys.
    // Windows keys switch away from the game and are handled as a recording pause.
    public static bool AllowedKey(int k) => k == 0x03 || (k is >= 0x08 and <= 0xFE && k is not (0x5B or 0x5C));
    public const uint RecorderModifiers = 0x4003; // MOD_NOREPEAT | MOD_CONTROL | MOD_ALT
    public const int ModeKey = 0x24; // Ctrl+Alt+Home
    public const int StopKey = 0x23; // Ctrl+Alt+End
    public static bool IsRecorderShortcut(int key, bool control, bool alt) => control && alt && key is ModeKey or StopKey;
}
public static class ProfileStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Root => Path.Combine(AppContext.BaseDirectory, "data");
    public static string DefaultPath => Path.Combine(Root, "profile.json");
    public static string BackupBeforeReset(Profile p, string segmentId)
    {
        p.Get(segmentId);
        string path = Path.Combine(Root, "backups", $"before-reset-{segmentId}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        Save(p, path);
        return path;
    }
    public static string ResolutionPath(int width, int height)
    {
        if (width is < 640 or > 16384 || height is < 360 or > 8640) throw new InvalidDataException("해상도는 가로 640~16384, 세로 360~8640 범위여야 합니다.");
        return Path.Combine(Root, "profiles", $"{width}x{height}.json");
    }
    public static Profile Load(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("프로필이 32MB를 초과합니다.");
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("프로필이 비어 있습니다.");
        var baseline = Profile.Create();
        if (p.DomainRuns is < 1 or > 10) throw new InvalidDataException("전투 횟수는 1~10회여야 합니다.");
        if (p.SelectedSteps != null && (p.SelectedSteps.Count > baseline.Segments.Count || p.SelectedSteps.Distinct().Count() != p.SelectedSteps.Count || p.SelectedSteps.Any(id => !baseline.Segments.Any(s => s.Id == id))))
            throw new InvalidDataException("실행 스텝 선택 목록이 유효하지 않습니다.");
        ResolutionPath(p.ResolutionWidth, p.ResolutionHeight);
        if (p.ScreenFlow == null || p.ScreenFlow.Validate(p.ResolutionWidth, p.ResolutionHeight, false).Count > 0)
            throw new InvalidDataException("로그인·로딩 설정이 유효하지 않습니다.");
        if (p.Version is not (1 or 2 or 3) || p.Segments == null || p.Segments.Count != baseline.Segments.Count || p.Segments.Any(s => s == null || s.Inputs == null || s.Inputs.Count > 200000 || s.Inputs.Any(i => i == null) || s.Navigation == null || s.Navigation.Templates == null || s.Navigation.Templates.Count > 5 || s.Navigation.Templates.Any(t => t == null) || s.Navigation.ArrivalInputs == null || s.Navigation.ArrivalInputs.Count > 200000 || s.Navigation.ArrivalInputs.Any(i => i == null)) || !p.Segments.Select(s => s.Id).OrderBy(x => x).SequenceEqual(baseline.Segments.Select(s => s.Id).OrderBy(x => x)))
            throw new InvalidDataException("지원하지 않는 프로필 구조입니다.");
        if (p.Version == 1)
        {
            foreach (var id in new[] { "enter_domain", "battle", "collect" }) p.Get(id).StopOnCompletion = true;
            p.Get("collect").Navigation.Enabled = true; p.Version = 2;
        }
        if (p.Segments.Any(s => s.Alignment == null || s.Combat == null || s.Combat.SkillTrigger == null)) throw new InvalidDataException("시점·전투 반복 설정이 유효하지 않습니다.");
        if (p.Version < 3)
        {
            foreach (var s in p.Segments)
            {
                foreach (var c in new[] { s.Before, s.After, s.Navigation.Arrival }.Where(c => c != null))
                { if (c!.Threshold == .91) c.Threshold = .86; if (c.SearchRadius == 8) c.SearchRadius = 18; }
                if (s.Navigation.MatchThreshold == .84) s.Navigation.MatchThreshold = .78;
                if (s.Navigation.AmbiguityMargin == .05) s.Navigation.AmbiguityMargin = .03;
            }
            p.Version = 3;
        }
        foreach (var s in p.Segments) { s.Name = baseline.Get(s.Id).Name; s.Hint = baseline.Get(s.Id).Hint; }
        if (p.Segments.Any(s => s.Width != 0 && (s.Width != p.ResolutionWidth || s.Height != p.ResolutionHeight))) throw new InvalidDataException("프로필과 녹화 구간의 해상도가 다릅니다.");
        return p;
    }
    public static void Save(Profile p, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(p, Options));
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temp, path, true);
    }
}





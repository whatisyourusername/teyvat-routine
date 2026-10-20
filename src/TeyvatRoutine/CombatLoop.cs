using System.IO;
using System.Text.Json.Serialization;

namespace TeyvatRoutine;
public enum CombatRotation { Recorded, SupportOrder2341 }
public sealed class CombatLoop
{
    public bool Enabled { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public long PeriodMs { get; set; } = 27000;
    public int MaxCycles { get; set; } = 6;
    public int TimeoutSeconds { get; set; } = 180;
    public bool RecenterBetweenCycles { get; set; } = true;
    public CombatRotation Rotation { get; set; }
    public int ThirdSkillHoldMs { get; set; } = 100;
    public int LandingWaitMs { get; set; } = 400;
    public int SecondSkillWaitMs { get; set; } = 500;
    public int ThirdSwitchWaitMs { get; set; } = 500;
    public bool WaitForThirdPartyReady { get; set; }
    public Checkpoint? ThirdPartyReady { get; set; }
    public PartySlotSettings FourthPartySwitch { get; set; } = new();
    public SkillTrigger SkillTrigger { get; set; } = new();
    public Checkpoint? InProgress { get; set; }
    [JsonIgnore] public long SecondSkillAt => 400L + SecondSkillWaitMs;
    [JsonIgnore] public long ThirdSlotAt => SecondSkillAt + 100 + ThirdSwitchWaitMs;
    [JsonIgnore] public long PlungeAt => ThirdSlotAt + 350 + ThirdSkillHoldMs + 500;
    [JsonIgnore] public long FourthSlotAt => PlungeAt + 80 + LandingWaitMs;
    // Timings describe attempted inputs, not observed skill cooldowns or energy.
    public static bool CombatKey(int code) => code is >= 0x31 and <= 0x34 or 0x45 or 0x51 or 0x57 or 0x41 or 0x53 or 0x44 or 0x52 or 0x20 or 0x10 or 0xA0 or 0xA1;
    public static CombatLoop Suggest(Segment s)
    {
        var first = s.Inputs.FirstOrDefault(i => i.Kind == InputKind.KeyDown && i.Code is >= 0x31 and <= 0x34)
            ?? throw new InvalidDataException("1~4번 캐릭터 교체 입력이 없습니다. 반복 시작·끝을 직접 지정하세요.");
        var last = s.Inputs.LastOrDefault(i => i.Kind == InputKind.MouseUp || i.Kind == InputKind.KeyUp && CombatKey(i.Code))
            ?? throw new InvalidDataException("전투 입력의 끝을 찾지 못했습니다.");
        var result = new CombatLoop { StartMs = first.AtMs, EndMs = last.AtMs,
            PeriodMs = (long)Math.Ceiling(s.DurationMs / 1000.0) * 1000 };
        var errors = result.Validate(s, true);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        return result;
    }
    public List<RecordedInput> Cycle(Segment s) => s.Inputs.Where(i => i.AtMs >= StartMs && i.AtMs <= EndMs).ToList();
    public List<RecordedInput> ExecutionCycle(Segment s)
    {
        RecordedInput Copy(RecordedInput i,long at)=>new(){AtMs=at,Kind=i.Kind,Code=i.Code,X=i.X,Y=i.Y,Extended=i.Extended};
        if(Rotation==CombatRotation.Recorded) return Cycle(s).Select(i=>Copy(i,i.AtMs-StartMs)).ToList();
        var main=Cycle(s).FirstOrDefault(i=>i.Kind==InputKind.KeyDown && i.Code==0x31)
            ?? throw new InvalidDataException("시험 순서에는 1번 캐릭터 딜링 녹화가 필요합니다.");
        var tail=Cycle(s).Where(i=>i.AtMs>=main.AtMs).ToList();
        if(tail.Any(i=>i.Kind==InputKind.KeyDown && i.Code is >=0x32 and <=0x34) || !Balanced(tail))
            throw new InvalidDataException("시험 순서는 마지막 1번 캐릭터 교체부터 딜링을 가져옵니다. 이후에 다른 캐릭터로 교체하지 않는 구간을 선택하세요.");
        var result=new List<RecordedInput>();
        void Key(long at,int code,long hold=100){result.Add(new(){AtMs=at,Kind=InputKind.KeyDown,Code=code});result.Add(new(){AtMs=at+hold,Kind=InputKind.KeyUp,Code=code});}
        Key(0,0x32);Key(300,0x45);Key(SecondSkillAt,0x45);
        Key(ThirdSlotAt,0x33);Key(ThirdSlotAt+350,0x45,ThirdSkillHoldMs);
        long plunge=PlungeAt;
        result.Add(new(){AtMs=plunge,Kind=InputKind.MouseDown,Code=0});result.Add(new(){AtMs=plunge+80,Kind=InputKind.MouseUp,Code=0});
        long fourth=FourthSlotAt;
        Key(fourth,0x34);Key(fourth+300,0x45);
        long mainStart=fourth+1200;
        result.AddRange(tail.Select(i=>Copy(i,mainStart+i.AtMs-main.AtMs)));
        return result;
    }
    public List<RecordedInput> Opening(Segment s)
    {
        var result=s.Inputs.Where(i=>i.AtMs<StartMs).ToList();
        result.AddRange(ExecutionCycle(s).Select(i=>new RecordedInput{AtMs=i.AtMs+StartMs,Kind=i.Kind,Code=i.Code,X=i.X,Y=i.Y,Extended=i.Extended}));
        return result;
    }
    public List<string> Validate(Segment s, bool force = false)
    {
        var errors = new List<string>();
        if (!Enabled && !force) return errors;
        if (s.Id != "battle" || s.Navigation.Enabled) errors.Add("전투 반복은 전투 구간에서만 사용할 수 있습니다.");
        if (StartMs < 0 || EndMs <= StartMs || EndMs > s.DurationMs || PeriodMs < EndMs - StartMs || PeriodMs is < 1 or > 120000 || MaxCycles is < 1 or > 20 || TimeoutSeconds is < 1 or > 600)
            errors.Add("반복 시작·끝·주기를 확인하세요. 주기는 전투 구간 길이 이상, 최대 120초 / 사이클 1~20회 / 제한 1~600초입니다.");
        var cycle = Cycle(s);
        if (cycle.Count == 0 || cycle[0].Kind != InputKind.KeyDown || cycle[0].Code is < 0x31 or > 0x34)
            errors.Add("반복 시작은 1~4번 캐릭터 교체 키를 누르는 시점이어야 합니다.");
        if (cycle.Any(i => i.Kind == InputKind.MoveAbsolute || i.Kind == InputKind.Wheel || (i.Kind is InputKind.KeyDown or InputKind.KeyUp) && !CombatKey(i.Code)))
            errors.Add("반복 구간에 메뉴·상호작용 입력이 있습니다. F·Alt·메뉴 조작이 없는 전투 부분만 지정하세요.");
        if (!cycle.Any(i => i.Kind == InputKind.MouseDown && i.Code == 0 || i.Kind == InputKind.KeyDown && i.Code is 0x45 or 0x51))
            errors.Add("반복 구간에 공격 입력이 없습니다.");
        if (!Balanced(s.Inputs.Where(i => i.AtMs <= EndMs)) || !Balanced(cycle))
            errors.Add("반복 경계에서 키나 마우스 버튼이 눌려 있습니다. 키를 모두 놓은 경계로 지정하세요.");
        if(!Enum.IsDefined(typeof(CombatRotation),Rotation) || ThirdSkillHoldMs is <50 or >2000 || LandingWaitMs is <200 or >4000 || SecondSkillWaitMs is <100 or >2000 || ThirdSwitchWaitMs is <500 or >3000)
            errors.Add("시험 순서: E 간격 100~2000ms, 2→3 대기 500~3000ms, 3번 E 50~2000ms, 낙공 입력 후 대기 200~4000ms 범위여야 합니다.");
        if(WaitForThirdPartyReady && (Rotation!=CombatRotation.SupportOrder2341 || ThirdPartyReady==null))errors.Add("파티창 전환 대기는 시험 순서에서 3번 캐릭터의 밝은 이름을 등록해야 합니다.");
        if(FourthPartySwitch==null || FourthPartySwitch.Enabled && (Rotation!=CombatRotation.SupportOrder2341 || FourthPartySwitch.Keycaps?.Count!=4 || FourthPartySwitch.Keycaps.Any(c=>c==null)))
            errors.Add("4번 교체 확인은 시험 순서에서 밝은 파티 번호 1~4를 모두 등록해야 합니다.");
        if(FourthPartySwitch?.Enabled==true && FourthPartySwitch.Keycaps?.Any(c=>c!=null && (c.Width is <16 or >80 || c.Height is <16 or >80 || c.SearchRadius is <0 or >3))==true)
            errors.Add("파티 번호는 가로·세로 16~80px, 탐색 반경 0~3px의 작은 영역으로 등록하세요.");
        if(SkillTrigger==null)errors.Add("스킬 인식 설정이 유효하지 않습니다.");
        else if(SkillTrigger.Enabled && (Rotation!=CombatRotation.SupportOrder2341 || SkillTrigger.ReadyIcon==null || SkillTrigger.CooldownArea==null))errors.Add("스킬 발동 인식은 시험 순서에서 재입력 아이콘과 쿨타임 영역을 등록해야 합니다.");
        if(errors.Count==0)
        {
            try{var execution=ExecutionCycle(s);if(!Balanced(execution)||execution[^1].AtMs>PeriodMs)errors.Add("시험 전투 입력 길이보다 시작 간격을 길게 설정하세요.");}
            catch(InvalidDataException e){errors.Add(e.Message);}
        }
        return errors;
    }
    private static bool Balanced(IEnumerable<RecordedInput> inputs)
    {
        var held = new HashSet<(bool mouse, int code)>();
        foreach (var i in inputs)
        {
            if (i.Kind is InputKind.KeyDown or InputKind.MouseDown) held.Add((i.Kind == InputKind.MouseDown, i.Code));
            if (i.Kind is InputKind.KeyUp or InputKind.MouseUp && !held.Remove((i.Kind == InputKind.MouseUp, i.Code))) return false;
        }
        return held.Count == 0;
    }
    public string Summary(Segment s)
    {
        var switches = Cycle(s).Where(i => i.Kind == InputKind.KeyDown && i.Code is >= 0x31 and <= 0x34)
            .Select(i => i.Code - 0x30).ToList();
        var order = switches.Where((v, i) => i == 0 || switches[i - 1] != v);
        return (Rotation==CombatRotation.Recorded ? $"녹화 순서 {string.Join(" → ", order)}" : "시험 순서 2 E×2 → 3 E·낙하 → 4 E → 1 딜링") + $" · 원본 {StartMs / 1000.0:0.000}~{EndMs / 1000.0:0.000}초 · 최소 간격 {PeriodMs / 1000.0:0.000}초";
    }
}

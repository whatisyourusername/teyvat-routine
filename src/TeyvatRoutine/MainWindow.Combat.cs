using System.Globalization;
using System.Windows;

namespace TeyvatRoutine;
public partial class MainWindow
{
    private void FillCombatSettings(CombatLoop c)
    {
        string Seconds(long ms) => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
        CombatEnabledCheck.IsChecked = c.Enabled;
        SecondSkillWaitBox.Text=c.SecondSkillWaitMs.ToString();SkillTriggerCheck.IsChecked=c.SkillTrigger.Enabled;
        ThirdSwitchWaitBox.Text=c.ThirdSwitchWaitMs.ToString();PartyReadyCheck.IsChecked=c.WaitForThirdPartyReady;
        PartyReadySummary.Text=c.ThirdPartyReady==null?"3번 파티 이름 · 미등록":"3번 파티 이름 · 등록됨";
        FourthSwitchCheck.IsChecked=c.FourthPartySwitch.Enabled;
        PartySlotSummary.Text="파티 번호 등록: "+string.Join(" · ",Enumerable.Range(0,4).Select(i=>$"{i+1} {(c.FourthPartySwitch.Keycaps.ElementAtOrDefault(i)==null?"미등록":"완료")}"));
        CombatProgressLabel.Text=c.InProgress==null?"자동 이어가기용 · 미등록":"자동 이어가기용 · 등록됨";
        SkillTriggerSummary.Text=$"재입력 아이콘: {(c.SkillTrigger.ReadyIcon==null?"미등록":"등록됨")} · 쿨타임 영역: {(c.SkillTrigger.CooldownArea==null?"미등록":"등록됨")}";
        CombatRotationPicker.SelectedIndex = (int)c.Rotation;
        CombatHoldBox.Text = c.ThirdSkillHoldMs.ToString(); CombatLandingBox.Text = c.LandingWaitMs.ToString();
        CombatStartBox.Text = Seconds(c.StartMs); CombatEndBox.Text = Seconds(c.EndMs);
        CombatPeriodBox.Text = Seconds(c.PeriodMs); CombatCyclesBox.Text = c.MaxCycles.ToString();
        CombatTimeoutBox.Text = c.TimeoutSeconds.ToString(); CombatRecenterCheck.IsChecked = c.RecenterBetweenCycles;
    }
    private void SuggestCombatClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s || s.Id != "battle") return;
        try
        {
            var c = CombatLoop.Suggest(s); c.Enabled = true; FillCombatSettings(c);
            CombatSummary.Text = c.Summary(s) + " · 아직 미적용 — 값을 확인한 뒤 ‘전투 설정 적용’을 누르세요.";
        }
        catch (Exception ex) { Error(ex); }
    }
    private void ApplyCombatClick(object sender, RoutedEventArgs e)
    {
        if (_busy || Selected is not Segment s || s.Id != "battle") return;
        try
        {
            long Milliseconds(string value)
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < 0 || n > 600)
                    throw new InvalidOperationException("전투 시간은 0~600 범위의 초 단위 숫자로 입력하세요.");
                return (long)Math.Round(n * 1000);
            }
            if (!int.TryParse(CombatCyclesBox.Text, out int cycles) || !int.TryParse(CombatTimeoutBox.Text, out int timeout) || !int.TryParse(CombatHoldBox.Text,out int hold) || !int.TryParse(CombatLandingBox.Text,out int landing) || !int.TryParse(SecondSkillWaitBox.Text,out int secondWait) || !int.TryParse(ThirdSwitchWaitBox.Text,out int switchWait))
                throw new InvalidOperationException("사이클 수와 전투 제한은 정수로 입력하세요.");
            var c = new CombatLoop { Enabled = CombatEnabledCheck.IsChecked == true,
                StartMs = Milliseconds(CombatStartBox.Text), EndMs = Milliseconds(CombatEndBox.Text),
                PeriodMs = Milliseconds(CombatPeriodBox.Text), MaxCycles = cycles, TimeoutSeconds = timeout,
                Rotation=(CombatRotation)CombatRotationPicker.SelectedIndex,ThirdSkillHoldMs=hold,LandingWaitMs=landing,
                SecondSkillWaitMs=secondWait,InProgress=s.Combat.InProgress,SkillTrigger=new(){Enabled=SkillTriggerCheck.IsChecked==true,ReadyIcon=s.Combat.SkillTrigger.ReadyIcon,CooldownArea=s.Combat.SkillTrigger.CooldownArea},
                ThirdSwitchWaitMs=switchWait,WaitForThirdPartyReady=PartyReadyCheck.IsChecked==true,ThirdPartyReady=s.Combat.ThirdPartyReady,
                FourthPartySwitch=new(){Enabled=FourthSwitchCheck.IsChecked==true,Keycaps=s.Combat.FourthPartySwitch.Keycaps},
                RecenterBetweenCycles = CombatRecenterCheck.IsChecked == true };
            var errors = c.Validate(s);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            s.Combat = c;
            if (s.After != null) s.After.GoldTextOnly = CombatGoldTextCheck.IsChecked == true;
            if (c.Enabled) s.StopOnCompletion = true;
            Save(); RefreshEditor(); Log("전투 반복 설정 저장 · " + (c.Enabled ? c.Summary(s) : "꺼짐"));
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void CaptureSkillReadyClick(object sender,RoutedEventArgs e)=>await CaptureSkill(true);
    private async void CaptureSkillCooldownClick(object sender,RoutedEventArgs e)=>await CaptureSkill(false);
    private async Task CaptureSkill(bool ready)
    {
        if(_busy || Selected is not Segment s || s.Id!="battle")return;
        SetBusy(true);_operation=new();
        try
        {
            var game=Game;await Countdown(game,ready?"첫 E 뒤 재입력 아이콘 캡처":"쿨타임 숫자 영역 캡처",_operation.Token);
            using var screen=Native.Capture(game);CheckSize(s,screen);Activate();
            var dialog=new CaptureWindow(screen,ready?"첫 E 뒤 재입력 아이콘":"쿨타임 숫자 영역",instructions:ready?"첫 E 뒤 바뀐 흰색 아이콘을 선택하세요. 아래의 E 키 글자는 제외하세요.":"13.5처럼 소수점 숫자가 표시되는 영역만 선택하세요. 두 자리 초까지 들어가도록 좌우 여백을 주고, 위아래 여백은 조금만 남기세요."){Owner=this};
            if(dialog.ShowDialog()!=true)return;
            var c=dialog.Result!;c.SearchRadius=ready?3:0;c.Threshold=.91;c.WhiteForegroundOnly=ready;
            if(ready)s.Combat.SkillTrigger.ReadyIcon=c;else s.Combat.SkillTrigger.CooldownArea=c;
            Save();RefreshEditor();Log(ready?"2번 E 재입력 아이콘 등록":"2번 E 쿨타임 영역 등록");
        }
        catch(OperationCanceledException){Log("스킬 이미지 등록 취소");}
        catch(Exception ex){Error(ex);}
        finally{_operation?.Dispose();_operation=null;SetBusy(false);}
    }
    private async void CapturePartyReadyClick(object sender,RoutedEventArgs e)
    {
        if(_busy || Selected is not Segment s || s.Id!="battle")return;
        SetBusy(true);_operation=new();
        try
        {
            var game=Game;await Countdown(game,"3번 파티 이름 등록 · 2번 캐릭터로 모션이 끝난 상태",_operation.Token);
            using var screen=Native.Capture(game);CheckSize(s,screen);Activate();
            var dialog=new CaptureWindow(screen,"3번 파티 이름 등록",instructions:"2번 캐릭터를 조작하며 모션이 끝난 상태에서, 오른쪽 3번 캐릭터의 밝은 이름만 선택하세요. 숫자 3, 초상화, HP 막대와 넓은 배경은 제외하세요."){Owner=this};
            if(dialog.ShowDialog()!=true)return;
            var c=dialog.Result!;c.SearchRadius=2;c.Threshold=.80;c.WhiteForegroundOnly=true;c.WhiteBrightness=200;
            s.Combat.ThirdPartyReady=c;Save();RefreshEditor();Log("3번 파티 이름 등록 · 전환 대기는 전투 설정에서 적용하세요.");
        }
        catch(OperationCanceledException){Log("파티 이름 등록 취소");}
        catch(Exception ex){Error(ex);}
        finally{_operation?.Dispose();_operation=null;SetBusy(false);}
    }
    private async void TestSkillClick(object sender,RoutedEventArgs e)
    {
        if(_busy || Selected is not Segment s || s.Id!="battle")return;
        var t=s.Combat.SkillTrigger;
        if(t.ReadyIcon==null || t.CooldownArea==null){Error(new InvalidOperationException("재입력 아이콘과 쿨타임 영역을 먼저 등록하세요."));return;}
        SetBusy(true);_operation=new();
        try
        {
            var game=Game;await Countdown(game,"스킬 인식 테스트 · 게임 입력 없음",_operation.Token);
            using var screen=Native.Capture(game);CheckSize(s,screen);
            double score=Vision.Score(screen,t.ReadyIcon);bool cooldown=CooldownDigits.Present(screen,t.CooldownArea);
            string detail=$"재입력 아이콘 {score:P1} / 기준 {t.ReadyIcon.Threshold:P1}\n쿨타임 숫자 표시: {(cooldown?"감지":"미감지")}\n숫자의 값은 읽지 않습니다. 실제 실행은 두 화면 연속 확인합니다.";
            if(s.Combat.ThirdPartyReady is Checkpoint party)detail+=$"\n3번 파티 이름 밝기 회복 {Vision.Score(screen,party):P1} / 기준 {party.Threshold:P1} (2번 조작 중 확인)";
            if(s.Combat.FourthPartySwitch.Keycaps.Count==4 && s.Combat.FourthPartySwitch.Keycaps.All(c=>c!=null))
            {var seen=PartySlotReader.Read(screen,s.Combat.FourthPartySwitch);detail+=$"\n활성 파티 번호: {(seen.Slot==0?"확인 불가":seen.Slot.ToString())} · 형태 {seen.Confidence:P1}";}
            SaveDiagnostic(screen,detail);Activate();new CaptureWindow(screen,"스킬 인식 테스트",true,detail){Owner=this}.ShowDialog();Log(detail.Replace('\n',' '));
        }
        catch(OperationCanceledException){Log("스킬 인식 테스트 취소");}
        catch(Exception ex){Error(ex);}
        finally{_operation?.Dispose();_operation=null;SetBusy(false);}
    }
    private async void CapturePartySlotClick(object sender,RoutedEventArgs e)
    {
        if(_busy || Selected is not Segment s || s.Id!="battle")return;
        int slot=PartySlotPicker.SelectedIndex+1;if(slot is <1 or >4)return;
        SetBusy(true);_operation=new();
        try
        {
            var game=Game;await Countdown(game,$"{slot}번 파티 번호 등록 · 다른 캐릭터를 선택해 두세요",_operation.Token);
            using var screen=Native.Capture(game);CheckSize(s,screen);Activate();
            var dialog=new CaptureWindow(screen,$"{slot}번 파티 번호 등록",instructions:$"현재 조작 중인 캐릭터가 {slot}번이면 취소하세요. 오른쪽 숫자 {slot}의 흰색 사각형 안쪽을 선택하세요. 숫자 전체와 흰 배경을 포함하고 바깥 풍경·둥근 테두리는 제외하세요."){Owner=this};
            if(dialog.ShowDialog()!=true)return;
            var c=dialog.Result!;c.SearchRadius=1;
            using var crop=Vision.Decode(c);var bytes=Vision.Pixels(crop);
            if(bytes.Average(b=>(double)b)<180)throw new InvalidOperationException("활성 캐릭터의 흐린 번호 대신 다른 캐릭터의 밝은 번호를 등록하세요.");
            if(s.Combat.FourthPartySwitch.Keycaps.Count!=4)s.Combat.FourthPartySwitch.Keycaps=new(){null,null,null,null};
            s.Combat.FourthPartySwitch.Keycaps[slot-1]=c;Save();RefreshEditor();Log($"{slot}번 파티 번호 등록 · 교체 확인은 전투 설정에서 적용하세요.");
        }
        catch(OperationCanceledException){Log("파티 번호 등록 취소");}
        catch(Exception ex){Error(ex);}
        finally{_operation?.Dispose();_operation=null;SetBusy(false);}
    }
}

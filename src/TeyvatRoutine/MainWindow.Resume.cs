using System.Windows;
using System.Windows.Controls;

namespace TeyvatRoutine;
public partial class MainWindow
{
    private int _resumeRound=1;
    private Window ResumeDialog(string title,StackPanel panel)=>new(){Owner=this,Title=title,Width=620,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)FindResource(typeof(Window)),Content=panel};
    private async void ResumeClick(object sender,RoutedEventArgs e)
    {
        if(_busy)return;
        var plan=Workflow.Build(_profile);
        if(plan.Count==0){Error(new InvalidOperationException("왼쪽에서 이어서 실행할 작업을 선택하세요."));return;}
        var panel=new StackPanel{Margin=new Thickness(20)};var dialog=ResumeDialog("중간에서 이어서 시작",panel);
        panel.Children.Add(new TextBlock{Text="왼쪽에서 선택한 작업 중 시작점을 고릅니다. 이전 작업은 건너뛰며, 녹화의 임의 시점까지 복원하지는 않습니다.",TextWrapping=TextWrapping.Wrap});
        var mode=new ComboBox{ItemsSource=new[]{"현재 화면에서 자동 찾기","작업·동작을 직접 선택"},SelectedIndex=0,Margin=new Thickness(0,14,0,10)};panel.Children.Add(mode);
        panel.Children.Add(new TextBlock{Text="자동 찾기: 현재 비경 회차 (화면만으로는 횟수를 알 수 없습니다)"});
        var rounds=new ComboBox{ItemsSource=Enumerable.Range(1,_profile.DomainRuns).ToArray(),SelectedItem=Math.Min(_resumeRound,_profile.DomainRuns),Margin=new Thickness(0,6,0,12)};panel.Children.Add(rounds);
        panel.Children.Add(new TextBlock{Text="직접 선택: 이 동작부터 남은 작업을 실행"});
        var items=new ComboBox{ItemsSource=plan.Select(ResumePlanner.Label).ToArray(),SelectedIndex=Math.Max(0,plan.FindIndex(i=>i.Segment==Selected)),Margin=new Thickness(0,6,0,8),IsEnabled=false};panel.Children.Add(items);
        var combat=new CheckBox{Content="전투가 이미 시작됨 · 시작 장치 F 생략",IsChecked=true,IsEnabled=false};panel.Children.Add(combat);
        void Enable(){bool manual=mode.SelectedIndex==1;items.IsEnabled=manual;rounds.IsEnabled=!manual;combat.IsEnabled=manual && items.SelectedIndex>=0 && plan[items.SelectedIndex].Segment.Id=="battle" && plan[items.SelectedIndex].Segment.Combat.Enabled;}
        mode.SelectionChanged+=(_,_)=>Enable();items.SelectionChanged+=(_,_)=>Enable();
        panel.Children.Add(new TextBlock{Text="자동 모드는 등록한 시작·완료 화면과 나무 도착 F를 두 번 확인합니다. 전투 도중 자동 찾기는 ‘전투 중 화면 등록’이 필요합니다. 구분이 안 되면 직접 선택하세요.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,12)});
        var start=new Button{Content="이어서 시작",HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(start);start.Click+=(_,_)=>dialog.DialogResult=true;
        if(dialog.ShowDialog()!=true)return;
        _resumeRound=(int)rounds.SelectedItem;
        if(mode.SelectedIndex==1)
        {
            await RunPlan(ResumePlanner.Continue(plan,items.SelectedIndex,combat.IsEnabled && combat.IsChecked==true));return;
        }
        List<ResumeCandidate>? candidates=null;
        SetBusy(true);_operation=new();
        try
        {
            var game=Game;var token=_operation.Token;await Countdown(game,"이어갈 화면 확인 · 입력 없음",token);
            int width=_profile.ResolutionWidth,height=_profile.ResolutionHeight;Native.Ensure(game,width,height);
            using var first=Native.Capture(game);await Task.Delay(180,token);Native.Ensure(game,width,height);
            using var second=Native.Capture(game);
            candidates=await Task.Run(()=>
            {
                var cache=new Dictionary<Checkpoint,double>();
                return ResumePlanner.Detect(plan,_resumeRound,c=>
                {
                    token.ThrowIfCancellationRequested();if(cache.TryGetValue(c,out double v))return v;
                    double value=Math.Min(Vision.Score(first,c),Vision.Score(second,c));cache[c]=value;return value;
                }, _profile.ScreenFlow);
            },token);
            token.ThrowIfCancellationRequested();Native.Ensure(game,width,height);
        }
        catch(OperationCanceledException){Log("이어서 시작 화면 확인 취소");}
        catch(Exception ex){Error(ex);}
        finally{_operation?.Dispose();_operation=null;SetBusy(false);}
        if(candidates==null)return;
        if(candidates.Count==0){ShowText("이어갈 위치를 확인하지 못했습니다","등록된 화면과 일치하는 시작점을 찾지 못했습니다. ‘이어서 시작’에서 작업·동작을 직접 선택하세요. 전투 도중 자동 인식은 전투에만 보이는 목표/남은 적 표시를 등록해야 합니다.");return;}
        ResumeCandidate? chosen=candidates.Count==1?candidates[0]:ChooseResumeCandidate(plan,candidates);
        if(chosen==null)return;
        Log("이어서 시작: "+chosen.Reason);
        var remaining=ResumePlanner.Continue(plan,chosen.Index,chosen.CombatInProgress);
        if(remaining.Count==0){RunState.Text="이미 마지막 작업 완료 화면입니다";RunDetail.Text="추가 입력 없이 종료했습니다.";return;}
        await RunPlan(remaining);
    }
    private ResumeCandidate? ChooseResumeCandidate(List<PlanItem> plan,List<ResumeCandidate> candidates)
    {
        Activate();var panel=new StackPanel{Margin=new Thickness(20)};var dialog=ResumeDialog("이어갈 위치 선택",panel);
        panel.Children.Add(new TextBlock{Text="현재 화면과 맞는 후보가 여러 개입니다. 이어갈 위치를 선택하세요.",TextWrapping=TextWrapping.Wrap});
        var list=new ListBox{ItemsSource=candidates.Select(c=>(c.Index==plan.Count?"선택한 작업 모두 완료":ResumePlanner.Label(plan[c.Index]))+(c.CombatInProgress?" · 전투 도중":"")+"\n"+c.Reason).ToArray(),MinHeight=130,MaxHeight=340,Margin=new Thickness(0,12,0,12)};panel.Children.Add(list);
        var start=new Button{Content="선택한 위치부터 실행",IsEnabled=false,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(start);
        list.SelectionChanged+=(_,_)=>start.IsEnabled=list.SelectedIndex>=0;start.Click+=(_,_)=>dialog.DialogResult=true;
        return dialog.ShowDialog()==true?candidates[list.SelectedIndex]:null;
    }
    private async void CaptureCombatProgressClick(object sender,RoutedEventArgs e)
    {
        if(_busy || Selected is not Segment s || s.Id!="battle")return;
        SetBusy(true);_operation=new();
        try
        {
            var game=Game;await Countdown(game,"전투 중 화면 캡처",_operation.Token);
            using var screen=Native.Capture(game);CheckSize(s,screen);Activate();
            var dialog=new CaptureWindow(screen,"전투 중 화면 등록",instructions:"전투가 시작된 뒤에만 보이는 고정 목표 문구를 선택하세요. 변하는 적 수·시간은 제외하고, 평소에도 보이는 Enter·체력·스킬 표시는 피하세요."){Owner=this};
            if(dialog.ShowDialog()==true){s.Combat.InProgress=dialog.Result;Save();RefreshEditor();Log("전투 도중 자동 이어가기 화면 등록");}
        }
        catch(OperationCanceledException){Log("전투 중 화면 등록 취소");}
        catch(Exception ex){Error(ex);}
        finally{_operation?.Dispose();_operation=null;SetBusy(false);}
    }
}

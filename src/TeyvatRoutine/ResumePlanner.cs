namespace TeyvatRoutine;
public sealed record ResumeCandidate(int Index,bool CombatInProgress,string Reason,int Priority=0);
public static class ResumePlanner
{
    public static string Label(PlanItem item)=>item.Segment.Name+(item.Round>0?$" · {item.Round}회차":"");
    public static List<PlanItem> Continue(IReadOnlyList<PlanItem> plan,int index,bool combatInProgress=false)
    {
        if(index<0 || index>plan.Count)throw new ArgumentOutOfRangeException(nameof(index));
        var remaining=plan.Skip(index).ToList();
        if(combatInProgress)
        {
            if(remaining.Count==0 || remaining[0].Segment.Id!="battle" || !remaining[0].Segment.Combat.Enabled)
                throw new InvalidOperationException("전투 도중 재개는 전투 사이클 반복이 설정된 전투에서만 가능합니다.");
            remaining[0]=remaining[0] with{CombatInProgress=true};
        }
        return remaining;
    }
    // Scores must be the minimum from two separately captured frames. Do not infer the
    // reward count from identical domain screens; the user specifies the current round.
    public static List<ResumeCandidate> Detect(IReadOnlyList<PlanItem> plan,int round,Func<Checkpoint,double> score, ScreenFlowSettings? flow = null)
    {
        var found=new List<ResumeCandidate>();
        bool Matches(Checkpoint? c)=>c!=null && score(c)>=c.Threshold;
        for(int i=0;i<plan.Count;i++)
        {
            var item=plan[i];var s=item.Segment;
            if(item.Round>0 && item.Round!=round)continue;
            if(s.Id=="login" && flow?.LoginEnabled==true)
            {
                if(Matches(flow.Door) || (flow.WelkinEnabled && Matches(flow.Welkin)))
                    found.Add(new(i,false,"로그인 문·공월 화면",110));
                if(Matches(flow.Start ?? s.Before) || Matches(flow.Field ?? s.After))found.Add(new(i,false,"로그인 상태 확인부터 이어가기"));
                continue;
            }
            // A battle's common Enter HUD does not distinguish idle from active combat.
            if(s.Id=="battle" && s.Combat.Enabled)
            {
                if(Matches(s.Combat.InProgress))found.Add(new(i,true,"등록한 전투 중 화면",100));
            }
            else if(!s.Navigation.Enabled && Matches(s.Before))found.Add(new(i,false,s.Name+" 시작 화면"));
            if(s.Navigation.Enabled && Matches(s.Navigation.Arrival))found.Add(new(i,false,"나무 도착 F 표시",100));
            if(s.Id!="quit" && Matches(s.After))
            {
                int priority=(s.Id=="battle" && s.After!.GoldTextOnly || s.Id=="collect")?100:0;
                found.Add(new(i+1,false,s.Name+" 완료 화면",priority));
            }
        }
        if(found.Count==0)return found;
        int best=found.Max(c=>c.Priority);
        return found.Where(c=>c.Priority==best).GroupBy(c=>(c.Index,c.CombatInProgress))
            .Select(g=>g.First() with{Reason=string.Join(" / ",g.Select(c=>c.Reason).Distinct())}).OrderBy(c=>c.Index).ToList();
    }
}

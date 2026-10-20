using System.Diagnostics;

namespace TeyvatRoutine;
public static class PartySwitchGate
{
    // Start only after the second E has been confirmed (or released in timed mode).
    // This checks switch availability, not that the character swap itself succeeded.
    public static async Task<bool> Run(IRuntime runtime,CombatLoop combat,Func<int,Task<bool>> wait,Action<string> report)
    {
        try
        {
            report($"2→3 교체 · 두 번째 E 뒤 모션 대기 {combat.ThirdSwitchWaitMs}ms");
            if(await wait(combat.ThirdSwitchWaitMs))return true;
            if(!combat.WaitForThirdPartyReady)return false;
            var ready=combat.ThirdPartyReady ?? throw new InvalidOperationException("3번 파티 이름을 먼저 등록하세요.");
            var clock=RuntimeClock.StartNew(runtime);int stable=0;double score=0;
            report("2→3 교체 · 파티 이름의 반투명 표시가 풀리는지 확인");
            while(clock.ElapsedMilliseconds<2000)
            {
                if(await wait(0))return true;
                score=runtime.Match(ready);
                if(await wait(0))return true;
                stable=score>=ready.Threshold?stable+1:0;
                if(stable>=2){report($"3번 파티 이름 밝기 회복 확인 {score:P0} · 교체 진행");return false;}
                if(await wait(60))return true;
            }
            throw new TimeoutException($"3번 파티 이름의 밝기 회복을 확인하지 못해 교체 전에 중단했습니다 ({score:P0} / 기준 {ready.Threshold:P0}). 파티 이름 등록과 화면 상태를 확인하세요.");
        }
        finally{runtime.Release();}
    }
}

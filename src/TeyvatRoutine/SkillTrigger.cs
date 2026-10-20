using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace TeyvatRoutine;
public sealed class SkillTrigger
{
    public bool Enabled { get; set; }
    public Checkpoint? ReadyIcon { get; set; }
    public Checkpoint? CooldownArea { get; set; }
}
public sealed record SkillObservation(double ReadyScore,bool Cooldown);
public static class SecondSkillChain
{
    public static async Task<(bool Completed,bool AlreadyReady)> PrepareResume(IRuntime runtime,SkillTrigger trigger,Func<int,Task<bool>> wait,Action<string> report)
    {
        var clock=RuntimeClock.StartNew(runtime);int clear=0,ready=0;bool logged=false;
        while(clock.ElapsedMilliseconds<20000)
        {
            if(await wait(0))return(true,false);
            var seen=runtime.ObserveSkill(trigger);
            clear=seen.Cooldown?0:clear+1;
            ready=!seen.Cooldown && seen.ReadyScore>=trigger.ReadyIcon!.Threshold?ready+1:0;
            if(clear>=2)return(false,ready>=2);
            if(seen.Cooldown && !logged){report("전투 재개 · 2번 E의 남은 쿨타임 표시가 사라질 때까지 대기");logged=true;}
            if(await wait(80))return(true,false);
        }
        throw new TimeoutException("전투 재개 중 2번 E의 쿨타임 표시가 20초 동안 사라지지 않아 중단했습니다.");
    }
    // Called immediately after the first E is released. All waits use actual elapsed time.
    // wait also checks foreground focus, cancellation and the stage completion image.
    public static async Task<bool> Run(IRuntime runtime,CombatLoop combat,Func<int,Task<bool>> wait,Action<string> report)
    {
        try
        {
            if(!combat.SkillTrigger.Enabled)
            {
                report($"2번 E 재입력 · 첫 E를 놓은 뒤 {combat.SecondSkillWaitMs}ms 대기");
                if(await wait(combat.SecondSkillWaitMs))return true;
                runtime.Play(new(){Kind=InputKind.KeyDown,Code=0x45});
                if(await wait(100))return true;
                runtime.Play(new(){Kind=InputKind.KeyUp,Code=0x45});return false;
            }
            var trigger=combat.SkillTrigger;var clock=RuntimeClock.StartNew(runtime);int ready=0;
            report("2번 첫 E · 재입력 아이콘 확인 중");
            while(clock.ElapsedMilliseconds<2000)
            {
                if(await wait(0))return true;
                var seen=runtime.ObserveSkill(trigger);
                ready=!seen.Cooldown && seen.ReadyScore>=trigger.ReadyIcon!.Threshold?ready+1:0;
                if(ready>=2)break;
                if(await wait(40))return true;
            }
            if(ready<2)throw new TimeoutException("2번 E 재입력 아이콘을 확인하지 못했습니다. 스킬 상태·등록 이미지를 확인하세요.");
            int attempts=0,cooldown=0;long lastRelease=-1000;clock.Restart();
            while(clock.ElapsedMilliseconds<3000)
            {
                if(attempts==0 || ready>=2 && cooldown==0 && attempts<3 && clock.ElapsedMilliseconds-lastRelease>=250)
                {
                    if(await wait(0))return true;
                    report($"2번 두 번째 E 입력 {++attempts}/3 · 쿨타임 표시 확인");
                    runtime.Play(new(){Kind=InputKind.KeyDown,Code=0x45});
                    if(await wait(100))return true;
                    runtime.Play(new(){Kind=InputKind.KeyUp,Code=0x45});lastRelease=clock.ElapsedMilliseconds;ready=0;
                }
                if(await wait(0))return true;
                var seen=runtime.ObserveSkill(trigger);
                cooldown=seen.Cooldown?cooldown+1:0;
                ready=!seen.Cooldown && seen.ReadyScore>=trigger.ReadyIcon!.Threshold?ready+1:0;
                if(cooldown>=2){report("2번 두 번째 E 발동 확인 · 쿨타임 숫자 표시 감지");return false;}
                if(await wait(50))return true;
            }
            throw new TimeoutException("2번 두 번째 E의 쿨타임 표시를 확인하지 못해 중단했습니다. 다음 캐릭터로 넘기지 않습니다.");
        }
        finally{runtime.Release();}
    }
}
public static class CooldownDigits
{
    // Detect the layout of a decimal countdown (e.g. 13.5), without pretending to OCR its value.
    public static bool Present(Bitmap screen,Checkpoint area)
    {
        var region=new Rectangle(area.X,area.Y,area.Width,area.Height);
        if(!new Rectangle(0,0,screen.Width,screen.Height).Contains(region))return false;
        using var crop=screen.Clone(region,PixelFormat.Format24bppRgb);
        var pixels=Vision.Pixels(crop);int w=crop.Width,h=crop.Height;
        var mask=new bool[w*h];
        for(int i=0;i<mask.Length;i++){int b=pixels[3*i],g=pixels[3*i+1],r=pixels[3*i+2];mask[i]=Math.Min(r,Math.Min(g,b))>=180 && Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<=40;}
        var components=new List<(Rectangle Box,int Count)>();
        for(int start=0;start<mask.Length;start++)
        {
            if(!mask[start])continue;
            var queue=new Queue<int>();queue.Enqueue(start);mask[start]=false;int minX=w,minY=h,maxX=0,maxY=0,count=0;
            while(queue.Count>0)
            {
                int p=queue.Dequeue(),x=p%w,y=p/w;count++;minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){int nx=x+dx,ny=y+dy;if(nx>=0 && ny>=0 && nx<w && ny<h && mask[ny*w+nx]){mask[ny*w+nx]=false;queue.Enqueue(ny*w+nx);}}
            }
            if(count>=2)components.Add((Rectangle.FromLTRB(minX,minY,maxX+1,maxY+1),count));
        }
        foreach(var dot in components.Where(c=>c.Box.Width<=h*.26 && c.Box.Height<=h*.26 && c.Box.Top>h*.35 && c.Count<=h*h*.07))
        {
            var digits=components.Where(c=>c.Box.Height>=h*.35 && c.Box.Height<=h*.9 && c.Box.Width>=h*.09 && c.Box.Width<=h*.7 && c.Count>=h*.65 && Math.Abs(c.Box.Bottom-dot.Box.Bottom)<=h*.17).ToList();
            var left=digits.Where(c=>c.Box.Right<=dot.Box.Left+1).OrderBy(c=>c.Box.Left).ToList();
            var right=digits.Where(c=>c.Box.Left>=dot.Box.Right-1).ToList();
            if(left.Count is <1 or >2 || right.Count!=1)continue;
            var all=left.Concat(right).ToList();
            if(all.Max(c=>c.Box.Top)-all.Min(c=>c.Box.Top)>h*.2 || dot.Box.Left-left[^1].Box.Right>h*.3 || right[0].Box.Left-dot.Box.Right>h*.3)continue;
            if(components.Except(all).Except(new[]{dot}).Any(c=>c.Count>h*.7))continue;
            return true;
        }
        return false;
    }
}

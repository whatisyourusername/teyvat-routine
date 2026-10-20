using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;

namespace TeyvatRoutine;
public static class SkillTests
{
    public static List<string> Run()
    {
        var results=new List<string>();
        void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        void Test(string name,Action test){test();results.Add("PASS  "+name);}
        CombatLoop Settings()=>new(){SkillTrigger=new(){Enabled=true,ReadyIcon=new(){Threshold=.91},CooldownArea=new()}};
        bool Run(Runtime r,CombatLoop c,CancellationToken token=default,Func<bool>? complete=null)
        {
            async Task<bool> Wait(int ms)
            {
                var clock=Stopwatch.StartNew();
                do{token.ThrowIfCancellationRequested();r.Ensure(new());if(complete?.Invoke()==true)return true;
                    if(clock.ElapsedMilliseconds>=ms)return false;await Task.Delay(5,token);
                }while(true);
            }
            return SecondSkillChain.Run(r,c,Wait,_=>{}).GetAwaiter().GetResult();
        }
        Test("Second E fallback waits 500ms from the actual first release",()=>
        {
            var r=new Runtime();var clock=Stopwatch.StartNew();long pressed=0;
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown)pressed=clock.ElapsedMilliseconds;};
            Run(r,new());Check(pressed>=500 && pressed<1200 && r.Presses==1 && !r.Held,"Fallback compressed or repeated E");
        });
        Test("Ready icon triggers E immediately and two cooldown frames confirm success",()=>
        {
            var r=new Runtime();int cooldownFrames=0;
            r.Observe=()=>r.Presses==0?new(1,false):new(0,++cooldownFrames>=1);
            var clock=Stopwatch.StartNew();Run(r,Settings());
            Check(r.Presses==1 && cooldownFrames==2 && clock.ElapsedMilliseconds<500 && !r.Held,"Trigger or confirmation did not work");
        });
        Test("Transient ready and cooldown frames cannot trigger or confirm the chain",()=>
        {
            var r=new Runtime();int seen=0;
            var observations=new Queue<SkillObservation>(new[]{new SkillObservation(1,false),new(0,false),new(1,false),new(1,false),new(0,true),new(0,false),new(0,true),new(0,true)});
            r.Observe=()=>{seen++;return observations.Dequeue();};
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown)Check(seen==4,"Single ready frame fired E");};
            Run(r,Settings());Check(seen==8 && r.Presses==1,"Single cooldown frame confirmed success");
        });
        Test("Ignored second E is retried only while its ready icon persists",()=>
        {
            var r=new Runtime();r.Observe=()=>r.Presses<2?new(1,false):new(0,true);
            Run(r,Settings());Check(r.Presses==2 && !r.Held,"Did not recover the ignored input");
        });
        Test("Missing cooldown stops after three bounded attempts",()=>
        {
            var r=new Runtime();r.Observe=()=>new(1,false);bool stopped=false;
            try{Run(r,Settings());}catch(TimeoutException){stopped=true;}
            Check(stopped && r.Presses==3 && !r.Held,"Unbounded retries or unconfirmed success");
        });
        Test("A pre-existing cooldown cannot substitute for first-E readiness",()=>
        {
            var r=new Runtime();r.Observe=()=>new(0,true);bool stopped=false;
            try{Run(r,Settings());}catch(TimeoutException){stopped=true;}
            Check(stopped && r.Presses==0 && !r.Held,"Old cooldown incorrectly advanced the chain");
        });
        Test("Lost icon does not cause blind retries",()=>
        {
            var r=new Runtime();r.Observe=()=>r.Presses==0?new(1,false):new(0,false);bool stopped=false;
            try{Run(r,Settings());}catch(TimeoutException){stopped=true;}
            Check(stopped && r.Presses==1 && !r.Held,"Blind E retries");
        });
        Test("Completion, cancellation and focus loss release E during visual confirmation",()=>
        {
            foreach(int mode in new[]{0,1,2})
            {
                var r=new Runtime();r.Observe=()=>new(1,false);using var cancel=new CancellationTokenSource();bool stopped=false;
                r.OnPlay=i=>{if(i.Kind!=InputKind.KeyDown)return;if(mode==1)cancel.Cancel();if(mode==2)r.Focus=false;};
                try{stopped=Run(r,Settings(),cancel.Token,()=>mode==0 && r.Presses>0);}catch(Exception ex)when(ex is OperationCanceledException or InvalidOperationException){stopped=true;}
                Check(stopped && !r.Held && r.Presses==1,"Interrupt left E held or pressed again");
            }
        });
        Test("Cooldown layout accepts 13.5 and single-digit decimals, rejects icon-like solid patches",()=>
        {
            using var image=new Bitmap(60,28);using var g=Graphics.FromImage(image);
            using var font=new Font("Arial",16,FontStyle.Bold,GraphicsUnit.Pixel);
            var roi=new Checkpoint{X=0,Y=0,Width=60,Height=28};
            foreach(string value in new[]{"13.5","9.9","0.1"})
            {
                g.Clear(Color.FromArgb(35,40,55));g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                g.DrawString(value,font,Brushes.White,8,3,StringFormat.GenericTypographic);
                Check(CooldownDigits.Present(image,roi),"Decimal not detected: "+value);
            }
            g.Clear(Color.White);Check(!CooldownDigits.Present(image,roi),"Solid white matched");
            g.Clear(Color.Black);g.FillEllipse(Brushes.White,10,3,35,22);Check(!CooldownDigits.Present(image,roi),"Icon blob matched");
        });
        Test("Combat resume waits through existing cooldown and distinguishes an already-ready second E",()=>
        {
            foreach(bool ready in new[]{true,false})
            {
                var r=new Runtime();int observations=0;
                r.Observe=()=>++observations<=3?new(0,true):new(ready?1:0,false);
                var result=SecondSkillChain.PrepareResume(r,Settings().SkillTrigger,_=>Task.FromResult(false),_=>{}).GetAwaiter().GetResult();
                Check(observations==5 && result.AlreadyReady==ready && !result.Completed && r.Presses==0,"Resume pressed into an existing cooldown or lost followup state");
            }
        });
        return results;
    }
    private sealed class Runtime:IRuntime
    {
        public int Presses;public bool Held,Focus=true;
        public Func<SkillObservation> Observe=()=>new(0,false);
        public Action<RecordedInput>? OnPlay;
        public bool Exists=>true;
        public void Ensure(Segment s){if(!Focus)throw new InvalidOperationException("Focus lost");}
        public double Match(Checkpoint c)=>0;
        public SkillObservation ObserveSkill(SkillTrigger settings)=>Observe();
        public void Play(RecordedInput i){if(i.Kind==InputKind.KeyDown){Presses++;Held=true;}else if(i.Kind==InputKind.KeyUp)Held=false;OnPlay?.Invoke(i);}
        public void Release()=>Held=false;
    }
}

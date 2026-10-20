using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace TeyvatRoutine;
public static class ScreenFlowTests
{
    private sealed class Runtime : IRuntime
    {
        public bool Exists => true;
        public Func<Checkpoint, double> Score = _ => 0;
        public Func<IReadOnlyList<Checkpoint>, double[]>? Frame;
        public Action? EnsureAction;
        public Action<RecordedInput>? OnPlay;
        public List<(RecordedInput Input, long At)> Inputs = new();
        public HashSet<int> Keys = new();
        public HashSet<int> Buttons = new();
        public Stopwatch Clock = Stopwatch.StartNew();
        public int Releases;
        public void Ensure(Segment s) => EnsureAction?.Invoke();
        public double Match(Checkpoint c) => Score(c);
        public double[] Scores(IReadOnlyList<Checkpoint> c) => Frame?.Invoke(c) ?? c.Select(Score).ToArray();
        public void Play(RecordedInput e)
        {
            Inputs.Add((e, Clock.ElapsedMilliseconds));
            if(e.Kind==InputKind.KeyDown)Keys.Add(e.Code);
            if(e.Kind==InputKind.KeyUp)Keys.Remove(e.Code);
            if(e.Kind==InputKind.MouseDown)Buttons.Add(e.Code);
            if(e.Kind==InputKind.MouseUp)Buttons.Remove(e.Code);
            OnPlay?.Invoke(e);
        }
        public void Release() { Keys.Clear(); Buttons.Clear(); Releases++; }
    }
    public static List<string> Run()
    {
        var result = new List<string>();
        void Test(string name, Action body) { body(); result.Add("PASS  "+name); }
        void Check(bool value, string reason) { if(!value)throw new Exception(reason); }
        using var bitmap = new Bitmap(48,48);
        using(var g=Graphics.FromImage(bitmap)) { g.Clear(Color.Navy);g.FillRectangle(Brushes.White,5,5,15,30);g.FillRectangle(Brushes.Gold,25,10,18,13); }
        Checkpoint C() => Vision.Crop(bitmap,new Rectangle(0,0,48,48));
        ScreenFlowSettings Flow() => new() { LoginEnabled=true, LoadingEnabled=true, Door=C(), Start=C(), Field=C(), Welkin=C(), LoadingScreens=new(){C()} };
        Segment Login(ScreenFlowSettings f)
        {
            var s=Profile.Create().Get("login");s.Width=960;s.Height=540;s.Before=f.Start;s.After=f.Field;s.DurationMs=1;
            s.Inputs=new(){new(){Kind=InputKind.KeyDown,Code=0x57},new(){Kind=InputKind.KeyUp,Code=0x57,AtMs=1}};
            return s;
        }
        void RunLogin(Runtime r, ScreenFlowSettings f, int quiet=8, int timeout=1500, CancellationToken token=default)
            => LoginFlow.Run(r,Login(f),f,_=>{},token,1,quiet,timeout).GetAwaiter().GetResult();
        Runtime Sequence(ScreenFlowSettings f, params string[] frames)
        {
            int index=0;var r=new Runtime();
            r.Frame=checks => {
                string state=frames[Math.Min(index++,frames.Length-1)];
                return checks.Select(c => ReferenceEquals(c,state switch{"start"=>f.Start,"door"=>f.Door,"field"=>f.Field,"popup"=>f.Welkin,_=>null})?1.0:0).ToArray();
            };
            return r;
        }
        int Clicks(Runtime r)=>r.Inputs.Count(e=>e.Input.Kind==InputKind.MouseDown);
        Test("Login clicks once before update, waits for stable door, then waits for field",()=>{
            var f=Flow();var r=Sequence(f,"start","start","update","update","update","door","door","blank","blank","field");
            RunLogin(r,f);Check(Clicks(r)==2 && r.Keys.Count+r.Buttons.Count==0,"Login click count or release incorrect");
        });
        Test("Starting at an already ready door sends only one entry click",()=>{
            var f=Flow();var r=Sequence(f,"door","door","blank","field");RunLogin(r,f);
            Check(Clicks(r)==1,"Repeated initial login click");
        });
        Test("Already claimed Welkin and visible field produce no clicks",()=>{
            var f=Flow();f.WelkinEnabled=true;var r=Sequence(f,"field");RunLogin(r,f);
            Check(Clicks(r)==0,"Blind popup clicks on field");
        });
        Test("Delayed Welkin interrupts field quiet period and is dismissed",()=>{
            var f=Flow();f.WelkinEnabled=true;var r=Sequence(f,"field","field","field","popup","popup","field");
            RunLogin(r,f,20);Check(Clicks(r)==1,"Delayed popup was skipped");
        });
        Test("Starting from Welkin can finish login without reclicking the door",()=>{
            var f=Flow();f.WelkinEnabled=true;var r=Sequence(f,"popup","popup","blank","field");RunLogin(r,f);
            Check(Clicks(r)==1 && r.Inputs[0].Input.Y==.78,"Wrong popup action");
        });
        Test("Single door or popup flicker cannot trigger a click",()=>{
            var f=Flow();f.WelkinEnabled=true;var r=Sequence(f,"door","blank","popup","blank","field");RunLogin(r,f);
            Check(Clicks(r)==0,"One-frame recognition triggered input");
        });
        Test("Update timeout stops without speculative clicks",()=>{
            var f=Flow();var r=Sequence(f,"update");
            try { RunLogin(r,f,8,35);throw new Exception("Missing timeout"); } catch(TimeoutException){}
            Check(Clicks(r)==0 && r.Releases>0,"Timeout sent input");
        });
        Test("Cancellation during login click releases the mouse",()=>{
            var f=Flow();var r=Sequence(f,"door");using var cancel=new CancellationTokenSource();
            r.OnPlay=e=>{if(e.Kind==InputKind.MouseDown)cancel.Cancel();};
            try{RunLogin(r,f,8,1500,cancel.Token);throw new Exception("Missing cancellation");}catch(OperationCanceledException){}
            Check(r.Buttons.Count==0,"Cancelled click stuck");
        });
        Test("Loading releases movement, waits for positive ready UI, then resumes movement",()=>{
            var f=Flow();var s=Login(f);var r=new Runtime();bool active=false;int samples=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(active && ++samples<3?1:0):ReferenceEquals(c,f.Field)&&samples>=6?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>{},2,3);
            guarded.Ensure(s);guarded.Play(new(){Kind=InputKind.KeyDown,Code=0x57});active=true;Thread.Sleep(4);
            var watch=RuntimeClock.StartNew(guarded);guarded.Ensure(s);
            Check(samples>=8 && r.Releases>=1 && r.Keys.Contains(0x57),"Loading did not require stable field UI");
            Check(guarded.PausedMilliseconds>=10 && watch.ElapsedMilliseconds<30,"Loading time was not excluded");
            guarded.Release();
        });
        Test("Loading never repeats held menu clicks or skills",()=>{
            var f=Flow();var s=Login(f);var r=new Runtime();bool active=false;int samples=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(active && ++samples<3?1:0):ReferenceEquals(c,f.Field)?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>{},2,3);
            guarded.Ensure(s);guarded.Play(new(){Kind=InputKind.MouseDown,Code=0});guarded.Play(new(){Kind=InputKind.KeyDown,Code=0x45});
            active=true;Thread.Sleep(4);guarded.Ensure(s);guarded.Play(new(){Kind=InputKind.MouseUp,Code=0});
            Check(Clicks(r)==1 && r.Inputs.Count(e=>e.Input.Kind==InputKind.MouseUp)==0 && r.Keys.Count+r.Buttons.Count==0,"Menu click or skill repeated");
        });
        Test("Domain HUD resumes two separate warps even without the outdoor Paimon icon",()=>{
            var f=Flow();f.DomainField=C();var s=Login(f);var r=new Runtime();bool active=false;int samples=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(active && ++samples<3?1:0):ReferenceEquals(c,f.DomainField)?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>{},2,3);
            guarded.Ensure(s);
            for(int round=0;round<2;round++){
                samples=0;active=true;Thread.Sleep(4);long before=guarded.PausedMilliseconds;
                guarded.Ensure(s);Check(guarded.PausedMilliseconds>before && samples>=5,"Second warp or domain HUD failed");
                active=false;
            }
        });
        Test("Disappearing loading icons without a ready screen time out with no input",()=>{
            var f=Flow();f.LoadingTimeoutSeconds=5;var s=Login(f);var r=new Runtime();int probes=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0]) && ++probes==1?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>{},5);
            try{guarded.Ensure(s);throw new Exception("Missing loading timeout");}catch(TimeoutException){}
            Check(r.Inputs.Count==0 && r.Keys.Count+r.Buttons.Count==0,"Unknown screen resumed replay");
        });
        Test("Domain notice resumes loading with both outdoor and domain HUD hidden",()=>{
            var f=Flow();f.DomainNotice=C();f.DomainField=C();var r=new Runtime();int samples=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(++samples<=2?1:0):ReferenceEquals(c,f.DomainNotice)?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>{},2,3);
            guarded.Ensure(Login(f));
            Check(samples>=5 && r.Inputs.Count==0 && guarded.PausedMilliseconds>0,"Notice did not resume or sent an extra click");
        });
        Test("A flickering domain notice must become stable after loading disappears",()=>{
            var f=Flow();f.DomainNotice=C();var r=new Runtime();int samples=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(++samples<=2?1:0):
                ReferenceEquals(c,f.DomainNotice)&&(samples==3 || samples>=5)?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>{},2,3);
            guarded.Ensure(Login(f));
            Check(samples>=7 && r.Inputs.Count==0,"Flickering notice or remaining loading allowed early replay");
        });
        Test("Domain entry replays its recorded dismiss click and movement after the notice",()=>{
            var f=Flow();f.DomainNotice=C();var s=Login(f);s.Id="enter_domain";s.After=C();s.DurationMs=600;
            s.Inputs=new(){
                new(){Kind=InputKind.KeyDown,Code=0x46},new(){AtMs=50,Kind=InputKind.KeyUp,Code=0x46},
                new(){AtMs=250,Kind=InputKind.MouseDown,Code=0},new(){AtMs=300,Kind=InputKind.MouseUp,Code=0},
                new(){AtMs=400,Kind=InputKind.KeyDown,Code=0x57},new(){AtMs=600,Kind=InputKind.KeyUp,Code=0x57}};
            var r=new Runtime();bool entered=false,done=false,dismissed=false;int samples=0;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(entered && ++samples<=2?1:0):
                ReferenceEquals(c,f.DomainNotice)&&entered || ReferenceEquals(c,s.Before)&&!entered || ReferenceEquals(c,s.After)&&done?1:0;
            r.OnPlay=e=>{
                if(e.Kind==InputKind.KeyUp && e.Code==0x46)entered=true;
                if(e.Kind==InputKind.MouseDown){Check(samples>=5,"Dismissal happened during loading");dismissed=true;}
                if(e.Kind==InputKind.KeyDown && e.Code==0x57)Check(dismissed,"Movement preceded dismissal");
                if(e.Kind==InputKind.KeyUp && e.Code==0x57)done=true;
            };
            new RoutineEngine(r,_=>{},f).Run(new[]{new PlanItem(s)},CancellationToken.None,1,1).GetAwaiter().GetResult();
            Check(done && Clicks(r)==1 && r.Keys.Count+r.Buttons.Count==0,"Recorded entry did not complete cleanly");
        });
        Test("Welkin popup is dismissed even when the field HUD remains visible behind it",()=>{
            var f=Flow();f.WelkinEnabled=true;var r=new Runtime();int frame=0;
            r.Frame=checks=>checks.Select(c=>ReferenceEquals(c,f.Field)||ReferenceEquals(c,f.Welkin)&&frame<2?1.0:0).ToArray();
            r.OnPlay=e=>{if(e.Kind==InputKind.MouseDown)frame=2;};
            RunLogin(r,f);Check(Clicks(r)==1,"Visible background HUD prevented popup dismissal");
        });
        Test("Loading cancellation releases all controls and never resumes movement",()=>{
            var f=Flow();var s=Login(f);var r=new Runtime();bool active=false;using var cancel=new CancellationTokenSource();
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])&&active?1:0;
            var guarded=new LoadingRuntime(r,f,cancel.Token,_=>cancel.Cancel(),2);
            guarded.Ensure(s);guarded.Play(new(){Kind=InputKind.KeyDown,Code=0x57});active=true;Thread.Sleep(4);
            try{guarded.Ensure(s);throw new Exception("Missing cancellation");}catch(OperationCanceledException){}
            Check(r.Keys.Count+r.Buttons.Count==0,"Loading cancellation left held inputs");
        });
        Test("Focus loss while waiting for loading fails closed and releases controls",()=>{
            var f=Flow();var s=Login(f);var r=new Runtime();bool active=false;
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])&&active?1:0;
            var guarded=new LoadingRuntime(r,f,CancellationToken.None,_=>r.EnsureAction=()=>throw new InvalidOperationException("focus"),2);
            guarded.Ensure(s);guarded.Play(new(){Kind=InputKind.KeyDown,Code=0x57});active=true;Thread.Sleep(4);
            try{guarded.Ensure(s);throw new Exception("Missing focus failure");}catch(InvalidOperationException){}
            Check(r.Keys.Count+r.Buttons.Count==0,"Focus loss left movement held");
        });
        Test("Engine pauses the timeline across loading instead of bursting later commands",()=>{
            var f=Flow();var s=Login(f);s.Id="warp_domain";s.DurationMs=750;s.TimeoutSeconds=1;
            s.Inputs=new(){new(){Kind=InputKind.KeyDown,Code=0x57},new(){AtMs=250,Kind=InputKind.KeyUp,Code=0x57},
                new(){AtMs=500,Kind=InputKind.KeyDown,Code=0x46},new(){AtMs=750,Kind=InputKind.KeyUp,Code=0x46}};
            var r=new Runtime();long begin=-1;r.OnPlay=e=>{if(begin<0)begin=r.Clock.ElapsedMilliseconds;};
            r.Score=c=>ReferenceEquals(c,f.LoadingScreens[0])?(begin>=0 && r.Clock.ElapsedMilliseconds-begin is >=50 and <450?1:0):1;
            new RoutineEngine(r,_=>{},f).Run(new[]{new PlanItem(s)},CancellationToken.None,1,1).GetAwaiter().GetResult();
            var down=r.Inputs.Single(e=>e.Input.Kind==InputKind.KeyDown && e.Input.Code==0x46);
            var up=r.Inputs.Single(e=>e.Input.Kind==InputKind.KeyUp && e.Input.Code==0x46);
            Check(down.At-begin>800 && up.At-down.At>=220 && r.Keys.Count==0,"Inputs caught up after loading");
        });
        Test("Screen login preserves its legacy recording and ignores its commands",()=>{
            var f=Flow();f.LoadingEnabled=false;f.FieldQuietMs=1000;var s=Login(f);var r=Sequence(f,"field");
            new RoutineEngine(r,_=>{},f).Run(new[]{new PlanItem(s)},CancellationToken.None,1,1).GetAwaiter().GetResult();
            Check(r.Inputs.Count==0 && s.Inputs.Count==2 && s.DurationMs==1,"Old login recording overwritten or replayed");
        });
        Test("Resume detection prioritizes a login door and popup over generic matches",()=>{
            var f=Flow();f.WelkinEnabled=true;var s=Login(f);var plan=new[]{new PlanItem(s)};
            foreach(var screen in new[]{f.Door,f.Welkin}) {
                var found=ResumePlanner.Detect(plan,1,c=>ReferenceEquals(c,screen)?1:0,f);
                Check(found.Count==1 && found[0].Index==0 && found[0].Priority==110,"Login resume was skipped");
            }
        });
        Test("Enabled screen login permits no legacy input recording",()=>{
            var f=Flow();var s=Login(f);s.Inputs.Clear();s.DurationMs=0;
            Check(Workflow.Validate(new[]{new PlanItem(s)},f).Count==0,"Screen login demanded a recording");
            Check(Workflow.Validate(new[]{new PlanItem(s)}).Count>0,"Legacy mode accepted missing recording");
        });
        Test("Missing or malformed loading and Welkin registrations are rejected",()=>{
            var f=Flow();f.Field=null;Check(f.Validate(960,540,true).Count>0,"Missing ready screen accepted");
            f=Flow();f.WelkinEnabled=true;f.Welkin=null;Check(f.Validate(960,540,true).Count>0,"Unregistered Welkin accepted");
            f=Flow();f.Door!.PngBase64="invalid";Check(f.Validate(960,540,true).Count>0,"Invalid image accepted");
            f=Flow();f.LoadingScreens.Clear();Check(f.Validate(960,540,false).Count>0,"Empty loading list accepted");
            f=Flow();f.DomainNotice=C();f.DomainNotice.PngBase64="invalid";Check(f.Validate(960,540,false).Count>0,"Invalid domain notice accepted");
        });
        Test("Profile round trip preserves screen flow and existing recording",()=>{
            var f=Flow();f.DomainNotice=C();var p=Profile.Create();p.ResolutionWidth=960;p.ResolutionHeight=540;p.ScreenFlow=f;
            p.Get("login").Inputs=Login(f).Inputs;
            string path=Path.Combine(Path.GetTempPath(),"teyvat-screenflow-"+Guid.NewGuid()+".json");
            try { ProfileStore.Save(p,path);var copy=ProfileStore.Load(path);
                Check(copy.ScreenFlow.LoadingScreens.Count==1 && copy.ScreenFlow.Door!.PngBase64==f.Door!.PngBase64 && copy.Get("login").Inputs.Count==2,"Profile lost settings");
                Check(copy.ScreenFlow.DomainNotice?.PngBase64==f.DomainNotice.PngBase64,"Domain notice registration lost");
            }finally{if(File.Exists(path))File.Delete(path);}
        });
        return result;
    }
}

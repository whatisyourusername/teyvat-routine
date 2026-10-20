using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace TeyvatRoutine;
public static class CombatTests
{
    public static List<string> Run()
    {
        var results=new List<string>();
        void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        void Test(string name,Action action){action();results.Add("PASS  "+name);}
        using var bitmap=new Bitmap(96,48);
        using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.DarkBlue);g.FillRectangle(Brushes.Gold,12,12,10,22);g.FillRectangle(Brushes.Gold,40,18,25,7);}
        var image=Vision.Crop(bitmap,new Rectangle(0,0,96,48));
        Checkpoint Copy()=>JsonSerializer.Deserialize<Checkpoint>(JsonSerializer.Serialize(image))!;
        RecordedInput Key(long at,int code,bool down)=>new(){AtMs=at,Code=code,Kind=down?InputKind.KeyDown:InputKind.KeyUp};
        Segment Battle()
        {
            var s=Profile.Create().Get("battle");s.Width=960;s.Height=540;s.Before=Copy();s.After=Copy();s.TimeoutSeconds=1;s.StopOnCompletion=false;s.DurationMs=42;
            s.Inputs=new(){Key(0,70,true),Key(1,70,false),Key(10,50,true),Key(12,50,false),Key(14,69,true),Key(16,69,false),Key(20,49,true),Key(22,49,false),new(){AtMs=24,Kind=InputKind.MouseDown},new(){AtMs=26,Kind=InputKind.MouseUp},Key(40,164,true),Key(42,164,false)};
            s.Combat=CombatLoop.Suggest(s);s.Combat.Enabled=true;s.Combat.PeriodMs=80;s.Combat.MaxCycles=3;s.Combat.TimeoutSeconds=2;s.Combat.RecenterBetweenCycles=false;return s;
        }
        Segment Reward()
        {
            var s=Profile.Create().Get("collect");s.Navigation.Enabled=false;s.StopOnCompletion=false;s.Width=960;s.Height=540;s.Before=Copy();s.After=Copy();s.DurationMs=1;s.TimeoutSeconds=1;
            s.Inputs=new(){Key(0,88,true),Key(1,88,false)};return s;
        }
        void Run(Segment s,Runtime r,Segment? after=null,CancellationToken token=default)
        {new RoutineEngine(r,_=>{}).Run(after==null?new[]{new PlanItem(s)}:new[]{new PlanItem(s),new PlanItem(after)},token,2,2).GetAwaiter().GetResult();}
        Test("Combat analysis isolates the rotation without changing the original recording",()=>
        {
            var s=Battle();string before=JsonSerializer.Serialize(s.Inputs);var c=CombatLoop.Suggest(s);
            Check(c.StartMs==10 && c.EndMs==26 && c.PeriodMs==1000 && c.Cycle(s).Count==8,"Wrong suggested bounds");
            Check(before==JsonSerializer.Serialize(s.Inputs) && c.Summary(s).Contains("2 → 1"),"Analysis changed inputs or lost order");
        });
        Test("Uncleared combat repeats only the rotation and advances only after stable clear",()=>
        {
            var s=Battle();var next=Reward();var r=new Runtime();
            r.Score=c=>c==s.After?(r.Count(49)>=2?1:0):1;
            Run(s,r,next);
            Check(r.Count(50)==2 && r.Count(70)==1 && r.Count(164)==0 && r.Count(88)==1 && r.Held.Count==0,"Opener repeated, tail leaked, or clear failed");
            var times=r.Timed.Where(i=>i.Input.Kind==InputKind.KeyDown && i.Input.Code==50).Select(i=>i.At).ToArray();
            Check(times[1]-times[0]>=70,"Cooldown interval was compressed");
        });
        Test("Combat clear interrupts a held key even if legacy early-completion toggle is off",()=>
        {
            var s=Battle();var r=new Runtime();r.Score=c=>c==s.After?(r.Count(69)>0?1:0):1;Run(s,r);
            Check(r.Count(69)==1 && r.Count(49)==0 && r.Held.Count==0,"Held key continued after clear");
        });
        Test("Already-cleared combat skips the challenge trigger",()=>
        {var s=Battle();var r=new Runtime();Run(s,r);Check(r.Inputs.Count==0,"Re-triggered a cleared domain");});
        Test("No clear at cycle limit stops before reward or navigation",()=>
        {
            var s=Battle();s.Combat.MaxCycles=2;var r=new Runtime();r.Score=c=>c==s.After?0:1;bool stopped=false;
            try{Run(s,r,Reward());}catch(TimeoutException){stopped=true;}
            Check(stopped && r.Count(50)==2 && r.Count(88)==0 && r.Held.Count==0,"Unconfirmed combat advanced");
        });
        Test("Combat deadline bounds long waits and releases every input",()=>
        {
            var s=Battle();s.Combat.PeriodMs=5000;s.Combat.TimeoutSeconds=1;var r=new Runtime();r.Score=c=>c==s.After?0:1;bool stopped=false;var timer=Stopwatch.StartNew();
            try{Run(s,r,Reward());}catch(TimeoutException){stopped=true;}
            Check(stopped && timer.ElapsedMilliseconds<2500 && r.Count(88)==0 && r.Held.Count==0,"Deadline was ignored");
        });
        Test("Combat cancellation and focus loss both release held inputs",()=>
        {
            foreach(bool focus in new[]{false,true})
            {
                var s=Battle();var r=new Runtime();r.Score=c=>c==s.After?0:1;using var cancel=new CancellationTokenSource();bool stopped=false;
                r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown){if(focus)r.Focus=false;else cancel.Cancel();}};
                try{Run(s,r,Reward(),cancel.Token);}catch(Exception e)when(e is OperationCanceledException or InvalidOperationException){stopped=true;}
                Check(stopped && r.Held.Count==0 && r.Count(88)==0,"Stop failed to release keys");
            }
        });
        Test("Normal attack then middle-click precedes additional cycles and remains interruptible",()=>
        {
            var s=Battle();s.Combat.RecenterBetweenCycles=true;var r=new Runtime();
            r.Score=c=>c==s.After?(r.Inputs.Any(i=>i.Kind==InputKind.MouseDown && i.Code==2)?1:0):1;Run(s,r);
            var mouse=r.Inputs.Where(i=>i.Kind is InputKind.MouseDown or InputKind.MouseUp).ToArray();
            Check(mouse.Length==5 && mouse[2].Kind==InputKind.MouseDown && mouse[2].Code==0 && mouse[4].Kind==InputKind.MouseDown && mouse[4].Code==2 && r.Count(50)==1 && r.Held.Count==0,"Camera sequence or interruption failed");
        });
        Test("One transient clear frame cannot finish combat",()=>
        {
            var s=Battle();var r=new Runtime();bool flashed=false;
            r.Score=c=>{if(c!=s.After)return 1;if(r.Count(50)>=2)return 1;if(!flashed && r.Count(50)==1){flashed=true;return 1;}return 0;};
            Run(s,r);Check(r.Count(50)==2,"Single-frame match ended combat");
        });
        Test("Combat ranges reject held boundaries, menus, and invalid intervals before input",()=>
        {
            foreach(var edit in new Action<Segment>[] {s=>s.Combat.StartMs=12,s=>s.Combat.EndMs=24,s=>s.Combat.PeriodMs=1,s=>s.Combat.EndMs=42,s=>s.Combat.MaxCycles=0,s=>s.Combat.TimeoutSeconds=0})
            {var s=Battle();edit(s);var r=new Runtime();bool failed=false;try{Run(s,r);}catch(InvalidDataException){failed=true;}Check(failed && r.Inputs.Count==0,"Invalid cycle played input");}
        });
        Test("Rerecording and reset invalidate old combat ranges without losing completion on rerecord",()=>
        {
            var s=Battle();var after=s.After;s.StoreRecording(s.Inputs,42);Check(!s.Combat.Enabled && s.After==after,"Rerecord reused stale cycle");s=Battle();s.ResetRegistration();Check(!s.Combat.Enabled,"Reset retained combat loop");
        });
        Test("Old profiles retain recorded combat behavior and new settings round-trip",()=>
        {
            var p=Profile.Create();string legacy=JsonSerializer.Serialize(p,ProfileStore.Options);
            Check(!JsonSerializer.Deserialize<Profile>(legacy,ProfileStore.Options)!.Get("battle").Combat.Enabled,"New defaults silently repeat old profiles");
            p.Segments[p.Segments.FindIndex(s=>s.Id=="battle")]=Battle();var loaded=JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(p,ProfileStore.Options),ProfileStore.Options)!;
            Check(loaded.Get("battle").Combat.Enabled && loaded.Get("battle").Combat.PeriodMs==80 && loaded.Get("warp_train").Name.Contains("몬드"),"Settings or renamed task lost");
        });
        Test("Gold-text detection tolerates background changes and rejects missing or solid gold",()=>
        {
            image.GoldTextOnly=true;image.SearchRadius=0;
            using var different=new Bitmap(96,48);using(var g=Graphics.FromImage(different)){g.Clear(Color.LightBlue);g.FillRectangle(Brushes.Gold,12,12,10,22);g.FillRectangle(Brushes.Gold,40,18,25,7);}
            Check(Vision.Score(different,image)>.95,"Text lost to background");
            using(var g=Graphics.FromImage(different))g.Clear(Color.Gold);Check(Vision.Score(different,image)<.65,"Solid gold falsely matched");
            using(var g=Graphics.FromImage(different))g.Clear(Color.LightBlue);Check(Vision.Score(different,image)==0,"Missing text matched");
        });
        Test("Experimental support order keeps the original damage tail and recording intact",()=>
        {
            var s=Battle();string original=JsonSerializer.Serialize(s.Inputs);s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;
            Check(s.Combat.Validate(s).Count==0,"Valid experiment rejected");
            var sequence=s.Combat.ExecutionCycle(s);var switches=sequence.Where(i=>i.Kind==InputKind.KeyDown && i.Code is >=49 and <=52).Select(i=>i.Code);
            Check(switches.SequenceEqual(new[]{50,51,52,49}),"Support order is wrong");
            var third=sequence.Single(i=>i.Kind==InputKind.KeyDown && i.Code==51);var fourth=sequence.Single(i=>i.Kind==InputKind.KeyDown && i.Code==52);
            var skill=sequence.Where(i=>i.Code==69 && i.AtMs>third.AtMs && i.AtMs<fourth.AtMs).ToArray();
            var plunge=sequence.First(i=>i.Kind==InputKind.MouseDown);
            Check(skill.Length==2 && skill[1].AtMs-skill[0].AtMs==100 && plunge.AtMs>skill[1].AtMs && fourth.AtMs-plunge.AtMs==480,"E tap, plunge or landing wait is wrong");
            Check(sequence.Count(i=>i.Kind==InputKind.KeyDown && i.Code==69 && i.AtMs<third.AtMs)==2,"Slot 2 must use E twice");
            Check(JsonSerializer.Serialize(s.Inputs)==original && s.Combat.Opening(s).Count(i=>i.Kind==InputKind.KeyDown && i.Code==70)==1,"Original recording or opener changed");
            var main=sequence.Single(i=>i.Kind==InputKind.KeyDown && i.Code==49);
            Check(sequence.Last().AtMs-main.AtMs==6 && sequence.Last().Kind==InputKind.MouseUp,"Main damage timings changed");
        });
        Test("Experimental order rejects missing main damage and too-short cycle periods",()=>
        {
            var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;
            Check(s.Combat.Validate(s).Count>0,"Generated cycle overlaps the next cycle");
            s.Combat.PeriodMs=27000;s.Inputs.RemoveAll(i=>i.Code==49);
            Check(s.Combat.Validate(s).Count>0,"Missing main slot accepted");
        });
        Test("Engine waits after real first E, sends exactly one second E and shifts the following inputs",()=>
        {
            var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;s.Combat.TimeoutSeconds=5;
            var r=new Runtime();r.Score=c=>c==s.After?(r.Count(51)>0?1:0):1;
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyUp && i.Code==69 && r.Count(69)==1)Thread.Sleep(200);};
            Run(s,r);var es=r.Timed.Where(i=>i.Input.Code==69).ToArray();
            Check(es.Length==4 && es[2].At-es[1].At>=700 && r.Count(51)==1 && r.Held.Count==0,"Engine compressed E or replayed generated second E");
            Check(r.Timed.Single(i=>i.Input.Kind==InputKind.KeyDown && i.Input.Code==51).At-es[3].At>=500,"Third swap did not wait 500ms after E release");
        });
        Test("Visual E confirmation waits 500ms plus stable party recovery before 3 and preserves delayed swap-to-E timing",()=>
        {
            var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;s.Combat.TimeoutSeconds=8;
            s.Combat.SkillTrigger=new(){Enabled=true,ReadyIcon=Copy(),CooldownArea=Copy()};s.Combat.WaitForThirdPartyReady=true;s.Combat.ThirdPartyReady=Copy();
            var r=new Runtime();int confirmations=0,partyReads=0;long confirmed=0,partyFirst=0;
            r.Observe=()=>{if(r.Count(69)<2)return new(1,false);if(++confirmations==2)confirmed=r.Millis;return new(0,true);};
            r.Score=c=>c==s.After?(r.Count(69)>=3?1:0):c==s.Combat.ThirdPartyReady?Party():1;
            double Party(){if(++partyReads==1)partyFirst=r.Millis;return partyReads>=3?1:0;}
            r.OnPlay=i=>{if(i.Kind==InputKind.KeyUp && i.Code==51)Thread.Sleep(300);};
            Run(s,r);var third=r.Timed.Single(i=>i.Input.Kind==InputKind.KeyDown && i.Input.Code==51);
            var released=r.Timed.Single(i=>i.Input.Kind==InputKind.KeyUp && i.Input.Code==51);
            var skill=r.Timed.Last(i=>i.Input.Kind==InputKind.KeyDown && i.Input.Code==69);
            Check(confirmations==2 && partyReads==4 && partyFirst-confirmed>=500 && third.At-confirmed>=680,"Visual confirmation or party recovery skipped");
            Check(skill.At-released.At>=550 && r.Count(69)==3 && r.Held.Count==0,"Lag compressed swap-to-skill wait");
        });
        Test("Clear detected during the party wait prevents the 3 swap",()=>
        {
            var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;s.Combat.TimeoutSeconds=6;
            s.Combat.WaitForThirdPartyReady=true;s.Combat.ThirdPartyReady=Copy();var r=new Runtime();int partyReads=0;
            r.Score=c=>c==s.After?(partyReads>0?1:0):c==s.Combat.ThirdPartyReady?(++partyReads>0?0:0):1;
            Run(s,r);Check(r.Count(51)==0 && r.Count(69)==2 && r.Held.Count==0,"Clear during gate still sent a swap");
        });
        Test("Invalid party registration and sub-500ms motion waits fail before input",()=>
        {
            foreach(int mode in new[]{0,1,2,3})
            {
                var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;
                s.Combat.WaitForThirdPartyReady=true;s.Combat.ThirdPartyReady=Copy();
                if(mode==0)s.Combat.ThirdPartyReady=null;
                if(mode==1)s.Combat.ThirdPartyReady!.X=9999;
                if(mode==2)s.Combat.ThirdSwitchWaitMs=499;
                if(mode==3)s.Combat.ThirdPartyReady!.WhiteBrightness=256;
                var r=new Runtime();bool failed=false;try{Run(s,r);}catch(InvalidDataException){failed=true;}
                Check(failed && r.Inputs.Count==0,"Invalid party settings reached playback");
            }
        });
        Test("Visual trigger images are validated before the engine sends any input",()=>
        {
            var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;s.Combat.SkillTrigger=new(){Enabled=true,ReadyIcon=Copy(),CooldownArea=Copy()};
            s.Combat.SkillTrigger.CooldownArea.X=9999;var r=new Runtime();bool failed=false;
            try{Run(s,r);}catch(InvalidDataException){failed=true;}
            Check(failed && r.Inputs.Count==0,"Out-of-bounds skill area reached playback");
        });
        Test("Resuming an active fight skips opener F and waits for a confirmed clear",()=>
        {
            var s=Battle();var r=new Runtime();r.Score=c=>c==s.Before?0:c==s.After?(r.Count(49)>0?1:0):0;
            new RoutineEngine(r,_=>{}).Run(new[]{new PlanItem(s,2,true)},CancellationToken.None,2,2).GetAwaiter().GetResult();
            Check(r.Count(70)==0 && r.Count(50)==1 && r.Count(49)==1 && r.Held.Count==0,"Resume replayed opener, waited for initial screen, or failed to clear");
        });
        Test("Already cleared resumed fight sends no combat input",()=>
        {
            var s=Battle();var r=new Runtime();new RoutineEngine(r,_=>{}).Run(new[]{new PlanItem(s,1,true)},CancellationToken.None,2,2).GetAwaiter().GetResult();
            Check(r.Inputs.Count==0,"Resumed a cleared fight");
        });
        Test("Actual plunge release and confirmed fourth swap preserve timing before fourth E",()=>
        {
            var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;s.Combat.TimeoutSeconds=10;
            var cap=Copy();cap.Width=48;cap.Height=48;using(var patch=bitmap.Clone(new Rectangle(0,0,48,48),System.Drawing.Imaging.PixelFormat.Format24bppRgb))cap=Vision.Crop(patch,new Rectangle(0,0,48,48));cap.SearchRadius=0;
            s.Combat.FourthPartySwitch=new(){Enabled=true,Keycaps=new(){cap,cap,cap,cap}};
            var r=new Runtime();long confirmed=0;
            r.Party=()=>{int active=r.Count(52)>=2?4:3;if(active==4)confirmed=r.Millis;return new(active,1);};
            r.Score=c=>c==s.After?(r.Count(69)>=4?1:0):1;
            r.OnPlay=i=>{if(i.Kind==InputKind.MouseUp)Thread.Sleep(200);};
            Run(s,r);
            var plunge=r.Timed.First(i=>i.Input.Kind==InputKind.MouseUp);
            var fourth=r.Timed.First(i=>i.Input.Kind==InputKind.KeyDown && i.Input.Code==52);
            var release=r.Timed.Last(i=>i.Input.Kind==InputKind.KeyUp && i.Input.Code==52);
            var skill=r.Timed.Last(i=>i.Input.Kind==InputKind.KeyDown && i.Input.Code==69);
            Check(fourth.At-plunge.At>=600,"Landing interval consumed by input latency");
            Check(r.Count(52)==2 && confirmed>0 && skill.At>=confirmed && skill.At-release.At>=200 && r.Held.Count==0,"Skill before confirmed swap or gap lost");
        });
        Test("Fourth swap registration rejects missing and oversized keycaps before input",()=>
        {
            foreach(int mode in new[]{0,1,2,3})
            {
                var s=Battle();s.Combat.Rotation=CombatRotation.SupportOrder2341;s.Combat.PeriodMs=27000;
                s.Combat.FourthPartySwitch=new(){Enabled=true,Keycaps=new(){Copy(),Copy(),Copy(),Copy()}};
                if(mode==0)s.Combat.FourthPartySwitch.Keycaps.RemoveAt(0);
                if(mode==1)s.Combat.FourthPartySwitch.Keycaps[0]=null;
                if(mode==2)s.Combat.FourthPartySwitch.Keycaps[0]!.X=9999;
                // Original 96px image is intentionally too wide in mode 3.
                var r=new Runtime();bool failed=false;try{Run(s,r);}catch(InvalidDataException){failed=true;}
                Check(failed && r.Inputs.Count==0,"Invalid fourth swap registration reached playback");
            }
        });
        return results;
    }
    private sealed class Runtime:IRuntime
    {
        private readonly Stopwatch _clock=Stopwatch.StartNew();
        public List<RecordedInput> Inputs{get;}=new();
        public List<(RecordedInput Input,long At)> Timed{get;}=new();
        public HashSet<(bool,int)> Held{get;}=new();
        public Func<Checkpoint,double> Score{get;set;}=_=>1;
        public Action<RecordedInput>? OnPlay{get;set;}
        public bool Focus{get;set;}=true;
        public bool Exists=>true;
        public long Millis=>_clock.ElapsedMilliseconds;
        public Func<SkillObservation> Observe=()=>new(0,false);
        public SkillObservation ObserveSkill(SkillTrigger settings)=>Observe();
        public Func<PartySlotObservation> Party=()=>new(0,0);
        public PartySlotObservation ObservePartySlot(PartySlotSettings settings)=>Party();
        public int Count(int code)=>Inputs.Count(i=>i.Kind==InputKind.KeyDown && i.Code==code);
        public void Ensure(Segment s){if(!Focus)throw new InvalidOperationException("Focus lost");}
        public double Match(Checkpoint c)=>Score(c);
        public void Play(RecordedInput i){Inputs.Add(i);Timed.Add((i,_clock.ElapsedMilliseconds));if(i.Kind is InputKind.KeyDown or InputKind.MouseDown)Held.Add((i.Kind==InputKind.MouseDown,i.Code));if(i.Kind is InputKind.KeyUp or InputKind.MouseUp)Held.Remove((i.Kind==InputKind.MouseUp,i.Code));OnPlay?.Invoke(i);}
        public void Release()=>Held.Clear();
    }
}

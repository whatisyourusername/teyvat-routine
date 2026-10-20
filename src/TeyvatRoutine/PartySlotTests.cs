using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace TeyvatRoutine;
public static class PartySlotTests
{
    public static List<string> Run()
    {
        var results=new List<string>();
        void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        void Test(string name,Action action){action();results.Add("PASS  "+name);}
        Test("Party numbers distinguish all four active slots and reject absent, multiple and globally dimmed HUDs",()=>
        {
            using var source=new Bitmap(160,120,PixelFormat.Format24bppRgb);
            using(var g=Graphics.FromImage(source))
            using(var font=new Font("Arial",22,FontStyle.Bold,GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(245,245,245));g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                for(int s=0;s<4;s++)g.DrawString((s+1).ToString(),font,Brushes.Black,s*36+5,8,StringFormat.GenericTypographic);
            }
            var settings=new PartySlotSettings{Enabled=true,Keycaps=Enumerable.Range(0,4).Select(s=>(Checkpoint?)Vision.Crop(source,new Rectangle(s*36,4,30,32))).ToList()};
            foreach(var c in settings.Keycaps)c!.SearchRadius=0;
            for(int slot=1;slot<=4;slot++)foreach(int background in new[]{25,100,200})
            {
                using var scene=new Bitmap(source);
                Fade(scene,settings.Keycaps[slot-1]!,.4,background);
                Check(PartySlotReader.Read(scene,settings).Slot==slot,"Active number missed");
            }
            Check(PartySlotReader.Read(source,settings).Slot==0,"No selected number became a selection");
            using var dim=new Bitmap(source);foreach(var c in settings.Keycaps)Fade(dim,c!,.4,100);
            Check(PartySlotReader.Read(dim,settings).Slot==0,"Globally dim HUD accepted");
            using var multiple=new Bitmap(source);Fade(multiple,settings.Keycaps[1]!, .4,100);Fade(multiple,settings.Keycaps[3]!, .4,100);
            Check(PartySlotReader.Read(multiple,settings).Slot==0,"Two active candidates accepted");
            using var hidden=new Bitmap(source);using(var g=Graphics.FromImage(hidden))g.Clear(Color.White);
            Check(PartySlotReader.Read(hidden,settings).Slot==0,"Hidden HUD accepted");
            using var covered=new Bitmap(source);Fade(covered,settings.Keycaps[3]!, .4,100);
            using(var g=Graphics.FromImage(covered))g.FillRectangle(Brushes.Black,0,4,30,32);
            Check(PartySlotReader.Read(covered,settings).Slot==0,"Partially hidden party accepted");
        });
        bool Run(Runtime runtime,Func<bool>? done=null,CancellationToken token=default)
        {
            async Task<bool> Wait(int ms)
            {
                var clock=Stopwatch.StartNew();
                do{token.ThrowIfCancellationRequested();runtime.Ensure(new());if(done?.Invoke()==true)return true;
                    if(clock.ElapsedMilliseconds>=ms)return false;await Task.Delay(5,token);}while(true);
            }
            return FourthPartySwitch.Confirm(runtime,new(),Wait,_=>{}).GetAwaiter().GetResult();
        }
        Test("Successful fourth swap needs fresh frames, no redundant retry, and minimum skill delay",()=>
        {
            var r=new Runtime();r.Read=()=>new(4,1);var clock=Stopwatch.StartNew();
            Check(!Run(r) && r.Inputs.Count==0 && r.Reads>=2 && clock.ElapsedMilliseconds>=200 && r.Held.Count==0,"Premature success or redundant input");
        });
        Test("Ignored fourth swap retries only 4, then confirms before proceeding",()=>
        {
            var r=new Runtime();r.Read=()=>new(r.Inputs.Count>=2?4:3,1);var clock=Stopwatch.StartNew();
            Check(!Run(r) && r.Inputs.Count==2 && r.Inputs.All(i=>i.Code==0x34) && r.Held.Count==0,"Retry sequence failed");
            Check(r.DownAt>=250 && r.UpAt-r.DownAt>=100 && clock.ElapsedMilliseconds-r.UpAt>=200,"Retry or skill gap compressed");
        });
        Test("Unknown HUD and one-frame flashes never confirm fourth swap or cause blind retries",()=>
        {
            var r=new Runtime();r.Read=()=>new(r.Reads%2==0?4:0,1);bool stopped=false;
            try{Run(r);}catch(TimeoutException){stopped=true;}
            Check(stopped && r.Inputs.Count==0 && r.Held.Count==0,"Unknown HUD or flash accepted");
        });
        Test("Fourth swap retries are bounded and stop before any skill",()=>
        {
            var r=new Runtime();r.Read=()=>new(3,1);bool stopped=false;
            try{Run(r);}catch(TimeoutException){stopped=true;}
            Check(stopped && r.Inputs.Count==6 && r.Inputs.All(i=>i.Code==52) && r.Held.Count==0,"Retry count unbounded or skill leaked");
        });
        Test("Clear, cancellation and focus loss interrupt fourth-swap retry and release held key",()=>
        {
            foreach(int mode in new[]{0,1,2})
            {
                var r=new Runtime();using var cts=new CancellationTokenSource();bool stopped=false;r.Read=()=>new(3,1);
                r.OnPlay=i=>{if(i.Kind==InputKind.KeyDown){if(mode==1)cts.Cancel();if(mode==2)r.Focus=false;}};
                try{stopped=Run(r,()=>mode==0 && r.Inputs.Count>0,cts.Token);}
                catch(Exception e)when(e is OperationCanceledException or InvalidOperationException){stopped=true;}
                Check(stopped && r.Inputs.Count==1 && r.Held.Count==0,"Interrupted retry continued or held key");
            }
        });
        return results;
    }
    private static void Fade(Bitmap scene,Checkpoint area,double gain,int background)
    {
        for(int y=area.Y;y<area.Y+area.Height;y++)for(int x=area.X;x<area.X+area.Width;x++)
        {var c=scene.GetPixel(x,y);int v=(int)Math.Round(c.R*gain+background*(1-gain));scene.SetPixel(x,y,Color.FromArgb(v,v,v));}
    }
    private sealed class Runtime:IRuntime
    {
        readonly Stopwatch clock=Stopwatch.StartNew();
        public int Reads;public bool Focus=true;public long DownAt,UpAt;
        public List<RecordedInput> Inputs=new();public HashSet<int> Held=new();
        public Func<PartySlotObservation> Read=()=>new(0,0);public Action<RecordedInput>? OnPlay;
        public bool Exists=>true;
        public void Ensure(Segment s){if(!Focus)throw new InvalidOperationException("Focus lost");}
        public double Match(Checkpoint c)=>0;
        public PartySlotObservation ObservePartySlot(PartySlotSettings settings){Reads++;return Read();}
        public void Play(RecordedInput i){Inputs.Add(i);if(i.Kind==InputKind.KeyDown){Held.Add(i.Code);DownAt=clock.ElapsedMilliseconds;}else{Held.Remove(i.Code);UpAt=clock.ElapsedMilliseconds;}OnPlay?.Invoke(i);}
        public void Release()=>Held.Clear();
    }
}

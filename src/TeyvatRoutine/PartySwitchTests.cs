using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;

namespace TeyvatRoutine;
public static class PartySwitchTests
{
    public static List<string> Run()
    {
        var results=new List<string>();
        void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        void Test(string name,Action test){test();results.Add("PASS  "+name);}
        CombatLoop Settings()=>new(){WaitForThirdPartyReady=true,ThirdPartyReady=new(){Threshold=.8}};
        bool RunGate(Runtime r,CombatLoop c,CancellationToken token=default,Func<bool>? complete=null)
        {
            async Task<bool> Wait(int ms)
            {
                var clock=Stopwatch.StartNew();
                do{token.ThrowIfCancellationRequested();r.Ensure(new());if(complete?.Invoke()==true)return true;
                    if(clock.ElapsedMilliseconds>=ms)return false;await Task.Delay(5,token);
                }while(true);
            }
            return PartySwitchGate.Run(r,c,Wait,_=>{}).GetAwaiter().GetResult();
        }
        Test("Party gate waits at least 500ms then requires two fresh bright frames",()=>
        {
            var r=new Runtime();var clock=Stopwatch.StartNew();long first=0,last=0;
            r.Score=()=>{if(r.Reads==1)first=clock.ElapsedMilliseconds;last=clock.ElapsedMilliseconds;return 1;};
            Check(!RunGate(r,Settings()) && first>=500 && last-first>=60 && r.Reads==2 && r.Releases==1,"Party gate advanced early");
        });
        Test("Party gate rejects a bright flash between dim frames",()=>
        {
            var r=new Runtime();var scores=new Queue<double>(new[]{.2,1,.3,1,1});r.Score=()=>scores.Dequeue();
            Check(!RunGate(r,Settings()) && r.Reads==5,"Transient bright card advanced");
        });
        Test("Party gate timeout stops without sending a swap or skill",()=>
        {
            var r=new Runtime();r.Score=()=>.3;bool stopped=false;var clock=Stopwatch.StartNew();
            try{RunGate(r,Settings());}catch(TimeoutException){stopped=true;}
            Check(stopped && clock.ElapsedMilliseconds>=2500 && clock.ElapsedMilliseconds<4000 && r.Releases==1,"Timeout did not stop");
        });
        Test("Party gate timed-only mode still waits 500ms without image reads",()=>
        {
            var r=new Runtime();var clock=Stopwatch.StartNew();
            Check(!RunGate(r,new()) && clock.ElapsedMilliseconds>=500 && r.Reads==0,"Timed mode lost the minimum wait");
        });
        Test("Completion, cancellation and focus loss interrupt the motion wait and image confirmation",()=>
        {
            foreach(bool afterRead in new[]{false,true})foreach(int mode in new[]{0,1,2})
            {
                var r=new Runtime();using var cancel=new CancellationTokenSource();bool stopped=false;
                void Interrupt(){if(mode==1)cancel.Cancel();if(mode==2)r.Focus=false;}
                if(!afterRead)Interrupt();r.Score=()=>{Interrupt();return 1;};
                try{stopped=RunGate(r,Settings(),cancel.Token,()=>mode==0 && (!afterRead || r.Reads>0));}
                catch(Exception ex)when(ex is OperationCanceledException or InvalidOperationException){stopped=true;}
                Check(stopped && r.Reads==(afterRead?1:0) && r.Releases==1,"Interrupt was delayed or ignored");
            }
        });
        Test("Bright party text survives background changes while dim and blank cards fail",()=>
        {
            using var ready=new Bitmap(160,40);using var image=new Bitmap(160,40);
            using var font=new Font("Arial",22,FontStyle.Bold,GraphicsUnit.Pixel);
            void Draw(Bitmap b,Color background,Color foreground)
            {using var g=Graphics.FromImage(b);g.Clear(background);g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;using var brush=new SolidBrush(foreground);g.DrawString("Kazuha",font,brush,8,6,StringFormat.GenericTypographic);}
            Draw(ready,Color.FromArgb(40,60,80),Color.FromArgb(235,235,235));
            var c=Vision.Crop(ready,new Rectangle(0,0,160,40));c.WhiteForegroundOnly=true;c.WhiteBrightness=200;c.SearchRadius=2;
            foreach(int bg in new[]{40,150,185})
            {
                Draw(image,Color.FromArgb(bg,bg,bg),Color.FromArgb(235,235,235));Check(Vision.Score(image,c)>=.8,"Bright card rejected");
                Draw(image,Color.FromArgb(bg,bg,bg),Color.FromArgb(190,190,190));Check(Vision.Score(image,c)<.8,"Dim card matched");
            }
            using(var g=Graphics.FromImage(image))g.Clear(Color.White);Check(Vision.Score(image,c)<.8,"Blank bright background matched");
        });
        return results;
    }
    private sealed class Runtime:IRuntime
    {
        public int Reads,Releases;public bool Focus=true;
        public Func<double> Score=()=>1;
        public bool Exists=>true;
        public void Ensure(Segment s){if(!Focus)throw new InvalidOperationException("Focus lost");}
        public double Match(Checkpoint c){Reads++;return Score();}
        public void Play(RecordedInput i)=>throw new Exception("Gate must not send inputs");
        public void Release()=>Releases++;
    }
}

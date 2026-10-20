namespace TeyvatRoutine;
public static class ResumeTests
{
    public static List<string> Run()
    {
        var results=new List<string>();void Test(string name,Action a){a();results.Add("PASS  "+name);}void Check(bool ok,string why){if(!ok)throw new Exception(why);}
        Profile ProfileWithImages()
        {
            var p=Profile.Create();p.DomainRuns=3;
            foreach(var s in p.Segments){s.Before=new();s.After=new();}
            var b=p.Get("battle");b.Combat.Enabled=true;b.After!.GoldTextOnly=true;b.Combat.InProgress=new();p.Get("collect").Navigation.Arrival=new();return p;
        }
        Test("Manual resume keeps only remaining selected stages and preserves their round numbers",()=>
        {
            var p=ProfileWithImages();var plan=Workflow.Build(p);int index=plan.FindIndex(i=>i.Segment.Id=="battle" && i.Round==2);
            var rest=ResumePlanner.Continue(plan,index,true);
            Check(rest[0].CombatInProgress && rest[0].Round==2 && rest.Count(i=>i.Segment.Id=="collect")==2 && !rest.Skip(1).Any(i=>i.CombatInProgress),"Wrong remaining rewards or repeated resume flag");
            Check(!plan[index].CombatInProgress && rest.All(i=>i.Segment.Id is not ("login" or "warp_domain" or "enter_domain")),"Original plan changed or previous actions retained");
        });
        Test("Auto resume prioritizes clear and F arrival over common HUD matches",()=>
        {
            var p=ProfileWithImages();var plan=Workflow.Build(p);var battle=p.Get("battle");var collect=p.Get("collect");
            var found=ResumePlanner.Detect(plan,2,c=>c==battle.After || c==battle.Before || c==collect.Navigation.Arrival || c==p.Get("enter_domain").After?1:0);
            Check(found.Count==1 && plan[found[0].Index].Segment.Id=="collect" && plan[found[0].Index].Round==2,"Clear incorrectly replayed the opener or selected wrong round");
        });
        Test("Auto resume recognizes an active battle only through its dedicated image",()=>
        {
            var p=ProfileWithImages();var plan=Workflow.Build(p);var battle=p.Get("battle");
            Check(ResumePlanner.Detect(plan,1,c=>c==battle.Before?1:0).Count==0,"Common Enter inferred active combat");
            var found=ResumePlanner.Detect(plan,3,c=>c==battle.Combat.InProgress?1:0);
            Check(found.Count==1 && found[0].CombatInProgress && plan[found[0].Index].Round==3,"In-progress battle not resumed");
        });
        Test("A reward screen in the last round skips retry and starts the selected next task",()=>
        {
            var p=ProfileWithImages();p.SelectedSteps=new(){"battle","collect","retry","warp_train"};var plan=Workflow.Build(p);
            var found=ResumePlanner.Detect(plan,3,c=>c==p.Get("collect").After?1:0);
            Check(found.Count==1 && plan[found[0].Index].Segment.Id=="warp_train","Last reward incorrectly repeated combat");
        });
        Test("Ambiguous auto matches remain separate candidates instead of guessing",()=>
        {
            var p=ProfileWithImages();var plan=Workflow.Build(p);
            var found=ResumePlanner.Detect(plan,1,c=>c==p.Get("warp_domain").Before || c==p.Get("daily").Before?1:0);
            Check(found.Count==2,"Ambiguity was silently resolved");
            Check(ResumePlanner.Detect(plan,1,_=>.5).Count==0,"Weak matches became candidates");
        });
        Test("Final completion produces an empty remainder and invalid combat resumes are rejected",()=>
        {
            var p=ProfileWithImages();p.SelectedSteps=new(){"daily"};var plan=Workflow.Build(p);
            var found=ResumePlanner.Detect(plan,1,c=>c==p.Get("daily").After?1:0);
            Check(found.Count==1 && ResumePlanner.Continue(plan,found[0].Index).Count==0,"Completed plan replayed input");
            bool rejected=false;try{ResumePlanner.Continue(plan,0,true);}catch(InvalidOperationException){rejected=true;}Check(rejected,"Noncombat resumed as combat");
        });
        return results;
    }
}

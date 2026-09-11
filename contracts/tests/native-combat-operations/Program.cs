#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Assembly bridge=null!;
    private static int checks;
    private static Type Native(string name)=>bridge.GetType("HomeBridge.BridgeTools."+name,true)!;
    private static object Call(string type,string method,params object?[] args)=>Native(type).GetMethod(method,Flags)!.Invoke(null,args)!;
    private static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
    private static object Wire(string json){var parser=bridge.GetType("RimGovernor.Protocol.Operations.AttackTarget",true)!.GetProperty("Parser")!.GetValue(null)!;return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;}
    private static void Main(string[] args)
    {
        var dirs=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=dirs.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        const string entities="\"pawn\":{\"entityId\":\"Human1\",\"expectedSnapshotToken\":\"attacker\"},\"target\":{\"entityId\":\"Hare1\",\"expectedSnapshotToken\":\"target\"}";
        foreach(var mode in new[]{"AUTO","MELEE","RANGED"})
            Check((bool)Call("NativeCombatOperations","Valid",Wire("{"+entities+",\"mode\":\"ATTACK_MODE_"+mode+"\"}")),"Official mode shape accepted before native support guard");
        foreach(var invalid in new[]{"{}","{"+entities+"}","{"+entities+",\"mode\":0}","{"+entities+",\"mode\":99}",("{"+entities+",\"mode\":2}").Replace("Hare1","Human1"),("{"+entities+",\"mode\":2}").Replace("\"expectedSnapshotToken\":\"target\"","\"expectedSnapshotToken\":\"\"")})
            Check(!(bool)Call("NativeCombatOperations","Valid",Wire(invalid)),"Missing exact target/attacker or explicit supported enum refused");
        var cases=new[] {
            ("unrelated death",true,true,true,false,true,false,false,false,"TargetDead"),
            ("unrelated downing requires standing",true,true,false,true,true,false,false,false,"Interrupted"),
            ("downed target permitted still attacking",true,true,false,true,false,true,false,false,"Pending"),
            ("vanished job cannot imply kill",true,true,false,false,true,false,false,false,"Unknown"),
            ("uncorrelated live job",false,true,false,false,true,true,false,false,"Unknown"),
            ("player override",true,false,false,false,true,true,false,false,"Interrupted"),
            ("causal death before later Manual",true,false,true,false,true,false,true,false,"Completed"),
            ("causal downing standing objective",true,true,false,true,true,false,false,true,"Completed"),
            ("causal downing does not finish unrestricted attack",true,true,false,true,false,true,false,true,"Pending"),
            ("ordinary live attack",true,true,false,false,true,true,false,false,"Pending")};
        foreach(var test in cases) {
            var actual=Call("NativeCombatRecord","Classify",test.Item2,test.Item3,test.Item4,test.Item5,test.Item6,test.Item7,test.Rest.Item1,test.Rest.Item2).ToString();
            Check(actual==test.Rest.Item3,test.Item1);
        }
        Check(!(bool)Call("NativeCombatOperations","CombatHealthAllows",Array.Empty<float>(),Array.Empty<bool>()),"Empty colony cannot satisfy combat census");
        foreach(float invalid in new[]{0f,.5005f,float.NaN,float.NegativeInfinity})
            Check(!(bool)Call("NativeCombatOperations","CombatHealthAllows",new[]{1f,invalid},new[]{false,false}),"Every colonist must exceed native health threshold");
        Check((bool)Call("NativeCombatOperations","CombatHealthAllows",new[]{.501f,1f},new[]{false,false}),"Conscious healthy census accepted");
        Check(!(bool)Call("NativeCombatOperations","CombatHealthAllows",new[]{1f,1f},new[]{false,true}),"A downed colonist holds combat");
        var absent=Wire("{"+entities+",\"mode\":2}");var explicitFalse=Wire("{"+entities+",\"mode\":2,\"requireHostile\":false}");
        var presence=absent.GetType().GetProperty("HasRequireHostile")!;
        Check(!(bool)presence.GetValue(absent)! && (bool)presence.GetValue(explicitFalse)!,"Optional guard absence remains distinct in generated presence");
        var game=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp");
        var job=Activator.CreateInstance(game.GetType("Verse.AI.Job",true)!)!;
        var verb=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(game.GetType("Verse.Verb_Shoot",true)!);
        var target=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(game.GetType("Verse.Pawn",true)!);
        Call("NativeCombatOperations","ConfigureRangedJob",job,verb,target);
        Check(ReferenceEquals(job.GetType().GetField("verbToUse")!.GetValue(job),verb),"Ranged forced job pins exact native selected verb");
        Check((bool)job.GetType().GetField("endIfCantShootInMelee")!.GetValue(job)!,"Ranged forced job stops if unable to shoot in melee");
        var targetA=job.GetType().GetField("targetA")!.GetValue(job)!;
        Check(ReferenceEquals(targetA.GetType().GetProperty("Thing")!.GetValue(targetA),target),"Ranged forced job keeps exact pawn target reference");
        Check(!(bool)job.GetType().GetField("endIfCantShootTargetFromCurPos")!.GetValue(job)!,"Forced job does not invent unrelated expiry flags");
        Check((bool)Call("NativeCombatRecord","CausalOrderAllows",4UL,5UL,false),"Flight impact permits exactly admitted order after natural job completion");
        Check(!(bool)Call("NativeCombatRecord","CausalOrderAllows",4UL,6UL,false),"Later order invalidates projectile attribution");
        Check(!(bool)Call("NativeCombatRecord","CausalOrderAllows",4UL,4UL,false),"Unissued pre-order state cannot authorize a later impact");
        Check((bool)Call("NativeCombatRecord","CausalOrderAllows",4UL,4UL,true),"Synchronous native dispatch permits pre-postfix order state");
        Check(!(bool)Call("NativeCombatRecord","CausalOrderAllows",ulong.MaxValue,0UL,true),"Exhausted order revision cannot wrap into attribution");
        foreach(var test in new[]{("Pending","Unknown"),("TargetDead","Unknown"),("Completed","Completed"),("Interrupted","Interrupted")}) {
            var phase=Enum.Parse(Native("NativeCombatPhase"),test.Item1);
            Check(Call("NativeCombatRecord","WithTrackingLoss",phase,true).ToString()==test.Item2,"Tracking loss preserves only prior terminal attribution or observed interruption: "+test.Item1);
            Check(Call("NativeCombatRecord","WithTrackingLoss",phase,false).ToString()==test.Item1,"Intact lineage preserves native phase: "+test.Item1);
        }
        Console.WriteLine($"{checks} compiled combat shape, progress and health guard assertions passed; no gameplay.");
    }
}

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
    private static object New(string type,params object?[] args)=>Activator.CreateInstance(Native(type),Flags,null,args,null)!;
    private static void Field(object obj,string name,object value)=>obj.GetType().GetField(name,Flags)!.SetValue(obj,value);
    private static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
    private static object Wire(string type,string json){var parser=bridge.GetType("RimGovernor.Protocol."+type,true)!.GetProperty("Parser")!.GetValue(null)!;return parser.GetType().GetMethod("ParseJson")!.Invoke(parser,new object[]{json})!;}
    private static void Main(string[] args)
    {
        var dirs=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=dirs.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        const string pawn="\"pawn\":{\"entityId\":\"Human1\",\"expectedSnapshotToken\":\"token\"}";
        foreach(var invalid in new[]{"{}","{"+pawn+"}","{"+pawn+",\"destination\":{\"x\":0}}","{\"pawn\":{\"entityId\":\"Human1\"},\"destination\":{\"x\":0,\"z\":0}}"})
            Check(!(bool)Call("NativeMovementOperations","Valid",Wire("Operations.MovePawn",invalid)),"Missing explicit coordinates/CAS refused");
        Check((bool)Call("NativeMovementOperations","Valid",Wire("Operations.MovePawn","{"+pawn+",\"destination\":{\"x\":0,\"z\":0}}")),"Explicit origin accepted as shape");
        var owner=Wire("Authority.Owner","{\"controllerSessionId\":\"owner\",\"playerDirection\":\"1\"}");
        var other=Wire("Authority.Owner","{\"controllerSessionId\":\"owner\",\"playerDirection\":\"2\"}");
        var facts=New("NativePawnFacts");Field(facts,"Spawned",true);Field(facts,"PlayerControlled",true);Field(facts,"Drafted",true);
        Field(facts,"Drafter",System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
            AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp").GetType("RimWorld.Pawn_DraftController",true)!));
        var claim=New("NativeDraftClaim","claim",owner);var snapshot=New("NativePawnSnapshot","token",facts,claim);
        Check((bool)Call("NativeMovementOperations","Owns",snapshot,owner),"Exact eligible owner accepted");
        Check(!(bool)Call("NativeMovementOperations","Owns",snapshot,other),"New direction cannot adopt prior claim");
        Check(!(bool)Call("NativeMovementOperations","Owns",New("NativePawnSnapshot","token",facts,null),owner),"Unowned player draft refused");
        foreach(var field in new[]{"Dead","Downed","Mental"}){Field(facts,field,true);Check(!(bool)Call("NativeMovementOperations","Owns",snapshot,owner),field+" cannot move");Field(facts,field,false);}
        Field(facts,"Drafted",false);Check(!(bool)Call("NativeMovementOperations","Owns",snapshot,owner),"Move cannot auto-draft");
        var cases=new[] {
            ("uncorrelated arrival",false,true,false,false,false,true,"Unknown"),
            ("revoked after arrival",true,false,false,false,true,true,"Interrupted"),
            ("player replaced running job",true,false,true,false,true,false,"Interrupted"),
            ("queued while pawn already at target",true,true,false,true,false,true,"Pending"),
            ("queued after prior observation",true,true,false,true,true,true,"Pending"),
            ("running en route",true,true,true,false,true,false,"Pending"),
            ("started job reached exact cell",true,true,true,false,true,true,"Completed"),
            ("automatic transition at exact cell",true,true,false,false,true,true,"Completed"),
            ("vanished away from destination",true,true,false,false,true,false,"Unknown"),
            ("vanished before ever starting",true,true,false,false,false,false,"Unknown"),
            ("another job arrived before queued job began",true,true,false,false,false,true,"Unknown")};
        foreach(var test in cases) {
            var actual=Call("NativeMovementRecord","Classify",test.Item2,test.Item3,test.Item4,test.Item5,test.Item6,test.Item7).ToString();
            Check(actual==test.Rest.Item1,test.Item1);
        }
        Console.WriteLine($"{checks} compiled movement contract/ownership/progress assertions passed; no gameplay.");
    }
}

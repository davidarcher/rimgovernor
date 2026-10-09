#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class NativeMovementOperationsProbe
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Assembly bridge=null!;
    private static int checks;
    private static Type Native(string name)=>bridge.GetType("HomeBridge.BridgeTools."+name,true)!;
    private static object Call(string type,string method,params object?[] args)=>Native(type).GetMethod(method,Flags)!.Invoke(null,args)!;
    private static object New(string type,params object?[] args)=>Activator.CreateInstance(Native(type),Flags,null,args,null)!;
    private static void Field(object obj,string name,object value)=>obj.GetType().GetField(name,Flags)!.SetValue(obj,value);
    private static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
    internal static void Invoke(string[] args)
    {
        var dirs=args.Skip(1).Concat(new[]{Path.GetDirectoryName(Path.GetFullPath(args[0]))!}).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{var path=dirs.Select(d=>Path.Combine(d,new AssemblyName(e.Name).Name+".dll")).FirstOrDefault(File.Exists);return path==null?null:Assembly.LoadFrom(path);};
        bridge=Assembly.LoadFrom(Path.GetFullPath(args[0]));foreach(var reference in bridge.GetReferencedAssemblies())Assembly.Load(reference);
        // Drafts are plan-owned: Owns() is an eligible drafted pawn, with no native claim.
        var facts=New("NativePawnFacts");Field(facts,"Spawned",true);Field(facts,"PlayerControlled",true);Field(facts,"Drafted",true);
        Field(facts,"Drafter",System.Runtime.Serialization.FormatterServices.GetUninitializedObject(
            AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Assembly-CSharp").GetType("RimWorld.Pawn_DraftController",true)!));
        var snapshot=New("NativePawnSnapshot","token",facts);
        Check((bool)Call("NativeMovementOperations","Owns",snapshot),"Eligible drafted pawn accepted");
        foreach(var field in new[]{"Dead","Downed","Mental"}){Field(facts,field,true);Check(!(bool)Call("NativeMovementOperations","Owns",snapshot),field+" cannot move");Field(facts,field,false);}
        Field(facts,"Drafted",false);Check(!(bool)Call("NativeMovementOperations","Owns",snapshot),"Move cannot auto-draft");
        Console.WriteLine($"{checks} compiled movement ownership assertions passed; no gameplay.");
    }
}

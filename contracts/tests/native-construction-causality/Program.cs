using System;
using HomeBridge.BridgeTools;

internal static class Program
{
    private static int checks;
    private static void Check(bool value,string name) { checks++; if(!value)throw new Exception(name); }
    private static void Main()
    {
        object output;
        var frame=new SameLookingThing(); var other=new SameLookingThing();
        Check(frame.Equals(other),"fixture has equal-looking objects");
        var scope=new NativeConstructionCausality();
        scope.Created(frame);
        Check(!scope.TryComplete(true,null,out output),"unspawned factory result is not completed");
        scope.Spawned(other,other);
        Check(!scope.TryComplete(true,null,out output),"same-looking independent spawn cannot replace exact factory result");
        scope.Spawned(frame,frame);
        Check(scope.TryComplete(true,null,out output) && ReferenceEquals(output,frame),"exact created/spawned object establishes successor");
        Check(!scope.TryComplete(false,null,out output),"predecessor must have been consumed");
        Check(!scope.TryComplete(true,new Exception(),out output),"native failure remains uncertain despite observed spawn");
        var replaced=new NativeConstructionCausality(); replaced.Created(frame); replaced.Spawned(frame,other);
        Check(!replaced.TryComplete(true,null,out output),"replaced spawn result is not causal identity");
        var ambiguous=new NativeConstructionCausality(); ambiguous.Created(frame); ambiguous.Created(other); ambiguous.Spawned(frame,frame);
        Check(!ambiguous.TryComplete(true,null,out output),"multiple relevant factory results remain ambiguous");
        var parent=new NativeConstructionCausality(); var child=new NativeConstructionCausality();
        parent.Created(frame); child.Created(other); child.Spawned(other,other);
        Check(!parent.TryComplete(true,null,out output),"separate nested scope cannot finish parent");
        Check(child.TryComplete(true,null,out output)&&ReferenceEquals(output,other),"nested scope retains own successor");
        var duplicate=new NativeConstructionCausality(); duplicate.Created(frame); duplicate.Created(frame);
        duplicate.Spawned(frame,frame); duplicate.Spawned(frame,frame);
        Check(duplicate.TryComplete(true,null,out output),"duplicate observer callbacks preserve exact identity");
        Console.WriteLine("Construction causality passed "+checks+" assertions; exact-reference production helper, no native gameplay.");
    }
    private sealed class SameLookingThing
    {
        public override bool Equals(object obj) => obj is SameLookingThing;
        public override int GetHashCode() => 1;
    }
}

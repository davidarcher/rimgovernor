using System;
using RimGovernor.Native;

internal static class NativeGameThreadProbe {
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    internal static void Invoke() {
        Check(!GameThread.IsIdentifier("\ud800"),"identifier surrogate");
        Check(!GameThread.IsIdentifier("a\0b"),"identifier NUL");
        Check(GameThread.IsIdentifier(new string('é',128)),"identifier UTF8 bound");
        Check(!GameThread.IsIdentifier(new string('é',129)),"identifier UTF8 over");
        RimGovernor.Brain.GameFacts.Common.ObservationContext? context; RimGovernor.Brain.GameFacts.Common.Unavailable? unavailable;
        Check(!GameThread.TryReadContext(null,out context,out unavailable) && unavailable.Reason==RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotLoaded,"unloaded read does not create authority");
        var game = new Verse.Game { Identity = new ColonyIdentity { ColonyId="colony",LoadToken="load" } };
        var map = new Verse.Map { uniqueID=1 };
        Verse.Current.Game=game; Verse.Find.CurrentMap=map; Verse.Find.TickManager=new Verse.TickManager { TicksGame=12 };
        NativeControlAuthority? authority;
        Check(!NativeControlAuthority.TryGetForGame(game,out authority),"new game no authority");
        Check(GameThread.TryReadContext(map,out context,out unavailable) && context. NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase,"read with absent authority leaves generation absent");
        Check(!NativeControlAuthority.TryGetForGame(game,out authority),"read does not allocate authority");
        var owner=NativeControlAuthority.ForGame(game);
        Check(NativeControlAuthority.TryGetForGame(game,out authority) && ReferenceEquals(owner,authority),"existing authority identity preserved");
        Check(GameThread.TryReadContext(map,out context,out unavailable) && context.NativeGeneration.IsAt(owner.Status().Generation),"read reports existing owner generation");
        Check(!NativeControlAuthority.TryGetForGame(new Verse.Game(),out authority),"another game isolated");
        Check(!NativeControlAuthority.TryGetForGame(null,out authority),"null game unavailable");
        Console.WriteLine("Boundary probe passed: "+checks+" checks; identifier and journal/game seams; no gameplay acceptance.");
    }
}

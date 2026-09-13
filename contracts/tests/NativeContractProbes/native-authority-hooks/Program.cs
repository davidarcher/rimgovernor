using System;
using System.Threading;
using HarmonyLib;
using HomeBridge.BridgeTools;
using RimWorld;
using Verse;
using Verse.AI;

internal static class NativeAuthorityHooksProbe
{
    private static int checks;
    private static void Assert(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    private static NativeControlAuthority State = null!;
    private static void Acquire()
    {
        if (State.Status().Active) State.Revoke(State.Status().Generation, NativeControlRevocationReason.Manual);
        Assert(State.Acquire(State.Status().Generation, "controller", 1, 30000).Success, "acquire");
    }
    private static void Invalidates(Action action, string label)
    {
        Acquire(); var before = State.Status(); action(); var after = State.Status();
        Assert(!after.Active && after.Generation > before.Generation, label);
    }
    private static void Preserves(Action action, string label)
    {
        Acquire(); var before = State.Status(); action(); var after = State.Status();
        Assert(after.Active && after.Generation == before.Generation, label);
    }
    internal static void Invoke()
    {
        _ = UnityData.IsInMainThread;
        Assert(NativeAuthorityHooks.Health.Ready && NativeAuthorityHooks.Health.VerifiedTargets == 21, "all exact patches installed");
        Assert(NativeAuthorityHooks.InitializeForCurrentGame() == null, "no game initializes nothing");
        var game = new Game { CurrentMap = new Map { uniqueID = 1 } }; Current.Game = game;
        Assert(!NativeControlAuthority.TryGetForGame(game, out _), "setter allocated state");
        game.UpdatePlay(); Assert(NativeControlAuthority.TryGetForGame(game, out var state), "poll initialized state"); State = state!;
        Assert(State.Status().Available && !State.Status().Active, "poll granted authority");
        var jobs = new Pawn_JobTracker();
        Invalidates(() => jobs.TryTakeOrderedJob(new Job()), "successful job");
        Preserves(() => jobs.TryTakeOrderedJob(new Job { Accepted = false }), "failed job");
        var draft = new Pawn_DraftController();
        var work = new Pawn_WorkSettings(); var workType = new WorkTypeDef();
        Invalidates(() => work.SetPriority(workType, 1), "work priority change");
        Preserves(() => work.SetPriority(workType, 1), "work priority noop");
        Preserves(() => { using (State.Owned()) work.SetPriority(workType, 2); }, "owned work priority change");
        var zone=new Zone_Growing(); var crop=new ThingDef();
        Invalidates(()=>zone.SetPlantDefToGrow(crop),"crop change");
        Preserves(()=>zone.SetPlantDefToGrow(crop),"crop noop");
        Preserves(()=>{using(State.Owned())zone.SetPlantDefToGrow(new ThingDef());},"owned crop change");
        Invalidates(()=>zone.AddCell(default),"zone cell added");
        Preserves(()=>zone.AddCell(default),"zone cell noop");
        Invalidates(()=>zone.RemoveCell(default),"zone cell removed");
        var zones=new ZoneManager(); zones.AllZones.Add(zone);
        Invalidates(()=>zones.DeregisterZone(zone),"zone removed");
        Invalidates(()=>new Command_Toggle().ProcessInput(new UnityEngine.Event()),"player gizmo toggle");
        Invalidates(() => draft.Drafted = true, "draft change");
        Preserves(() => draft.Drafted = true, "draft noop");
        Invalidates(() => draft.Drafted = false, "undraft change");
        Action build = () => GenConstruct.PlaceBlueprintForBuild(new(), default, game.CurrentMap!, default, Faction.OfPlayer, new());
        Invalidates(build, "build");
        Invalidates(() => GenConstruct.PlaceBlueprintForInstall(new(), default, game.CurrentMap!, default, Faction.OfPlayer), "install");
        Invalidates(() => GenConstruct.PlaceBlueprintForReinstall(new(), default, game.CurrentMap!, default, Faction.OfPlayer), "reinstall");
        Preserves(() => GenConstruct.PlaceBlueprintForBuild(new(), default, game.CurrentMap!, default, new Faction(), new()), "nonplayer build");
        var cancel = new Designator_Cancel();
        Invalidates(() => cancel.DesignateThing(new Blueprint_Build()), "cancel blueprint");
        Preserves(() => cancel.DesignateThing(new Thing()), "cancel noop");
        game.CurrentMap!.designationManager.Cell.Add(new Designation());
        Invalidates(() => cancel.DesignateSingleCell(default), "cancel cell designation");
        Preserves(() => cancel.DesignateSingleCell(default), "cancel empty cell");
        game.CurrentMap.designationManager.Thing.Add(new Designation());
        Invalidates(() => cancel.DesignateThing(new Thing()), "cancel thing designation");
        var stack = new BillStack();
        var billProd = new Bill_Production();
        var dialog = new Dialog_BillConfig(billProd);
        Invalidates(() => stack.AddBill(billProd), "bill added");
        Preserves(() => { using (State.Owned()) stack.AddBill(new Bill_Production()); }, "owned bill added");
        Invalidates(() => stack.Delete(billProd), "bill deleted");
        Invalidates(() => stack.Reorder(billProd, 1), "bill reordered");
        billProd.interfaceAction = () => billProd.suspended = !billProd.suspended;
        Invalidates(() => billProd.DoInterface(0, 0, 0, 0), "bill suspend toggled via row");
        billProd.interfaceAction = null;
        Preserves(() => billProd.DoInterface(0, 0, 0, 0), "bill row noop");
        dialog.editAction = () => billProd.repeatCount = 7;
        Invalidates(() => dialog.DoWindowContents(default), "bill config repeat count changed");
        dialog.editAction = () => billProd.targetCount = 12;
        Invalidates(() => dialog.DoWindowContents(default), "bill config target count changed");
        dialog.editAction = () => billProd.SetStoreMode(new BillStoreModeDef(), null);
        Invalidates(() => dialog.DoWindowContents(default), "bill config store mode changed");
        dialog.editAction = () => billProd.ingredientFilter.Summary = "changed";
        Invalidates(() => dialog.DoWindowContents(default), "bill config ingredient filter changed");
        dialog.editAction = null;
        Preserves(() => dialog.DoWindowContents(default), "bill config noop");
        dialog.editAction = () => billProd.repeatCount = 99;
        Preserves(() => { using (State.Owned()) dialog.DoWindowContents(default); }, "owned bill config change");
        dialog.editAction = null;
        Preserves(() => { using (State.Owned()) using (State.Owned()) { build(); jobs.TryTakeOrderedJob(new Job()); draft.Drafted = true; } }, "owned work");
        Invalidates(() => { try { using (State.Owned()) throw new Exception(); } catch { } build(); }, "exception disposes suppression");
        var map = game.CurrentMap;
        Invalidates(() => { game.CurrentMap = new Map { uniqueID = 2 }; game.CurrentMap = map; }, "map away and back");
        Preserves(() => game.CurrentMap = map, "same map");
        Invalidates(() => { using (State.Owned()) { game.CurrentMap = null; game.CurrentMap = map; Assert(!State.IsOwned, "context bypassed owned scope"); } }, "owned context invalidation");
        Invalidates(() => { var worker = new Thread(() => { Current.Game = null; Current.Game = game; }); worker.Start(); worker.Join(); }, "loader game away and back");
        Preserves(() => game.UpdatePlay(), "ordinary poll not Manual");
        var replacement = new Game { CurrentMap = map }; Current.Game = replacement; replacement.UpdatePlay();
        Assert(NativeControlAuthority.TryGetForGame(replacement, out var next) && !ReferenceEquals(next, State) && !next!.Status().Active, "new game leaked authority");
        Current.Game = game; game.UpdatePlay(); Acquire();
        new Harmony("rimgovernor.native.authority").Unpatch(AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob"), HarmonyPatchType.Postfix, "rimgovernor.native.authority");
        Assert(!NativeAuthorityHooks.Health.Ready, "unpatch not detected");
        NativeAuthorityHooks.InitializeForCurrentGame();
        Assert(!State.Status().Available && !State.Status().Active && State.Status().Reason == NativeControlRevocationReason.HooksUnavailable, "hook loss did not fail closed");
        Console.WriteLine($"Native authority hooks: {checks} checks (actual Harmony, controlled native-shaped fixtures).");
    }
}

using System;
using HomeBridge.BridgeTools;
using Operations = RimGovernor.Protocol.Operations;

internal static class NativeProductionBillSettingsProbe
{
    public static void Invoke()
    {
        var bill = new Operations.AddBill {
            Bench = new Operations.EntityPrecondition { EntityId = "tailor", ExpectedSnapshotToken = "token" },
            RecipeDef = "Make_Apparel_BasicShirt",
            Settings = new Operations.BillSettings {
                RepeatMode = Operations.RepeatMode.Target, TargetCount = 1, UnpauseThreshold = 1,
                PauseWhenSatisfied = true, Suspended = false, IngredientSearchRadius = 40,
                Store = new Operations.BillStore { Mode = Operations.StoreMode.DropOnFloor }
            }
        };
        Check(NativeProductionBillSettings.Valid(bill), "recipe defaults rejected");
        var filter = new Operations.FilterPatch { Replace = new Operations.SelectorList() };
        filter.Replace.Selectors.Add(new Operations.FilterSelector { ThingDef = "Leather_Plain" });
        bill.Settings.Ingredients = filter;
        Check(NativeProductionBillSettings.Valid(bill), "exact leather filter rejected");
        foreach (var change in new Action<Operations.FilterPatch>[] {
            f => f.Replace = null,
            f => f.Replace.Selectors.Clear(),
            f => f.Replace.Selectors.Add(new Operations.FilterSelector { ThingDef = "Leather_Plain" }),
            f => f.Replace.Selectors[0] = new Operations.FilterSelector { CategoryDef = "Leathers" },
            f => f.Allow.Add(new Operations.FilterSelector { ThingDef = "Cloth" }),
            f => f.Disallow.Add(new Operations.FilterSelector { ThingDef = "Cloth" }),
            f => f.HitPointsMin = 0,
            f => f.Replace.Selectors[0] = new Operations.FilterSelector { ThingDef = "" }
        }) {
            var invalid = bill.Clone();
            change(invalid.Settings.Ingredients);
            Check(!NativeProductionBillSettings.Valid(invalid), "unsupported filter accepted: " + invalid.Settings.Ingredients);
        }
        var butcher = bill.Clone();
        butcher.RecipeDef = "ButcherCorpseFlesh";
        butcher.Settings = new Operations.BillSettings { RepeatMode = Operations.RepeatMode.Forever,
            Suspended = false, IngredientSearchRadius = 40,
            Store = new Operations.BillStore { Mode = Operations.StoreMode.DropOnFloor } };
        Check(NativeProductionBillSettings.Valid(butcher), "unfiltered butcher bill rejected" );
        butcher.ReplaceOwnedBillId = "foreign-bill";
        Check(NativeProductionBillSettings.Valid(butcher), "butcher takeover rejected" );
        butcher.Settings.Worker = new Operations.Assignment { EntityId = "Pawn_Cook" };
        Check(NativeProductionBillSettings.Valid(butcher), "pinned human butcher rejected");
        var invalidWorker = butcher.Clone();
        invalidWorker.Settings.Worker.EntityId = "";
        Check(!NativeProductionBillSettings.Valid(invalidWorker), "empty worker accepted");
        invalidWorker = bill.Clone();
        invalidWorker.Settings.Worker = butcher.Settings.Worker.Clone();
        Check(!NativeProductionBillSettings.Valid(invalidWorker), "worker accepted outside human butchery");
        butcher.Settings.Ingredients = filter;
        Check(!NativeProductionBillSettings.Valid(butcher), "butcher ingredient override accepted");
        Console.WriteLine("Native production bill settings checks passed.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

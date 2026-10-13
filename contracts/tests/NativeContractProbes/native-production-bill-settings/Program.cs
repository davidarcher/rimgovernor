using System;
using HomeBridge.BridgeTools;
using Operations = RimGovernor.Contracts.Wire.Operations;

internal static class NativeProductionBillSettingsProbe
{
    public static void Invoke()
    {
        // The game's two corpse recipes as rows: the butcher counter, and a corpse-consuming
        // recipe that makes nothing (NativeRecipeRoles reads these, never a defName).
        var corpse = new Verse.ThingDef { defName = "Corpse_Human", IsCorpse = true };
        var corpseIngredient = new RimWorld.IngredientCount();
        corpseIngredient.filter.AllowedThingDefs.Add(corpse);
        var butcherRecipe = new RimWorld.RecipeDef { defName = "ButcherCorpseFlesh", WorkerCounter = new RimWorld.RecipeWorkerCounter_ButcherAnimals(),
            specialProducts = new System.Collections.Generic.List<RimWorld.SpecialProductType> { RimWorld.SpecialProductType.Butchery } };
        butcherRecipe.ingredients.Add(corpseIngredient);
        Verse.DefDatabase<RimWorld.RecipeDef>.Named[butcherRecipe.defName] = butcherRecipe;
        var bill = new Operations.ProductionBillIntent {
            BenchId = "tailor",
            RecipeDef = "Make_Apparel_BasicShirt",
            Settings = new Operations.BillSettings {
                RepeatMode = Operations.RepeatMode.Target, TargetCount = 1, UnpauseThreshold = 1,
                PauseWhenSatisfied = true, Suspended = false, IngredientSearchRadius = 40,
                Store = DropOnFloor()
            }
        };
        Check(NativeProductionBillSettings.Valid(bill), "recipe defaults rejected");
        // A finite batch (gear, sculpture) repeats a count, with no target.
        var batch = bill with { Settings = new Operations.BillSettings { RepeatMode = Operations.RepeatMode.Count, RepeatCount = 2,
            Suspended = false, IngredientSearchRadius = 40,
            Store = DropOnFloor() } };
        Check(NativeProductionBillSettings.Valid(batch), "finite batch rejected");
        batch = Settings(batch, s => s with { TargetCount = 2 });
        Check(!NativeProductionBillSettings.Valid(batch), "finite batch with a target accepted");
        batch = Settings(batch, s => s with { TargetCount = null });
        // A batch may pin one artist.
        batch = Settings(batch, s => s with { Worker = Worker("Pawn_Artist") });
        Check(NativeProductionBillSettings.Valid(batch), "pinned finite batch rejected");
        batch = Settings(batch, s => s with { Worker = Worker("") });
        Check(!NativeProductionBillSettings.Valid(batch), "finite batch with an empty worker accepted");
        batch = Settings(batch, s => s with { Worker = null });
        var filter = new Operations.FilterPatch { Replace = new Operations.SelectorList { Selectors = [ThingDef("Leather_Plain")] } };
        bill = Settings(bill, s => s with { Ingredients = filter });
        Check(NativeProductionBillSettings.Valid(bill), "exact leather filter rejected");
        foreach (var change in new Func<Operations.FilterPatch, Operations.FilterPatch>[] {
            f => f with { Replace = null },
            f => f with { Replace = f.Replace with { Selectors = [] } },
            f => f with { Replace = f.Replace with { Selectors = [.. f.Replace.Selectors, ThingDef("Leather_Plain")] } },
            f => f with { Replace = f.Replace with { Selectors = [new Operations.FilterSelector { Definition = new Operations.FilterSelectorDefinition.CategoryDefCase("Leathers") }] } },
            f => f with { Allow = [.. f.Allow, ThingDef("Cloth")] },
            f => f with { Disallow = [.. f.Disallow, ThingDef("Cloth")] },
            f => f with { HitPointsMin = 0 },
            f => f with { Replace = f.Replace with { Selectors = [ThingDef("")] } }
        }) {
            var invalid = Settings(bill, s => s with { Ingredients = change(s.Ingredients) });
            Check(!NativeProductionBillSettings.Valid(invalid), "unsupported filter accepted: " + invalid.Settings.Ingredients);
        }
        var butcher = bill with { RecipeDef = "ButcherCorpseFlesh" };
        butcher = butcher with { Settings = new Operations.BillSettings { RepeatMode = Operations.RepeatMode.Forever,
            Suspended = false, IngredientSearchRadius = 40,
            Store = DropOnFloor() } };
        Check(!NativeProductionBillSettings.Valid(butcher), "butcher bill without a corpse class accepted");
        butcher = Settings(butcher, s => s with { CorpseClass = RimGovernor.Contracts.Wire.Common.CorpseClass.Animal });
        Check(NativeProductionBillSettings.Valid(butcher), "animal butcher bill rejected" );
        butcher = butcher with { ReplaceOwnedBill = new RimGovernor.Contracts.Wire.Common.Ref { Id = "foreign-bill" } };
        Check(NativeProductionBillSettings.Valid(butcher), "butcher takeover rejected" );
        var classedMeal = Settings(bill, s => s with { CorpseClass = RimGovernor.Contracts.Wire.Common.CorpseClass.Animal });
        Check(!NativeProductionBillSettings.Valid(classedMeal), "corpse class accepted outside corpse recipes");
        Check(!NativeProductionBillSettings.Valid(Settings(butcher, s => s with { Worker = Worker("Pawn_Cook") })), "pinned animal butcher accepted");
        butcher = Settings(butcher, s => s with { CorpseClass = RimGovernor.Contracts.Wire.Common.CorpseClass.Stranger, Worker = Worker("Pawn_Cook") });
        Check(NativeProductionBillSettings.Valid(butcher), "pinned human butcher rejected");
        var invalidWorker = Settings(butcher, s => s with { Worker = Worker("") });
        Check(!NativeProductionBillSettings.Valid(invalidWorker), "empty worker accepted");
        invalidWorker = Settings(bill, s => s with { Worker = butcher.Settings.Worker });
        Check(!NativeProductionBillSettings.Valid(invalidWorker), "worker accepted outside human butchery");
        butcher = Settings(butcher, s => s with { Ingredients = filter });
        Check(!NativeProductionBillSettings.Valid(butcher), "butcher ingredient override accepted");
        Console.WriteLine("Native production bill settings checks passed.");
    }
    // Records are immutable: each change builds a modified copy.
    private static Operations.ProductionBillIntent Settings(Operations.ProductionBillIntent bill, Func<Operations.BillSettings, Operations.BillSettings> change) => bill with { Settings = change(bill.Settings) };
    private static Operations.BillStore DropOnFloor() => new Operations.BillStore { Destination = new Operations.BillStoreDestination.ModeCase(Operations.StoreMode.DropOnFloor) };
    private static Operations.Assignment Worker(string entityId) => new Operations.Assignment { Value = new Operations.AssignmentValue.EntityIdCase(entityId) };
    private static Operations.FilterSelector ThingDef(string def) => new Operations.FilterSelector { Definition = new Operations.FilterSelectorDefinition.ThingDefCase(def) };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

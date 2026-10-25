using System;
using RimGovernor.Native;

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
        var bill = new RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent {
            BenchId = "tailor",
            RecipeDef = "Make_Apparel_BasicShirt",
            Settings = new RimGovernor.Brain.GameOrders.Operations.BillSettings {
                Repetition = new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Target(1, 1), BeerReserve = false, Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable()
            }, AcknowledgeViolation = false, PartIndex = new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.WholeBody() };
        Check(NativeProductionBillSettings.Valid(bill), "recipe defaults rejected");
        // A finite batch (gear, sculpture) repeats a count, with no target.
        var batch = bill with { Settings = new RimGovernor.Brain.GameOrders.Operations.BillSettings { Repetition = new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count(2), BeerReserve = false, Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable() } };
        Check(NativeProductionBillSettings.Valid(batch), "finite batch rejected");
        batch = Settings(batch, s => s with { Repetition = new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count(0) });
        Check(!NativeProductionBillSettings.Valid(batch), "empty finite batch accepted");
        batch = Settings(batch, s => s with { Repetition = new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count(2) });
        // A batch may pin one artist.
        batch = Settings(batch, s => s with { Worker = Worker("Pawn_Artist") });
        Check(NativeProductionBillSettings.Valid(batch), "pinned finite batch rejected");
        batch = Settings(batch, s => s with { Worker = Worker("") });
        Check(!NativeProductionBillSettings.Valid(batch), "finite batch with an empty worker accepted");
        batch = Settings(batch, s => s with { Worker = null });
        var filter = new RimGovernor.Brain.GameOrders.Operations.FilterPatch { Replace = new RimGovernor.Brain.GameOrders.Operations.SelectorList { Selectors = [ThingDef("Leather_Plain")] }, HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve() };
        bill = Settings(bill, s => s with { Ingredients = filter });
        Check(NativeProductionBillSettings.Valid(bill), "exact leather filter rejected");
        foreach (var change in new Func<RimGovernor.Brain.GameOrders.Operations.FilterPatch, RimGovernor.Brain.GameOrders.Operations.FilterPatch>[] {
            f => f with { Replace = null },
            f => f with { Replace = (f.Replace ?? throw new InvalidOperationException("filter fixture has no replacement")) with { Selectors = [] } },
            f => f with { Replace = (f.Replace ?? throw new InvalidOperationException("filter fixture has no replacement")) with { Selectors = [.. (f.Replace ?? throw new InvalidOperationException("filter fixture has no replacement")).Selectors, ThingDef("Leather_Plain")] } },
            f => f with { Replace = (f.Replace ?? throw new InvalidOperationException("filter fixture has no replacement")) with { Selectors = [new RimGovernor.Brain.GameOrders.Operations.FilterSelector { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.CategoryDefCase("Leathers") }] } },
            f => f with { Allow = [.. f.Allow, ThingDef("Cloth")] },
            f => f with { Disallow = [.. f.Disallow, ThingDef("Cloth")] },
            f => f with { HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Selected(0, 1) },
            f => f with { Replace = (f.Replace ?? throw new InvalidOperationException("filter fixture has no replacement")) with { Selectors = [ThingDef("")] } }
        }) {
            var invalid = Settings(bill, s => s with { Ingredients = change(s.Ingredients ?? throw new InvalidOperationException("bill fixture has no ingredients")) });
            Check(!NativeProductionBillSettings.Valid(invalid), "unsupported filter accepted: " + (invalid.Settings ?? throw new InvalidOperationException("bill fixture has no settings")).Ingredients);
        }
        var butcher = bill with { RecipeDef = "ButcherCorpseFlesh" };
        butcher = butcher with { Settings = new RimGovernor.Brain.GameOrders.Operations.BillSettings { Repetition = new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Forever(), BeerReserve = false, Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable() } };
        Check(!NativeProductionBillSettings.Valid(butcher), "butcher bill without a corpse class accepted");
        butcher = Settings(butcher, s => s with { Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(RimGovernor.Brain.GameFacts.Common.CorpseClass.Animal) });
        Check(NativeProductionBillSettings.Valid(butcher), "animal butcher bill rejected" );
        butcher = butcher with { ReplaceOwnedBill = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "foreign-bill" } };
        Check(NativeProductionBillSettings.Valid(butcher), "butcher takeover rejected" );
        var classedMeal = Settings(bill, s => s with { Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(RimGovernor.Brain.GameFacts.Common.CorpseClass.Animal) });
        Check(!NativeProductionBillSettings.Valid(classedMeal), "corpse class accepted outside corpse recipes");
        Check(!NativeProductionBillSettings.Valid(Settings(butcher, s => s with { Worker = Worker("Pawn_Cook") })), "pinned animal butcher accepted");
        butcher = Settings(butcher, s => s with { Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(RimGovernor.Brain.GameFacts.Common.CorpseClass.Stranger), Worker = Worker("Pawn_Cook") });
        Check(NativeProductionBillSettings.Valid(butcher), "pinned human butcher rejected");
        var invalidWorker = Settings(butcher, s => s with { Worker = Worker("") });
        Check(!NativeProductionBillSettings.Valid(invalidWorker), "empty worker accepted");
        invalidWorker = Settings(bill, s => s with { Worker = (butcher.Settings ?? throw new InvalidOperationException("butcher fixture has no settings")).Worker });
        Check(!NativeProductionBillSettings.Valid(invalidWorker), "worker accepted outside human butchery");
        butcher = Settings(butcher, s => s with { Ingredients = filter });
        Check(!NativeProductionBillSettings.Valid(butcher), "butcher ingredient override accepted");
        Console.WriteLine("Native production bill settings checks passed.");
    }
    // Records are immutable: each change builds a modified copy.
    private static RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent Settings(RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent bill, Func<RimGovernor.Brain.GameOrders.Operations.BillSettings, RimGovernor.Brain.GameOrders.Operations.BillSettings> change) => bill with { Settings = change(bill.Settings ?? throw new InvalidOperationException("bill fixture has no settings")) };
    private static RimGovernor.Brain.GameOrders.Operations.Assignment Worker(string entityId) => new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase(entityId) };
    private static RimGovernor.Brain.GameOrders.Operations.FilterSelector ThingDef(string def) => new RimGovernor.Brain.GameOrders.Operations.FilterSelector { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.ThingDefCase(def) };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

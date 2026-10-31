using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/ordinary-cooking</c> (native-led-labor-execution task 5.1): ordinary food production is native. Beside a staged
/// fueled stove without bills and a stack of raw rice, the served bill, cooking and work families create a FoodTarget
/// (vanilla Target-mode) cooking bill and enable cooking in a colonist's work settings; native pawns then cook by themselves,
/// with no exact order, and usable meals appear in the colony stock.
/// </summary>
internal static class FoodOrdinaryCooking
{
    private const string Stove = "stove";

    internal static TypedCase Case() => new()
    {
        Name = "food/ordinary-cooking",
        Scope = "Beside a staged FueledStove with no bills, 60 RawRice and a meal stockpile, the served bill, cooking and work "
            + "families create a Target-mode (FoodTarget) cooking bill on the stove and leave Cooking enabled in a colonist's work "
            + "settings; native work then cooks without any exact pawn order, and the colony facts' stock gains usable meals. "
            + "A unit test proves the bill and work decisions and that no forced dispatch exists; only the game proves vanilla "
            + "cooks from them.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 11,
                Height = 7,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 11, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Buildings = [new Fx.StageBuilding { Name = Stove, Def = "FueledStove", Cells = [new(5, 3)], Fuel = 50 }],
            Things = [new Fx.StageThing { Name = "rice", Def = "RawRice", Count = 60, Cell = new(2, 3) }],
            Zones = [new Fx.StageZone { Name = "meals", Cells = [new(9, 1), new(9, 2), new(9, 3)], Allow = ["MealSimple", "MealFine"] }],
            Pawns = [new Fx.StagePawn { Name = "cook", Colonist = 0 }],
        },
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["bill", "cooking", "work"], Prefix = "ordinary-cooking" },
        Run = RunAsync,
    };

    private static async Task<long> MealsAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var facts = await s.CallAsync(label, NativeOps.ReadColonyFacts, new RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        return facts.Resources.Where(r => r.DefName.StartsWith("Meal", StringComparison.Ordinal)).Sum(r => r.Units);
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var stove = s.Staged!.Buildings[Stove].Ids.Single();
        var before = await MealsAsync(s, "meals-before", cancellationToken).ConfigureAwait(false);
        s.Record("meals_before", before);
        var target = false;
        var meals = before;
        _ = await s.ServeUntilAsync("cooked", s.TicksLeft, async ct =>
        {
            var bills = await s.CallAsync("bills", NativeOps.ReadBills, new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
            target = bills.Benches.Where(b => b.Bench == stove).SelectMany(b => b.Bills)
                .Any(b => b.RepeatMode == RimGovernor.Brain.GameFacts.Operations.RepeatMode.Target && b.TargetCount > 0 && b.Recipe.StartsWith("Cook", StringComparison.Ordinal));
            meals = await MealsAsync(s, "meals", ct).ConfigureAwait(false);
            return (Wait.Signature(target, meals), target && meals > before);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("meals_after", meals);
        if (!target || meals <= before)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture,
                $"no native cooking from a served FoodTarget bill: target bill {target}, meals {before} -> {meals}"));
        }

        var colonists = await TypedPawns.ListAsync(s, "colonists", new RimGovernor.Brain.GameFacts.Observations.PawnFilter
            { Colonist = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Only }, cancellationToken).ConfigureAwait(false);
        if (!colonists.Any(p => p.Settings.Work is RimGovernor.Brain.GameFacts.Observations.PawnWork.Works { Rows: var rows }
            && rows.Any(w => w.DefName == "Cooking" && !w.Disabled && w.Priority > 0)))
        {
            throw new AcceptanceException("meals were cooked but no colonist's work settings enable Cooking");
        }
    }
}

using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>food/forage-unarmed</c> (#3668): a naked start's food. One stripped, unarmed colonist with nothing edible on the lab
/// and a patch of mature wild berry bushes: under the served brain the food Need designates the bushes, vanilla
/// harvests them and the colonist eats the berries.
/// </summary>
internal static class FoodForageUnarmed
{
    private const string Bushes = "berries";

    // Just above vanilla's hungry band (0.25): the kept Food need falls into it within the budget.
    private const float Fed = .3f;

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 6,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 4,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 6, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
        Plants = [new Fx.StagePlant { Name = Bushes, Def = "Plant_Berry", Cells = [.. Enumerable.Range(0, 6).Select(x => new Fx.StageCell(x, 1))] }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "forager", Colonist = 0, Gear = new Fx.StageGear { Strip = true },
                Needs = new Dictionary<string, float> { ["Food"] = Fed },
            },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "food/forage-unarmed",
        Scope = "Issue #3668: on the lab, one stripped colonist with no weapon and nothing edible, at Food 0.3, beside six mature "
            + "Plant_Berry bushes: under the served acquisition and work families colony facts read a staged bush designated, and "
            + "the consumption ring records RawBerries food_eaten. A unit test proves the forage selection, not the native "
            + "designation, vanilla's harvest or the meal. Food is kept for the hunger the claim needs.",
        Ticks = 15_000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Keep = ["Food"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["acquisition", "work"], Prefix = "forage-unarmed" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var bushes = s.Staged!.Plants[Bushes].Ids.ToHashSet(StringComparer.Ordinal);
        var designated = new SortedSet<string>(StringComparer.Ordinal);
        long eaten = 0;
        _ = await s.ServeUntilAsync("forage", s.TicksLeft, async ct =>
        {
            var facts = await s.CallAsync("colony-facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, ct).ConfigureAwait(false);
            designated.UnionWith(facts.Acquisition.Where(row => row.Designated == true && row.Source?.Id is { } id && bushes.Contains(id)).Select(row => row.Source!.Id!));

            // The ring lists completed hours only; a meal shows once its hour closes.
            var ring = await s.CallAsync("consumption", NativeOps.ReadConsumption, new Observations.ConsumptionRequest(), ct).ConfigureAwait(false);
            var hours = TypedReads.Observed("consumption", NativeOps.ReadConsumption, ring, ring.Outcome?.Observed);
            eaten = hours.Hours.SelectMany(h => h.Rows).Where(r => r is { Definition: "RawBerries", Reason: "food_eaten" }).Sum(r => r.Count ?? 0);
            return (string.Create(CultureInfo.InvariantCulture, $"designated={designated.Count} eaten={eaten}"), designated.Count > 0 && eaten > 0);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("designated", string.Join(",", designated));
        s.Record("berries_eaten", eaten);
        if (designated.Count == 0)
        {
            throw new AcceptanceException("no staged berry bush was ever read designated");
        }

        if (eaten <= 0)
        {
            throw new AcceptanceException("the consumption ring recorded no RawBerries eaten");
        }
    }
}

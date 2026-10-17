using RimGovernor.Brain.NativeFacade;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>caravan/departure</c>: the FormCaravanIntent arm of operations_apply. A home area holds simple meals, WoodLog, a
/// forbidden pemmican reserve and survival meals; the one-colonist crew carries nothing. Sending everyone is refused
/// (native keeps a colonist home); a fixed pack (WoodLog, the whole pemmican reserve, survival meals) toward a
/// reachable settlement applies, its key replays, the formed caravan's native inventory census carries exactly that
/// pack with every simple meal left home, and a fresh key after departure applies again.
/// </summary>
internal static class CaravanDeparture
{
    private const int FormationTicks = 20_000;

    private static readonly Dictionary<string, int> Packed = new(StringComparer.Ordinal) { ["WoodLog"] = 10, ["Pemmican"] = 20, ["MealSurvivalPack"] = 10 };

    internal static TypedCase Case() => new()
    {
        Name = "caravan/departure",
        Scope = "Native FormCaravanIntent: a fixed pack (WoodLog, the forbidden pemmican reserve, survival meals, simple meals left "
            + "home) formed through operations_apply, native's caravan inventory carrying exactly that pack, native's home-staffing "
            + "refusal, replay and a departed crew applied again.",
        Ticks = FormationTicks,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Site = new Fx.StageSite { Width = 11, Height = 11, Near = Fx.StageAnchor.Colonist, OffsetX = 2, Radius = 15, Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged] },

            // Native formation packs only home-area items.
            Cells = [new() { Rect = new(0, 0, 11, 11), Home = true }],
            Things =
            [
                new() { Name = "meals", Def = "MealSimple", Count = 10, Cell = new(2, 2), Near = true },
                new() { Name = "wood", Def = "WoodLog", Count = 75, Cell = new(8, 2), Near = true },
                new() { Name = "reserve", Def = "Pemmican", Count = 20, Cell = new(2, 8), Near = true, Forbidden = true },
                new() { Name = "survival", Def = "MealSurvivalPack", Count = 60, Cell = new(8, 8), Near = true },
            ],

            // A crew member's own inventory would show in the caravan's beside the pack.
            Pawns =
            [
                new() { Name = "crew", Colonist = 0, Gear = new Fx.StageGear { Strip = true } },
                new() { Name = "home-a", Colonist = 1 },
                new() { Name = "home-b", Colonist = 2 },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var pawns = s.Staged!.Pawns;
        var crew = pawns["crew"].Id;
        var everyone = new[] { crew, pawns["home-a"].Id, pawns["home-b"].Id };
        Operations.DefCount[] cargo = [.. Packed.Select(p => new Operations.DefCount { DefName = p.Key, Count = p.Value })];
        var destination = await DestinationAsync(s, crew, cargo, cancellationToken).ConfigureAwait(false);
        s.Record("destination", destination);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task<Operations.ActionResult> Apply(string key, IEnumerable<string> crewIds) => TradeKit.ApplyAsync(s, key, new Operations.FormCaravanIntent
        {
            PawnIds = [.. crewIds],
            Cargo = [.. cargo],
            DestinationTile = destination,
        }, cancellationToken);

        void Formed(string label, Operations.ActionResult result)
        {
            if (TradeKit.Applied(label, result).Caravan is not { AssemblyStarted: true } effect || effect.DestinationTile != destination || effect.PawnIds.Count != 1 || effect.PawnIds[0] != crew)
            {
                throw new AcceptanceException($"{label}: expected an applied formation of {crew} toward {destination}, got {result}");
            }
        }

        var everyoneResult = await Apply("caravan-departure-everyone", everyone).ConfigureAwait(false);
        if (TradeKit.RefusedCode(everyoneResult) != RimGovernor.Contracts.Wire.Common.FailureCode.InvalidRequest)
        {
            throw new AcceptanceException($"leave-no-one-home: expected an INVALID_REQUEST refusal, got {everyoneResult}");
        }

        var result = await Apply("caravan-departure", [crew]).ConfigureAwait(false);
        Formed("apply", result);
        var replay = await Apply("caravan-departure", [crew]).ConfigureAwait(false);
        if (replay != result)
        {
            throw new AcceptanceException($"replay: the same key returned a different result: {replay}");
        }

        // The crew gathers the pack and walks to the map edge in game time.
        Observations.CaravanState? caravan = null;
        _ = await s.RunUntilAsync("observe-formation", FormationTicks, async ct =>
        {
            var reply = await s.CallAsync("observe-formation-world", NativeOps.ReadWorldProgression, new Observations.WorldProgressionRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
            var world = TypedReads.Observed("observe-formation-world", NativeOps.ReadWorldProgression, reply, reply.Outcome?.Observed);
            caravan = world.Caravans.FirstOrDefault(c => c.Pawns.Count == 1 && c.Pawns[0].Pawn.Id == crew);
            return ($"{world.Caravans.Count} caravans", caravan is not null);
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("caravan", caravan);

        // Native's own caravan census carries exactly the composed pack: the reserve and survival meals aboard, every
        // simple meal left home.
        foreach (var def in new[] { "WoodLog", "Pemmican", "MealSurvivalPack", "MealSimple" })
        {
            var carried = caravan!.Inventory.Where(q => q.DefName == def).Sum(q => q.Units ?? 0);
            if (carried != Packed.GetValueOrDefault(def))
            {
                throw new AcceptanceException($"world-progression: caravan carries {def} x{carried}, the pack had x{Packed.GetValueOrDefault(def)} ({string.Join(", ", caravan.Inventory)})");
            }
        }

        // A fresh key after the crew departed is applied again: the intent already holds, so a resend after a lost
        // reply cannot form twice.
        Formed("apply-again", await Apply("caravan-departure-again", [crew]).ConfigureAwait(false));
    }

    /// <summary>A non-hostile settlement the crew's pack can reach, by the trade-acquisition pack estimate.</summary>
    private static async Task<int> DestinationAsync(ITypedSession s, string crew, Operations.DefCount[] cargo, CancellationToken cancellationToken)
    {
        var progression = await s.CallAsync("home-tile", NativeOps.ReadWorldProgression, new Observations.WorldProgressionRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var home = TypedReads.Observed("home-tile", NativeOps.ReadWorldProgression, progression, progression.Outcome?.Observed).Maps.FirstOrDefault(m => m.Home == true)?.Tile
            ?? throw new AcceptanceException("home-tile: no home map tile");
        var reply = await s.CallAsync("settlements", NativeOps.ReadWorld, new Observations.WorldRequest { Scope = s.Scope, Tile = home, SettlementRadius = 1000 }, cancellationToken).ConfigureAwait(false);
        var candidates = TypedReads.Observed("settlements", NativeOps.ReadWorld, reply, reply.Outcome?.Observed).Settlements
            .Where(c => c.Player != true && c.Relation == "neutral" && c.Tile is { } tile && tile != home)
            .OrderBy(c => c.Id, StringComparer.Ordinal).Take(12).ToList();
        foreach (var candidate in candidates)
        {
            var pack = new Operations.FormCaravanIntent { PawnIds = [crew], Cargo = [.. cargo], DestinationTile = candidate.Tile };
            var facts = await TradeKit.AcquisitionAsync(s, "reach-" + candidate.Id, pack, cancellationToken).ConfigureAwait(false);
            if (facts.Pack?.Outbound?.Reachable == true)
            {
                return candidate.Tile!.Value;
            }
        }

        throw new AcceptanceException($"destination: no reachable non-hostile settlement among {candidates.Count} candidates");
    }
}

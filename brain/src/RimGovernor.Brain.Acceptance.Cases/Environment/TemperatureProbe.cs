using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using static RimGovernor.Brain.Domain.Fact;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>environment/cold</c> and <c>environment/hot</c>: the EnvironmentCold/EnvironmentHot probes (#3328), the native stage
/// of the thought for each comfort deficit. test/environment_temperature stays a bespoke op: it holds a sealed room at a
/// deficit past the pawn's own comfy stat and sets or strips the ideoligion's Temperature_Tough precept, which the stage
/// primitives cannot express.
/// </summary>
internal static class TemperatureProbe
{
    internal const string HoldTool = "test/environment_temperature";

    // The fixture's room must sit within this many degrees of the asked deficit at the read.
    private const double Tolerance = 1;

    // Each step: the deficit past the comfy bound (0: mid-band) and whether the colony ideoligion holds Temperature_Tough.
    // The Tough step runs only when the profile loads Ideology.
    private static readonly (double Deficit, bool Tough)[] Steps = [(0, false), (5, false), (15, false), (25, false), (35, false), (15, true)];

    internal sealed record Hold(string Side, double Deficit, bool Tough);

    internal static readonly FixtureOp<Hold, Fx.EnvironmentTemperatureHeld> Held = new(
        HoldTool,
        h => ServedOps.Args(("side", h.Side), ("deficit", h.Deficit), ("tough", h.Tough)),
        ServedOps.Reply<Fx.EnvironmentTemperatureHeld>(HoldTool));

    internal static TypedCase Case(bool hot)
    {
        var side = hot ? "hot" : "cold";
        var thought = hot ? "EnvironmentHot" : "EnvironmentCold";
        var bound = hot ? "ComfyTemperatureMax" : "ComfyTemperatureMin";
        return new()
        {
            Name = "environment/" + side,
            Scope = $"Issue #3328, the {thought} probe (epic #3217): a colonist sealed in a doorless roofed room whose ambient "
                + $"temperature sits a held deficit past its own {bound} carries the {thought} situational thought at the shipped "
                + "card's stage offset in the observed mood thought rows (inside the comfort band: none; deficits 5/15/25/35: "
                + "stages 0-3, -4/-8/-12/-16), and none at deficit 15 with Temperature_Tough when Ideology is loaded. Vanilla "
                + "physics the card's domain tests cannot observe; it is the probe the verified-cards table cites.",

            // Each step settles at most 1200 ticks.
            Ticks = 7200,
            Tier = CaseTier.OnDemand,
            Start = new LabStart { Colonists = 1 },
            Ops = [HoldTool],
            QuietWorld = true,
            Crew = new Crew { Size = 1 },
            Run = (s, cancellationToken) => RunAsync(s, side, thought, cancellationToken),
        };
    }

    private static async Task RunAsync(TypedSession s, string side, string thought, CancellationToken cancellationToken)
    {
        var card = MechanicCards.ByDef[thought];
        var observed = new JsonArray();
        s.Record("steps", observed);
        bool? ideology = null;
        foreach (var (deficit, tough) in Steps)
        {
            if (tough && ideology == false)
            {
                s.Record("temperature_tough", "not observed: Ideology is not loaded in this profile");
                continue;
            }

            var label = string.Create(CultureInfo.InvariantCulture, $"d{deficit}{(tough ? "-tough" : "")}");
            var held = await s.FixtureAsync("hold-" + label, Held, new Hold(side, deficit, tough), cancellationToken).ConfigureAwait(false);
            ideology ??= held.Ideology;
            if (held.Tough != tough)
            {
                throw new AcceptanceException($"{label}: the fixture did not set Temperature_Tough as asked: {ServedOps.Json(held)?.ToJsonString()}");
            }

            var (offset, row, ticks, state) = await MoodProbe.ReadAsync(s, label, held.Pawn, thought, cancellationToken).ConfigureAwait(false);

            // The deficit the read itself carries: the brain's body stats at the read tick, the same ones the evaluator will read.
            var body = state.Settings.Body;
            var (cell, comfyMin, comfyMax) = (body.CellTemperatureC, body.ComfortableMinC, body.ComfortableMaxC);

            var comfy = side == "hot" ? comfyMax : comfyMin;
            var read = side == "hot" ? cell - comfy : comfy - cell;
            if (deficit == 0 ? read >= 0 : Math.Abs(read - deficit) > Tolerance)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: the room did not hold: deficit {read:F2} at the read, asked {deficit}"));
            }

            // The #3327 evaluator over the same read and the fixture's native trait and precept lists.
            var facts = new CardPawnFacts
            {
                Traits = Known(EquatableArray.From(held.Traits.ToArray())),
                Hediffs = Known(EquatableArray.From<string>([])),
                AnomalyActive = Known(true),
                IdeologyActive = Known(held.Ideology),
                Precepts = Known(EquatableArray.From(held.Precepts.ToArray())),
            };
            if (!MechanicCardGuards.CardStageOffset(card, facts, new CardStageFacts(comfyMin, comfyMax, cell)).TryGetValue(out var want))
            {
                throw new AcceptanceException($"{label}: the card prices this pawn and temperature as unknown");
            }

            var stage = MoodProbe.StageOf(card, offset);
            observed.Add(ServedOps.Args(
                ("deficit", deficit),
                ("tough", tough),
                ("read_deficit", Math.Round(read, 2)),
                ("comfy", comfy),
                ("cell_c", cell),
                ("offset", offset),
                ("stage", stage),
                ("card", want),
                ("settle_ticks", ticks),
                ("row", row is null ? null : JsonNode.Parse(WireJson.Serialize(row)))));
            if (offset != want)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: observed {thought} offset {offset} (stage {stage}) at deficit {read:F2}, the card says {want}"));
            }

            if (tough)
            {
                s.Record("temperature_tough", "observed");
            }
        }
    }
}

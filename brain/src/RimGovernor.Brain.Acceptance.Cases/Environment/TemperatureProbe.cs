using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>environment/cold</c> and <c>environment/hot</c>: the EnvironmentCold/EnvironmentHot probes (#3328), the native stage
/// of the thought for each comfort deficit.
/// </summary>
internal static class TemperatureProbe
{
    private const string HoldOp = "test/environment_temperature";

    // The fixture's room must sit within this many degrees of the asked deficit at the read.
    private const double Tolerance = 1;

    // Each step: the deficit past the comfy bound (0: mid-band) and whether the colony ideoligion holds Temperature_Tough.
    // The Tough step runs only when the profile loads Ideology.
    private static readonly (double Deficit, bool Tough)[] Steps = [(0, false), (5, false), (15, false), (25, false), (35, false), (15, true)];

    internal static AcceptanceCase Case(bool hot)
    {
        var side = hot ? "hot" : "cold";
        var thought = hot ? "EnvironmentHot" : "EnvironmentCold";
        var bound = hot ? "ComfyTemperatureMax" : "ComfyTemperatureMin";
        return new()
        {
            Name = "environment/" + side,
            Scope = $"Issue #3328, the {thought} probe (epic #3217): a colonist sealed in a roofed room whose ambient temperature "
                + $"sits a held deficit past its own {bound} carries the {thought} situational thought at the shipped card's stage "
                + "offset in the observed mood thought rows (inside the comfort band: none; deficits 5/15/25/35: stages 0-3, "
                + "-4/-8/-12/-16), and none at deficit 15 with Temperature_Tough when Ideology is loaded. Vanilla physics the "
                + "card's domain tests cannot observe; it is the probe the verified-cards table cites.",
            Start = new LabStart { Colonists = 1 },
            RequiredOps = [HoldOp],
            QuietWorld = true,
            VanillaColonists = true,
            Reason = "VanillaColonists: with no service the voluntary-control hold stops the scenario clock on the idle colonist "
                + "(unassigned_voluntary_pawn), and the thought refreshes only as game ticks pass. The fixture seals the colonist "
                + "in a doorless room, so its own jobs cannot move it out of the held temperature.",
            Budget = TimeSpan.FromMinutes(4),
            Crew = new Crew { Size = 1 },
            Run = (s, cancellationToken) => RunAsync(s, side, thought, cancellationToken),
        };
    }

    private static async Task RunAsync(ISession s, string side, string thought, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var card = MechanicCards.ByDef[thought];
        var observed = new JsonArray();
        s.Report["steps"] = observed;
        bool? ideology = null;
        foreach (var (deficit, tough) in Steps)
        {
            if (tough && ideology == false)
            {
                s.Report["temperature_tough"] = "not observed: Ideology is not loaded in this profile";
                continue;
            }

            var label = string.Create(CultureInfo.InvariantCulture, $"d{deficit}{(tough ? "-tough" : "")}");
            var held = await h.CallAsync("hold-" + label, HoldOp, new JsonObject { ["side"] = side, ["deficit"] = deficit, ["tough"] = tough }, cancellationToken).ConfigureAwait(false);
            ideology ??= held["ideology"].AsBool() == true;
            if (held["tough"].AsBool() != tough)
            {
                throw new AcceptanceException($"{label}: the fixture did not set Temperature_Tough as asked: {held.ToJson()}");
            }

            var pawn = held["pawn"].AsString();
            var (offset, row, ticks, state) = await MoodProbe.ReadAsync(s, label, pawn, thought, cancellationToken).ConfigureAwait(false);

            // The deficit the read itself carries: the brain's body stats at the read tick, the same ones the evaluator will read.
            var body = state.Settings?.Body;
            if (body?.CellTemperatureC is not { } cell || body.ComfortableMinC is not { } comfyMin || body.ComfortableMaxC is not { } comfyMax)
            {
                throw new AcceptanceException($"{label}: the pawn read carried no cell temperature or comfy range: {row.ToJson()}");
            }

            var comfy = side == "hot" ? comfyMax : comfyMin;

            var read = side == "hot" ? cell - comfy : comfy - cell;
            if (deficit == 0 ? read >= 0 : Math.Abs(read - deficit) > Tolerance)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: the room did not hold: deficit {read:F2} at the read, asked {deficit}: {held.ToJson()}"));
            }

            // The #3327 evaluator over the same read and the fixture's native trait and precept lists.
            var facts = new CardPawnFacts
            {
                Traits = Known(EquatableArray.From(held["traits"].AsSlice().Select(t => t.AsString()).ToArray())),
                Hediffs = Known(EquatableArray.From<string>([])),
                AnomalyActive = Known(true),
                IdeologyActive = Known(held["ideology"].AsBool() == true),
                Precepts = Known(EquatableArray.From(held["precepts"].AsSlice().Select(p => p.AsString()).ToArray())),
            };
            if (!MechanicCardGuards.CardStageOffset(card, facts, new CardStageFacts(comfyMin, comfyMax, cell)).TryGetValue(out var want))
            {
                throw new AcceptanceException($"{label}: the card prices this pawn and temperature as unknown: {held.ToJson()}");
            }
            var stage = MoodProbe.StageOf(card, offset);
            observed.Add(new JsonObject
            {
                ["deficit"] = deficit,
                ["tough"] = tough,
                ["read_deficit"] = Math.Round(read, 2),
                ["comfy"] = comfy,
                ["cell_c"] = cell,
                ["offset"] = offset,
                ["stage"] = stage,
                ["card"] = want,
                ["settle_ticks"] = ticks,
                ["row"] = row?.DeepClone(),
            });
            if (offset != want)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: observed {thought} offset {offset} (stage {stage}) at deficit {read:F2}, the card says {want}: {row.ToJson()}"));
            }

            if (tough)
            {
                s.Report["temperature_tough"] = "observed";
            }
        }
    }
}

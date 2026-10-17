using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using static RimGovernor.Brain.Domain.Fact;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apparel/dead_mans</c>: the DeadMansApparel probe (#3283), the native stage of the thought for each tainted count.
/// test/deadmans_wear stays a bespoke op: it marks garments natively WornByCorpse and sets or strips Bloodlust, which the
/// stage primitives cannot express.
/// </summary>
internal static class DeadMans
{
    private const string WearTool = "test/deadmans_wear";
    private const string Thought = "DeadMansApparel";

    // The fixture always dresses the pawn in this many garments; only the tainted count varies.
    private const int Garments = 5;

    // Each step: tainted count, Bloodlust, and whether the observed offset must equal the card's (n = 5 is recorded only, #3283).
    private static readonly (int Tainted, bool Bloodlust, bool Assert)[] Steps =
        [(0, false, true), (1, false, true), (2, false, true), (3, false, true), (4, false, true), (5, false, false), (2, true, true)];

    private sealed record Dress(int Tainted, bool Bloodlust);

    private static readonly FixtureOp<Dress, Fx.DeadMansWorn> Wear = new(
        WearTool,
        d => ServedOps.Args(("tainted", d.Tainted), ("bloodlust", d.Bloodlust)),
        ServedOps.Reply<Fx.DeadMansWorn>(WearTool));

    internal static TypedCase Case() => new()
    {
        Name = "apparel/dead_mans",
        Scope = "Issue #3283, the DeadMansApparel probe (epic #3217): a colonist dressed in five garments, n of them natively "
            + "WornByCorpse, carries the DeadMansApparel situational thought at the shipped card's stage offset in the observed "
            + "mood thought rows (n = 0: none; 1-4: -5/-8/-11/-14), none with Bloodlust and n = 2, and records the n = 5 stage "
            + "against the card's clamp. Vanilla physics the card's domain tests cannot observe; it is the probe the verified-cards "
            + "table cites.",

        // Each step settles at most 1200 ticks.
        Ticks = 8400,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Ops = [WearTool],
        QuietWorld = true,
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var card = MechanicCards.ByDef[Thought];
        var observed = new JsonArray();
        s.Record("steps", observed);
        foreach (var (tainted, bloodlust, assert) in Steps)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"n{tainted}{(bloodlust ? "-bloodlust" : "")}");
            var worn = await s.FixtureAsync("wear-" + label, Wear, new Dress(tainted, bloodlust), cancellationToken).ConfigureAwait(false);
            if (worn.Worn != Garments || worn.Tainted != tainted || worn.Bloodlust != bloodlust || worn.Inhumanized)
            {
                throw new AcceptanceException($"{label}: the fixture did not dress the pawn as asked: {ServedOps.Json(worn)?.ToJsonString()}");
            }

            var facts = new CardPawnFacts
            {
                Traits = Known(EquatableArray.From(worn.Traits.ToArray())),
                Hediffs = Known(EquatableArray.From<string>([])),
                AnomalyActive = Known(true),
            };
            GearOption[] items = [.. Enumerable.Range(0, Garments).Select(i => new GearOption { ID = "g" + i, Tainted = i < tainted })];
            if (!MechanicCardGuards.CardStageOffset(card, facts, items).TryGetValue(out var want))
            {
                throw new AcceptanceException($"{label}: the card prices this pawn and outfit as unknown");
            }

            var (offset, row, ticks, _) = await MoodProbe.ReadAsync(s, label, worn.Pawn, Thought, cancellationToken).ConfigureAwait(false);
            var stage = MoodProbe.StageOf(card, offset);
            observed.Add(ServedOps.Args(
                ("tainted", tainted),
                ("bloodlust", bloodlust),
                ("offset", offset),
                ("stage", stage),
                ("card", want),
                ("settle_ticks", ticks),
                ("row", row is null ? null : JsonNode.Parse(WireJson.Serialize(row)))));
            if (assert && offset != want)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: observed {Thought} offset {offset} (stage {stage}), the card says {want}"));
            }

            if (!assert)
            {
                if (row is null)
                {
                    throw new AcceptanceException($"{label}: no {Thought} thought observed with {tainted} tainted garments");
                }

                s.Record("n5_offset", offset);
                s.Record("n5_stage", stage);
                s.Record("n5_matches_card_clamp", offset == want);
            }
        }
    }
}

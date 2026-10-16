using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>apparel/dead_mans</c>: the DeadMansApparel probe, the native stage of the thought for each tainted count.</summary>
internal static class DeadMans
{
    private const string WearOp = "test/deadmans_wear";
    private const string Thought = "DeadMansApparel";

    // The fixture always dresses the pawn in this many garments; only the tainted count varies.
    private const int Garments = 5;

    // Situational thoughts refresh on the pawn's needs interval (150 ticks); the read waits for a clean cache.
    private const int Settle = 300;
    private const int SettleMax = 1200;

    // Each step: tainted count, Bloodlust, and whether the observed offset must equal the card's (n = 5 is recorded only, #3283).
    private static readonly (int Tainted, bool Bloodlust, bool Assert)[] Steps =
        [(0, false, true), (1, false, true), (2, false, true), (3, false, true), (4, false, true), (5, false, false), (2, true, true)];

    internal static AcceptanceCase Case() => new()
    {
        Name = "apparel/dead_mans",
        Scope = "Issue #3283, the DeadMansApparel probe (epic #3217): a colonist dressed in five garments, n of them natively "
            + "WornByCorpse, carries the DeadMansApparel situational thought at the shipped card's stage offset in the observed "
            + "mood thought rows (n = 0: none; 1-4: -5/-8/-11/-14), none with Bloodlust and n = 2, and records the n = 5 stage "
            + "against the card's clamp. Vanilla physics the card's domain tests cannot observe; it is the probe the verified-cards "
            + "table cites.",
        Start = new LabStart(),
        RequiredOps = [WearOp],
        QuietWorld = true,
        VanillaColonists = true,
        Reason = "VanillaColonists: with no service the voluntary-control hold stops the scenario clock on the idle colonist "
            + "(unassigned_voluntary_pawn), and the thought refreshes only as game ticks pass. The fixture locks the garments and "
            + "defers the apparel optimizer, so the colonist's own jobs leave the outfit as staged.",
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var card = MechanicCards.ByDef[Thought];
        var observed = new JsonArray();
        s.Report["steps"] = observed;
        foreach (var (tainted, bloodlust, assert) in Steps)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"n{tainted}{(bloodlust ? "-bloodlust" : "")}");
            var worn = await h.CallAsync("wear-" + label, WearOp, new JsonObject { ["tainted"] = tainted, ["bloodlust"] = bloodlust }, cancellationToken).ConfigureAwait(false);
            if ((int)worn["worn"].AsNumber() != Garments || (int)worn["tainted"].AsNumber() != tainted || worn["bloodlust"].AsBool() != bloodlust || worn["inhumanized"].AsBool() != false)
            {
                throw new AcceptanceException($"{label}: the fixture did not dress the pawn as asked: {worn.ToJson()}");
            }

            var pawn = worn["pawn"].AsString();
            var traits = worn["traits"].AsSlice().Select(t => t.AsString()).ToArray();
            var facts = new CardPawnFacts
            {
                Traits = Known(EquatableArray.From(traits)),
                Hediffs = Known(EquatableArray.From<string>([])),
                AnomalyActive = Known(true),
            };
            GearOption[] items = [.. Enumerable.Range(0, Garments).Select(i => new GearOption { ID = "g" + i, Tainted = i < tainted })];
            if (!MechanicCardGuards.CardStageOffset(card, facts, items).TryGetValue(out var want))
            {
                throw new AcceptanceException($"{label}: the card prices this pawn and outfit as unknown");
            }

            var (offset, row, ticks) = await ReadAsync(s, label, pawn, cancellationToken).ConfigureAwait(false);
            var stage = StageOf(card, offset);
            observed.Add(new JsonObject
            {
                ["tainted"] = tainted,
                ["bloodlust"] = bloodlust,
                ["offset"] = offset,
                ["stage"] = stage,
                ["card"] = want,
                ["settle_ticks"] = ticks,
                ["row"] = row?.DeepClone(),
            });
            if (assert && offset != want)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: observed {Thought} offset {offset} (stage {stage}), the card says {want}: {row.ToJson()}"));
            }

            if (!assert)
            {
                if (row is null)
                {
                    throw new AcceptanceException($"{label}: no {Thought} thought observed with {tainted} tainted garments");
                }

                s.Report["n5_offset"] = offset;
                s.Report["n5_stage"] = stage;
                s.Report["n5_matches_card_clamp"] = offset == want;
            }
        }
    }

    /// <summary>The thought's total offset (0 when absent) from the mood thoughts the brain lifts out of the pawn read, and the raw situational row.</summary>
    private static async Task<(double Offset, JsonObject? Row, int Ticks)> ReadAsync(ISession s, string label, string pawn, CancellationToken cancellationToken)
    {
        for (var advanced = 0; ; )
        {
            _ = await s.AdvanceAsync(Settle, null, cancellationToken).ConfigureAwait(false);
            advanced += Settle;
            var row = await PawnSupport.ReadPawnAsync(s.Harness, s, $"read-{label}-{CaseSupport.Tag(advanced)}", pawn, true, cancellationToken).ConfigureAwait(false);
            NativeAssert.RequireSocial(row);
            var social = row["social"].AsMap()!;
            if (social["situationalCacheStale"].AsBool() == true)
            {
                if (advanced >= SettleMax)
                {
                    throw new AcceptanceException($"{label}: the situational thought cache is still stale after {advanced} ticks");
                }

                continue;
            }

            if (!ObservationPackage.MoodThoughts(WireCodec.ToRecord<PawnState>(row)).TryGetValue(out var thoughts))
            {
                throw new AcceptanceException($"{label}: the pawn's mood thoughts are unknown: {social.ToJson()}");
            }

            var raw = social["situational"].AsSlice().Select(r => r.AsMap()).FirstOrDefault(r => r?["defName"].AsString() == Thought);
            return (thoughts.FirstOrDefault(t => t.Def == Thought)?.Offset ?? 0, raw, advanced);
        }
    }

    /// <summary>The card stage whose baseMoodEffect is <paramref name="offset"/>, -1 for none.</summary>
    private static int StageOf(MechanicCard card, double offset)
    {
        var stages = card.Xml.Stages;
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].BaseMoodEffect == offset)
            {
                return i;
            }
        }

        return -1;
    }
}

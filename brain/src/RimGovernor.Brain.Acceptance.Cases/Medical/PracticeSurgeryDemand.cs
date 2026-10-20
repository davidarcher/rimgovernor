using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/practice-surgery-*</c> (#3638): whether the served brain trains its doctor by practice surgery is decided by raid
/// demand alone (#3344). Each case stages one colonist, a Medicine-3 doctor with every other skill at 0 and a short bow, then
/// test/medical_learning "practice" puts a held prisoner missing a leg in a prison bed with wood for a peg leg and five herbal medicine
/// beside it and no bill. The practice slot and the medicine are the same in all three; the threat and the patients differ.
/// <para>
/// Demand reaches a calibrated exposure cell as apparel/harm_armor_direction does: Cassandra on day 25 gives the threat schedule
/// raids, the quiet colony's raid points sit at the game's floor of 35, and one skill-0 short-bow gunner's defense capacity (about
/// 29) puts the ratio of raid points to capacity near 1.2, in the 1 to 2 gunner bin <c>Policy/Harm/exposure.json</c> calibrates.
/// A second colonist would move the ratio out of the bin, so the cheaper-learning patient is a colony cow, which is no defender.
/// </para>
/// </summary>
internal static class MedicalPracticeSurgeryDemand
{
    private const string Practice = "practice/surgery/";
    private const string Tend = "support/care/";
    private const string Doctor = "doctor";
    private const string Cow = "cow";

    // Cassandra's threat comps gate on days passed, and her raids are no longer all forced after day 20 (RaidsForcedBeforeDays).
    private const float Day = 25;

    internal static IEnumerable<TypedCase> Cases() =>
    [
        Case("demand", true, false,
            "Under the served brain, a colony under Cassandra past day 20 whose one doctor is at Medicine 3 (a gunner in a calibrated "
                + "exposure cell), with a held prisoner missing a leg, wood for a peg leg and herbal medicine, selects a practice surgery and the "
                + "doctor's native surgery pays a Medicine XP callback equal to the projection: raid harm at the projected tend quality "
                + "values the XP (#3635), and the Investment credit selects the practice (#3637).",
            DemandAsync),
        Case("no-demand", false, false,
            "The same colony in a quiet world (no forecast threat) generates the practice surgery and refuses it: no coupled selection "
                + "selects a practice surgery while one rejects it, since XP has no value without raid demand.",
            NoDemandAsync),
        Case("cheaper-learning", true, true,
            "The demand colony with a colony cow needing tending selects the doctor's tend, whose native Medicine callback the "
                + "Investment term credits like practice XP, and no practice surgery while the tend is pending: real medical work "
                + "supplies the learning first (#3344).",
            CheaperLearningAsync),
    ];

    private static TypedCase Case(string name, bool threat, bool patient, string scope, Func<TypedSession, CancellationToken, Task> run) => new()
    {
        Name = "medical/practice-surgery-" + name,
        Scope = scope + " A unit test cannot observe the live storyteller, raid points, defense capacity, native stat curves, "
            + "practice and tend reads and the logged selection together.",
        Ticks = TypedCase.StagedTickCap, // a hang guard: the decisive review lands within the first minute of game time
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = Stage(threat, patient),
        Ops = [LabSkillLearning.VerifyTool, LabSkillLearning.MedicalTool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "practice-" + name },
        Run = run,
    };

    /// <summary>
    /// A 12 by 6 site beside the colonist: the doctor at (1, 2), so test/medical_learning's 3 by 4 bed area (x 2 to 4, z 1 to 4)
    /// lies inside it; meals for the doctor and the prisoner, and with <paramref name="patient"/> a bruised cow.
    /// </summary>
    private static Fx.StageSpec Stage(bool threat, bool patient) => new()
    {
        Paused = true,
        World = threat ? new Fx.StageWorld { Storyteller = "Cassandra", DaysPassed = Day } : null,
        Site = new Fx.StageSite
        {
            Width = 12,
            Height = 6,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 12, 6), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
        Things = [.. Enumerable.Range(0, 3).Select(i => new Fx.StageThing { Name = "meals" + i, Def = "MealSimple", Count = 50, Cell = new(8 + i, 1), Near = true })],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = Doctor,
                Colonist = 0,
                Cell = new(1, 2),
                SkillLevel = 0,
                Skills = new Dictionary<string, int> { ["Medicine"] = 3 },
                ClearTraits = true,
                Gear = new Fx.StageGear { Weapon = "Bow_Short", Strip = true },
                Needs = new Dictionary<string, float> { ["Food"] = 1f, ["Rest"] = 1f },
            },
            .. patient ? new[] { new Fx.StagePawn { Name = Cow, Kind = "Cow", Faction = Fx.StageFaction.Player, Age = 4, Cell = new(9, 4), Injured = true } } : [],
        ],
    };

    /// <summary>Stages the practice prisoner beside the doctor and starts the learning verifier.</summary>
    private static async Task<Fx.MedicalLearningRead> PrisonerAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("learning-start", LabSkillLearning.Verify, "start", cancellationToken).ConfigureAwait(false);
        var staged = await s.FixtureAsync("practice-stage", LabSkillLearning.Medical, "practice", cancellationToken).ConfigureAwait(false);
        s.Record("staged", ServedOps.Json(staged));
        if (staged.Doctor.Length == 0 || staged.Patient.Length == 0 || staged.Part < 0)
        {
            throw new AcceptanceException($"practice-stage: {ServedOps.Json(staged)}");
        }

        return staged;
    }

    private static bool Practises(CoupledSelectionRow row) => row.Tasks.Any(t => t.StartsWith(Practice, StringComparison.Ordinal));

    private static string Describe(CoupledSelectionRow row) => string.Create(CultureInfo.InvariantCulture,
        $"{row.Verdict} tasks=[{string.Join(' ', row.Tasks)}] investment={row.Selected.Investment} unknown=[{string.Join(' ', row.Selected.Unknown ?? [])}]");

    private static async Task DemandAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await PrisonerAsync(s, cancellationToken).ConfigureAwait(false);
        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? chosen = null;
        var surgeries = 0;
        _ = await s.ServeUntilAsync("practice-demand", s.TicksLeft, async ct =>
        {
            chosen ??= CoupledSelections.Of(s, before).FirstOrDefault(Practises);
            var got = await s.FixtureAsync("learning-read", LabSkillLearning.Verify, "read", ct).ConfigureAwait(false);
            surgeries = got.Surgery;
            if (got.Failures.Count != 0)
            {
                throw new AcceptanceException($"native learning callback failures: {string.Join(", ", got.Failures)}");
            }

            return (Wait.Signature(chosen is not null, surgeries), chosen is not null && surgeries >= 1);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("selection", Describe(chosen!));
        await LabSkillLearning.VerifyCallbacksAsync(s, "surgery", cancellationToken).ConfigureAwait(false);
    }

    private static async Task NoDemandAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await PrisonerAsync(s, cancellationToken).ConfigureAwait(false);
        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        IReadOnlyList<CoupledSelectionRow> rows = [];
        _ = await s.ServeUntilAsync("practice-no-demand", s.TicksLeft, ct =>
        {
            rows = CoupledSelections.Of(s, before);
            if (rows.FirstOrDefault(Practises) is { } practised)
            {
                throw new AcceptanceException($"a practice surgery was selected without a forecast threat: {Describe(practised)}");
            }

            var refused = rows.Any(r => r.RejectedIds.Any(id => id.StartsWith(Practice, StringComparison.Ordinal)));
            return Task.FromResult((string.Create(CultureInfo.InvariantCulture, $"{rows.Count} {refused}"), refused));
        }, cancellationToken).ConfigureAwait(false);
        s.Record("selections", rows.Count);
    }

    private static async Task CheaperLearningAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var cow = s.Staged?.Pawns.GetValueOrDefault(Cow)?.Id ?? throw new AcceptanceException("the stage made no cow");
        _ = await PrisonerAsync(s, cancellationToken).ConfigureAwait(false);
        var tend = Tend + Uri.EscapeDataString("care:" + cow);
        var before = CoupledSelections.Of(s, 0).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        CoupledSelectionRow? chosen = null;
        _ = await s.ServeUntilAsync("practice-cheaper-learning", s.TicksLeft, ct =>
        {
            foreach (var row in CoupledSelections.Of(s, before))
            {
                if (Practises(row))
                {
                    throw new AcceptanceException($"a practice surgery was selected while the cow's tend was pending: {Describe(row)}");
                }

                if (row.Tasks.Any(t => t.StartsWith(tend, StringComparison.Ordinal)))
                {
                    chosen = row;
                    break;
                }
            }

            return Task.FromResult((Wait.Signature(chosen is not null), chosen is not null));
        }, cancellationToken).ConfigureAwait(false);
        s.Record("selection", Describe(chosen!));
    }
}

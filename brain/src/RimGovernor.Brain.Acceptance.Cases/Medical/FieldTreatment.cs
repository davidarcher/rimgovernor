using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>medical/field-treatment</c> (task 4.4 of redefine-habitat-requirements): a downed, bleeding colonist lies far from the
/// colony's only bed, a doctor beside it. Under the served brain's normal review the patient is cared for within hours: its
/// bleeding stops or every bleeding wound is tended, read back from the production health detail. The claim is the observed
/// care, not the means: whether a field spot is placed where it lies, it is carried to the far bed, or it is stabilized first
/// is the care comparison's choice (CareFieldTests), and a designated spot nobody is tended on answers nothing.
/// </summary>
internal static class FieldTreatment
{
    private const int Colonists = 2;

    // A 40x7 strip: a roofed 5x5 room with one bed at its west end, the patient and the doctor at its east end, some 30 cells away.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 10 },
        Site = new Fx.StageSite
        {
            Width = 40,
            Height = 7,
            Near = Fx.StageAnchor.Colonist,
            Radius = 40,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 40, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Rooms = [new Fx.StageRoom { Name = "ward", Rect = new Fx.StageRect(0, 0, 5, 5), Stuff = "WoodLog", Doors = [new(4, 2)], Roof = true }],
        Buildings = [new Fx.StageBuilding { Name = "ward-bed", Def = "Bed", Stuff = "WoodLog", Cells = [new(1, 1)] }],
        Things = [new Fx.StageThing { Name = "meals", Def = "MealSimple", Count = 20, Cell = new(2, 6), Near = true }],
        Pawns =
        [
            new Fx.StagePawn { Name = "patient", Colonist = 0, Cell = new(37, 3), Downed = true, Cut = 12f },
            new Fx.StagePawn { Name = "doctor", Colonist = 1, Cell = new(36, 3), Work = new Dictionary<string, int> { ["Doctor"] = 1 } },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "medical/field-treatment",
        Scope = "Task 4.4: a downed colonist bleeding from a cut lies some 30 cells from the colony's only bed with a doctor beside it; "
            + "under the served brain's normal review its bleeding stops or every bleeding wound is tended within the hang guard, "
            + "and it is alive, read back from the production health detail. A designated field spot alone does not pass.",
        Ticks = 15_000, // a hang guard: a tend is a short job once the patient lies where the doctor reaches it
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["hospital", "medical", "tend", "rescue", "work", "sleeping"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "field-treatment" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var patient = s.Staged!.Pawns["patient"].Id;
        var before = await HealthAsync(s, "patient-before", patient, cancellationToken).ConfigureAwait(false);
        if (!before.Bleeding || !Untended(before).Any())
        {
            throw new AcceptanceException($"{patient} is not bleeding from an untended wound (bleed {before.BleedRatePerDay}): the precondition hides the claim");
        }

        RimGovernor.Brain.GameFacts.Observations.PawnHealth? last = null;
        var dead = false;
        _ = await s.ServeUntilAsync("patient-cared", s.TicksLeft, async ct =>
        {
            var row = await TypedPawns.OneAsync(s, "patient", patient, ct).ConfigureAwait(false);
            dead = row.Dead;
            last = row.Health;
            var cared = !dead && last is { } h && (!h.Bleeding || !Untended(h).Any());
            return (string.Create(CultureInfo.InvariantCulture, $"bleeding={last?.Bleeding} untended={(last is null ? -1 : Untended(last).Count())} bed={last?.BedId}"), cared || dead);
        }, cancellationToken).ConfigureAwait(false);

        s.Record("bed", last?.BedId ?? "");
        s.Record("bleed_rate_per_day", last?.BleedRatePerDay ?? -1);
        if (dead)
        {
            throw new AcceptanceException($"{patient} died before its care was observed");
        }

        if (last is not { } health || (health.Bleeding && Untended(health).Any()))
        {
            throw new AcceptanceException($"{patient} still bleeds from {(last is null ? "?" : string.Join(", ", Untended(last).Select(h => h.DefName)))}: no care was observed");
        }
    }

    private static async Task<RimGovernor.Brain.GameFacts.Observations.PawnHealth> HealthAsync(TypedSession s, string label, string pawn, CancellationToken cancellationToken) =>
        (await TypedPawns.OneAsync(s, label, pawn, cancellationToken).ConfigureAwait(false)).Health
            ?? throw new AcceptanceException($"{label}: {pawn} read no health detail");

    // The wounds a tend would stop: the native's tendable injuries (a Cut) not yet tended.
    private static IEnumerable<RimGovernor.Brain.GameFacts.Observations.Hediff> Untended(RimGovernor.Brain.GameFacts.Observations.PawnHealth health) =>
        health.Hediffs.Where(h => h.DefName == "Cut" && h.Tend is RimGovernor.Brain.GameFacts.Observations.HediffTend.Untended);
}

using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>recovery/service</c>: the disaster-recovery repair dispatch. A GiveJobIntent Repair on a staged damaged player
/// wall in the home area starts the vanilla WorkGiver_Repair job, an unknown pawn is refused, a resent key returns its
/// first result, and the game repairs the wall to full hit points.
/// </summary>
internal static class RecoveryService
{
    private const string Wall = "wall";
    private const string Builder = "builder";

    internal static TypedCase Case() => new()
    {
        Name = "recovery/service",
        Scope = "GiveJobIntent Repair on operations_apply: on a staged damaged player Wall in the home area, the applied "
            + "receipt names the Repair job for the builder on the wall; an unknown pawn is refused; a resent key returns its "
            + "first result; observations_list_buildings then reads the wall back at full hit points. The repair runs "
            + "RimWorld's own WorkGiver_Repair and JobDriver.",
        Ticks = 20000,
        Tier = CaseTier.Nightly,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 6,
                Height = 6,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 4,
                Radius = 40,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
            },
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 6, 6), Terrain = "Concrete", Home = true }],
            Buildings = [new Fx.StageBuilding { Name = Wall, Def = "Wall", Stuff = "WoodLog", Cells = [new(3, 3)], HitPoints = 60 }],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = Builder,
                    Colonist = 0,
                    Cell = new(1, 1),
                    Work = new Dictionary<string, int> { ["Construction"] = 1 },
                    Timetable = Fx.StageTimeAssignment.Work,
                },
            ],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var wallId = s.Staged!.Buildings[Wall].Ids.Single();
        var builderId = s.Staged.Pawns[Builder].Id;

        async Task<Observations.BuildingHitPoints.Tracked> WallAsync(string label, CancellationToken ct)
        {
            var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [wallId], Statuses = [Observations.BuildingStatus.Built] }, ct).ConfigureAwait(false);
            return rows is [{ HitPoints: Observations.BuildingHitPoints.Tracked hitPoints } row] && row.Building.Id == wallId
                ? hitPoints
                : throw new AcceptanceException($"{label}: want one built row with hit points for {wallId}, got {string.Join("; ", rows)}");
        }

        async Task<Operations.ActionResult> RepairAsync(string key, string pawn)
        {
            var reply = await s.CallAsync(
                key,
                NativeOps.Apply,
                new Operations.ApplyRequest
                {
                    Identity = s.Identity,
                    Actions = [new Operations.Action { Key = key, Intent = new Operations.GiveJobIntent { Pawn = new Common.Ref { Id = pawn }, Job = "Repair", Targets = [new Common.Ref { Id = wallId }] } }], DeferSnapshot = false },
                cancellationToken).ConfigureAwait(false);
            return reply.Results.ToList() is [{ } result]
                ? result
                : throw new AcceptanceException($"{key}: want one result, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
        }

        var before = await WallAsync("wall-before", cancellationToken).ConfigureAwait(false);
        s.RecordWire("wall_before", before);
        if (before.Current >= before.Maximum)
        {
            throw new AcceptanceException($"wall-before: the staged wall is not damaged: {before.Current}/{before.Maximum}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var unknown = await RepairAsync("recovery-unknown-pawn", "Human_unknown_recovery_pawn").ConfigureAwait(false);
        if (unknown.Outcome?.Refused is null)
        {
            throw new AcceptanceException($"recovery-unknown-pawn: want a refusal, got {unknown}");
        }

        var repair = await RepairAsync("recovery-repair", builderId).ConfigureAwait(false);
        s.RecordWire("repair", repair);
        var job = repair.Outcome?.Applied?.Outcome?.Applied?.Observed?.Effect?.Job;
        if (job?.PawnId != builderId || job.JobDef != "Repair" || job.TargetA?.Target?.ThingId != wallId)
        {
            throw new AcceptanceException($"recovery-repair: want an applied Repair job for {builderId} on {wallId}, got {repair}");
        }

        var replay = await RepairAsync("recovery-repair", builderId).ConfigureAwait(false);
        if (WireJson.Serialize(replay) != WireJson.Serialize(repair))
        {
            throw new AcceptanceException($"recovery-repair: the resent key returned {replay}, not its first result");
        }

        var after = before;
        _ = await s.RunUntilAsync("repaired", 20000, async ct =>
        {
            after = await WallAsync("wall-after", ct).ConfigureAwait(false);
            return (Wait.Signature(after.Current), after.Current >= after.Maximum);
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("wall_after", after);
    }
}

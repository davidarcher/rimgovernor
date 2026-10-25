using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>recovery/area</c>: the disaster-recovery allowed-area write. Under a registered roof hazard a colonist restricted
/// to an outdoor Area is moved to a roofed refuge by WorkSettingsIntent, read back from the pawn; clearing the
/// restriction under the hazard and a missing pawn are refused; a resent key returns its first result and a fresh key
/// applies again. The colony-facts environment row carries the hazard with its remaining duration.
/// </summary>
internal static class RecoveryArea
{
    private const string Tool = "test/recovery_area_prepare";

    private static readonly FixtureOp<string, Fx.RecoveryAreaReply> Prepare = new(
        Tool,
        _ => [],
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.RecoveryAreaReply>(reply.ToJsonString()) is { Success: true, Pawn.Length: > 0, Refuge.Length: > 0, Outdoor.Length: > 0 } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "recovery/area",
        Scope = "WorkSettingsIntent allowedArea on operations_apply under a registered ToxicFallout "
            + "(test/recovery_area_prepare: refuge and outdoor allowed Areas, the colonist restricted outdoors; no stage "
            + "primitive makes an Area): the colonist is reassigned to the roofed refuge, read back through "
            + "observations_list_pawns; a clear under the hazard is refused INVALID_REQUEST and leaves the restriction; a "
            + "missing pawn is refused NOT_FOUND; a resent key returns its first result and a fresh key applies again. The "
            + "colony-facts environment row carries the hazard with a timed remaining duration.",
        Ticks = 0,
        Tier = CaseTier.Nightly,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // The fixture writes the map outside any authority scope, so it runs before the grant.
        var prepared = await s.FixtureAsync("prepare", Prepare, "", cancellationToken).ConfigureAwait(false);
        s.RecordFixture("prepared", prepared);

        var observed = await s.CallAsync("environment-census", NativeOps.ReadColonyFacts, new RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var hazard = observed.Environment.FirstOrDefault(row => row.DefName == "ToxicFallout")
            ?? throw new AcceptanceException($"environment-census: no ToxicFallout row in {string.Join("; ", observed.Environment)}");
        s.RecordWire("environment_hazard", hazard);
        if (hazard.Duration is not RimGovernor.Brain.GameFacts.Observations.ConditionDuration.Timed { Ticks: > 0 } || hazard.Id.Length == 0)
        {
            throw new AcceptanceException($"environment-census: the hazard row lacks a timed remaining duration: {hazard}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<string?> AreaAsync(string label)
        {
            var reply = await s.CallAsync(
                label,
                NativeOps.ListPawns,
                new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest
                {
                    Scope = s.Scope,
                    Filter = new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Ids = [prepared.Pawn] },
                },
                cancellationToken).ConfigureAwait(false);
            var rows = reply.Pawns.ToList();
            return rows is [{ } row] && row.Pawn.Id == prepared.Pawn
                ? row.Settings.AllowedAreaId
                : throw new AcceptanceException($"{label}: want one row for {prepared.Pawn}, got {rows.Count}");
        }

        async Task<RimGovernor.Brain.GameFacts.Operations.ActionResult> ApplyAsync(string label, string key, string pawn, RimGovernor.Brain.GameOrders.Operations.AssignmentValue area)
        {
            var reply = await s.CallAsync(
                label,
                NativeOps.Apply,
                new RimGovernor.Brain.GameOrders.Operations.ApplyRequest
                {
                    Identity = s.Identity,
                    Actions = [new RimGovernor.Brain.GameOrders.Operations.Action { Key = key, Intent = new RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent { PawnId = pawn, AllowedArea = new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = area } } }], DeferSnapshot = false },
                cancellationToken).ConfigureAwait(false);
            return reply.Results.ToList() is [{ } result]
                ? result
                : throw new AcceptanceException($"{label}: want one result, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
        }

        static void Refused(string label, RimGovernor.Brain.GameFacts.Operations.ActionResult result, RimGovernor.Brain.GameFacts.Common.FailureCode code)
        {
            if (result.Outcome?.Refused?.Code != code)
            {
                throw new AcceptanceException($"{label}: want a {code} refusal, got {result}");
            }
        }

        static void Applied(string label, RimGovernor.Brain.GameFacts.Operations.ActionResult result)
        {
            var fields = result.Outcome?.Applied?.Outcome?.Applied?.Observed?.Effect?.Settings?.Fields.ToList() ?? [];
            if (fields is not [{ Field: RimGovernor.Brain.GameFacts.Receipts.SettingsField.AllowedArea, Outcome: RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }])
            {
                throw new AcceptanceException($"{label}: want one applied allowed-area field, got {result}");
            }
        }

        var refuge = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase(prepared.Refuge);
        if (await AreaAsync("pawn-before").ConfigureAwait(false) != prepared.Outdoor)
        {
            throw new AcceptanceException($"pawn-before: want the colonist restricted to {prepared.Outdoor}");
        }

        // Clearing the restriction under the roof hazard is refused and leaves it in place; so is a pawn not on the map.
        Refused("hazard-clear", await ApplyAsync("hazard-clear", "area-hazard-clear", prepared.Pawn, new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.ClearCase()).ConfigureAwait(false), RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest);
        if (await AreaAsync("hazard-clear-unchanged").ConfigureAwait(false) != prepared.Outdoor)
        {
            throw new AcceptanceException("hazard-clear: the refused clear changed the restriction");
        }

        Refused("missing-pawn", await ApplyAsync("missing-pawn", "area-missing-pawn", "Human_missing_pawn", refuge).ConfigureAwait(false), RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound);

        var first = await ApplyAsync("apply-area", "area-apply", prepared.Pawn, refuge).ConfigureAwait(false);
        Applied("apply-area", first);
        if (await AreaAsync("pawn-after-apply").ConfigureAwait(false) != prepared.Refuge)
        {
            throw new AcceptanceException($"pawn-after-apply: want the colonist reassigned to {prepared.Refuge}");
        }

        // A resent key returns its first result; a fresh key applies again.
        var resent = await ApplyAsync("resend-area", "area-apply", prepared.Pawn, refuge).ConfigureAwait(false);
        if (OrdersJson.Serialize(resent) != OrdersJson.Serialize(first))
        {
            throw new AcceptanceException($"resend-area: the resent key returned {resent}, not its first result {first}");
        }

        Applied("reapply-area", await ApplyAsync("reapply-area", "area-reapply", prepared.Pawn, refuge).ConfigureAwait(false));
    }
}

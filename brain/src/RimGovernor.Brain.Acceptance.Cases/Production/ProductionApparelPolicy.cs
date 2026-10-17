using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/apparel-policy</c>: the ApparelPolicyIntent's native filter write. The pawn starts on a player outfit
/// wearing a forced, locked shirt (test/gear_fixture, which the primitives cannot express); each write reuses that
/// outfit as the pawn's own, labelled with its short name (#1302), with the requested filter, and clears forcing and
/// locks; a repeat applies again; and a player's tainted edit is overridden. The colony-facts gear read shows each
/// result.
/// </summary>
internal static class ProductionApparelPolicy
{
    private const string Tool = "test/gear_fixture";

    private static readonly FixtureOp<string, Fx.GearPolicyReply> Gear = new(
        Tool,
        mode => new JsonObject([new KeyValuePair<string, JsonNode?>("mode", mode)]),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.GearPolicyReply>(reply.ToJsonString()) is { Success: true, Pawn.Length: > 0, ShortName.Length: > 0 } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "production/apparel-policy",
        Scope = "The ApparelPolicyIntent on operations_apply writes the native outfit: the pawn's player outfit is reused, "
            + "labelled with its short name (#1302), with the requested defs, hit-point and quality bounds and tainted "
            + "apparel excluded, its forced and locked apparel released; the same write applies again, and a player's tainted "
            + "edit is overridden. Read back through the colony-facts gear section.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var subject = await s.FixtureAsync("policy-setup", Gear, "policy_setup", cancellationToken).ConfigureAwait(false);

        async Task<Observations.GearLoadout> ReadAsync(string label)
        {
            var reply = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = true }, cancellationToken).ConfigureAwait(false);
            var facts = TypedReads.Observed(label, NativeOps.ReadColonyFacts, reply, reply.Outcome?.Observed);
            var gear = facts.Planning?.Outcome?.Observed?.Gear ?? throw new AcceptanceException($"{label}: no planning gear section");
            return gear.Pawns.FirstOrDefault(p => p.Pawn?.Id == subject.Pawn) ?? throw new AcceptanceException($"{label}: no gear row for {subject.Pawn}");
        }

        var setup = await ReadAsync("setup").ConfigureAwait(false);
        s.RecordWire("setup", setup.ApparelPolicy);
        var outfit = setup.ApparelPolicy?.PolicyId;
        if (outfit is null || setup.Equipment?.Apparel.Any(a => a is { Forced: true, Locked: true }) != true)
        {
            throw new AcceptanceException($"setup: want a player outfit and a forced, locked shirt, got {setup.ApparelPolicy} / {setup.Equipment}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task WriteAsync(string label, float minHitPoints, IReadOnlyList<string> defs)
        {
            var apply = await s.CallAsync(label, NativeOps.Apply, new Operations.ApplyRequest
            {
                Identity = s.Identity,
                Actions =
                [
                    new Operations.Action
                    {
                        Key = label,
                        Intent = new Operations.ApparelPolicyIntent
                        {
                            PawnId = subject.Pawn,
                            Name = subject.ShortName,
                            AllowedDefs = [.. defs],
                            MinHitPoints = minHitPoints,
                            MaxHitPoints = 1,
                            MinQuality = 1,
                            MaxQuality = 6,
                        },
                    },
                ],
            }, cancellationToken).ConfigureAwait(false);
            if (apply.Results.ToList() is not [{ Outcome.Applied: not null }])
            {
                throw new AcceptanceException($"{label}: want one applied apparel policy, got {apply.BatchFailure} {string.Join("; ", apply.Results)}");
            }

            var after = await ReadAsync(label + "-read").ConfigureAwait(false);
            s.RecordWire(label, after.ApparelPolicy);
            var policy = after.ApparelPolicy;
            var problems = new List<string>();
            if (policy?.PolicyId != outfit)
            {
                problems.Add($"outfit {policy?.PolicyId}, want the reused {outfit}");
            }

            if (policy?.Name != subject.ShortName)
            {
                problems.Add($"label {policy?.Name}, want {subject.ShortName}");
            }

            if (policy is null || !policy.AllowedDefs.SequenceEqual(defs.Order(StringComparer.Ordinal)))
            {
                problems.Add($"defs [{string.Join(' ', policy?.AllowedDefs ?? [])}], want [{string.Join(' ', defs)}]");
            }

            if (policy?.MinHitPoints is not { } min || Math.Abs(min - minHitPoints) > 0.00001 || policy.MinQuality != 1 || policy.MaxQuality != 6)
            {
                problems.Add($"bounds hp {policy?.MinHitPoints} quality {policy?.MinQuality}..{policy?.MaxQuality}, want hp {minHitPoints.ToString(CultureInfo.InvariantCulture)} quality 1..6");
            }

            if (policy?.ExcludesTainted != true)
            {
                problems.Add("tainted apparel allowed");
            }

            if (after.Equipment?.Apparel.Any(a => a.Forced == true || a.Locked == true) != false)
            {
                problems.Add($"forced or locked apparel left: {after.Equipment}");
            }

            if (problems.Count > 0)
            {
                throw new AcceptanceException($"{label}: {string.Join("; ", problems)}");
            }
        }

        await WriteAsync("apparel-write-0", .51f, ["Apparel_BasicShirt", "Apparel_Pants"]).ConfigureAwait(false);
        await WriteAsync("apparel-write-1", .65f, ["Apparel_Pants"]).ConfigureAwait(false);
        await WriteAsync("apparel-write-again", .65f, ["Apparel_Pants"]).ConfigureAwait(false);

        // A player edit on the pawn's own outfit is overridden on the next write.
        _ = await s.FixtureAsync("policy-edit", Gear, "policy_edit", cancellationToken).ConfigureAwait(false);
        var edited = await ReadAsync("player-edit").ConfigureAwait(false);
        if (edited.ApparelPolicy?.ExcludesTainted != false)
        {
            throw new AcceptanceException($"player-edit: the tainted edit did not land: {edited.ApparelPolicy}");
        }

        await WriteAsync("apparel-repair-player-edit", .51f, ["Apparel_Pants"]).ConfigureAwait(false);
    }
}

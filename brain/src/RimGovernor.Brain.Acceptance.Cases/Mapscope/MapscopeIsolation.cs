using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using Common = RimGovernor.Contracts.Wire.Common;
using Control = RimGovernor.Contracts.Wire.Authority;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mapscope/isolation</c>: two loaded player maps. Reads for either map's identity resolve that map whichever is
/// viewed; the player's switch of the viewed map invalidates authority as IDENTITY_CHANGED; the roster carries map ids;
/// presentation reads bound to the viewed map refuse the other identity as stale; an unloaded map is stale with the
/// viewed context; back on the home map a draft bound to it lands there.
/// </summary>
internal static class MapscopeIsolation
{
    private const string GenerateTool = "test/map_scope_generate";
    private const string ViewTool = "test/map_scope_view";

    private static readonly FixtureOp<string, Fx.MapScopeReply> Generate = new(GenerateTool, _ => [], (label, reply) => Read(GenerateTool, label, reply));

    private static readonly FixtureOp<int, Fx.MapScopeReply> View = new(
        ViewTool,
        mapId => new JsonObject([new KeyValuePair<string, JsonNode?>("mapId", mapId)]),
        (label, reply) => Read(ViewTool, label, reply));

    internal static TypedCase Case() => new()
    {
        Name = "mapscope/isolation",
        Scope = "Two loaded player maps (test/map_scope_generate settles a second; test/map_scope_view is the player's "
            + "map switch): observations_read_status for either identity answers about its own map whichever is viewed; the "
            + "switch invalidates authority as Inactive(IDENTITY_CHANGED) at generation+1; presentation_colonists rows carry "
            + "the home map id and a currentMapOnly roster for the second map is empty; presentation_selection refuses the "
            + "home identity STALE_IDENTITY with the viewed map's context while accepting the second's; an unloaded map id is "
            + "STALE_IDENTITY with the viewed context; back home, a regranted draft lands on the home map's colonist with the "
            + "receipt's snapshot token.",
        Ticks = 0,
        Tier = CaseTier.Nightly,
        Ops = [GenerateTool, ViewTool],
        Run = RunAsync,
    };

    private static Fx.MapScopeReply Read(string tool, string label, JsonObject? reply) =>
        reply is not null && Fx.FixtureJson.Deserialize<Fx.MapScopeReply>(reply.ToJsonString()) is { Success: true } read
            ? read
            : throw new AcceptanceException($"{label}: {tool} refused: {reply?.ToJsonString()}");

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var home = s.Identity;
        var expected = await TypedAuthority.ExpectedGenerationAsync(s, "status-fresh", home, cancellationToken).ConfigureAwait(false);
        _ = await TypedAuthority.GrantAsync(s, "grant-home", home, expected, cancellationToken).ConfigureAwait(false);

        // A second player map; a read for its identity answers about it while the home map stays viewed.
        var generated = await s.FixtureAsync("generate", Generate, "", cancellationToken).ConfigureAwait(false);
        s.RecordFixture("generated", generated);
        if (generated.HomeMapId != home.MapId || generated.SecondMapId == home.MapId || generated.ViewedMapId != home.MapId)
        {
            throw new AcceptanceException($"generate: want a distinct second map with map {home.MapId} still viewed, got {generated}");
        }

        var second = home with { MapId = generated.SecondMapId };
        await StatusOnAsync(s, "status-second-while-home-viewed", second, cancellationToken).ConfigureAwait(false);
        await StatusOnAsync(s, "status-home-while-home-viewed", home, cancellationToken).ConfigureAwait(false);

        // Generation may flip the viewed map and back, which authority sees as the player's own view change: measure from here.
        var beforeSwitch = await TypedAuthority.StatusAsync(s, "status-after-generate", home, cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("view-second", View, second.MapId, cancellationToken).ConfigureAwait(false);
        var afterSwitch = await TypedAuthority.StatusAsync(s, "status-after-switch", home, cancellationToken).ConfigureAwait(false);
        s.RecordWire("status_after_switch", afterSwitch);
        if (afterSwitch.State?.Inactive?.Reason != Control.RevocationReason.IdentityChanged
            || afterSwitch.Context?.NativeGeneration != beforeSwitch.Context?.NativeGeneration + 1)
        {
            throw new AcceptanceException($"status-after-switch: want Inactive(IDENTITY_CHANGED) at generation {beforeSwitch.Context?.NativeGeneration + 1}, got {afterSwitch}");
        }

        await StatusOnAsync(s, "status-home-while-second-viewed", home, cancellationToken).ConfigureAwait(false);
        await StatusOnAsync(s, "status-second-while-second-viewed", second, cancellationToken).ConfigureAwait(false);

        // The roster carries map ids: every colonist is on the home map, and none on the second.
        var roster = await RosterAsync(s, "roster", new Presentation.ColonistRosterRequest { Identity = home, CurrentMapOnly = false }, cancellationToken).ConfigureAwait(false);
        if (roster.Count == 0 || roster.Any(row => row.MapId != home.MapId))
        {
            throw new AcceptanceException($"roster: want colonists all on map {home.MapId}, got {string.Join("; ", roster)}");
        }

        var secondOnly = await RosterAsync(s, "roster-second-only", new Presentation.ColonistRosterRequest { Identity = second, CurrentMapOnly = true }, cancellationToken).ConfigureAwait(false);
        if (secondOnly.Count != 0)
        {
            throw new AcceptanceException($"roster-second-only: want no colonists on the generated map, got {secondOnly.Count}");
        }

        // Presentation state lives on the viewed map: the home identity is stale there (the HTTP refusal carries its code
        // and detail, not the viewed map's context); the second identity passes the identity check (a snapshot when
        // rendered, the graphical-only refusal headless).
        await TypedLifecycle.ExpectRefusedAsync(
            "selection-home-while-second-viewed",
            () => s.CallAsync("selection-home-while-second-viewed", NativeOps.ReadSelection, new Presentation.ReadRequest { Identity = home }, cancellationToken),
            Common.FailureCode.StaleIdentity).ConfigureAwait(false);
        try
        {
            _ = await s.CallAsync("selection-second-while-second-viewed", NativeOps.ReadSelection, new Presentation.ReadRequest { Identity = second }, cancellationToken).ConfigureAwait(false);
        }
        catch (NativeFailureException e) when (e.Value.Code == Common.FailureCode.Unavailable)
        {
            s.RecordWire("selection-second-headless-refusal", e.Value);
        }

        // An identity naming a map that is not loaded is stale, with the viewed map's context to re-anchor on.
        var gone = await s.CallAsync("status-unloaded-map", NativeOps.ReadStatus, new Observations.StatusRequest { Scope = new() { ExpectedIdentity = home with { MapId = 987654 } } }, cancellationToken).ConfigureAwait(false);
        StaleOn("status-unloaded-map", gone.Outcome?.Failure, second.MapId, gone.Outcome);

        // Back on the home map, a fresh grant admits a draft bound to the home identity, and it lands there.
        _ = await s.FixtureAsync("view-home", View, home.MapId, cancellationToken).ConfigureAwait(false);
        var back = await TypedAuthority.StatusAsync(s, "status-back-home", home, cancellationToken).ConfigureAwait(false);
        _ = await TypedAuthority.GrantAsync(s, "regrant-home", home, back.Context?.NativeGeneration ?? throw new AcceptanceException($"status-back-home: no generation: {back}"), cancellationToken).ConfigureAwait(false);
        var colonist = roster[0].PawnId ?? throw new AcceptanceException("roster: the first colonist has no pawn id");
        var draft = await s.CallAsync(
            "draft",
            NativeOps.Apply,
            new Operations.ApplyRequest
            {
                Identity = home,
                Actions = [new Operations.Action { Key = "mapscope-draft-1", Intent = new Operations.DraftIntent { PawnId = colonist, Drafted = true } }],
            },
            cancellationToken).ConfigureAwait(false);
        var receipt = draft.Results.ToList() is [{ Outcome.Applied: { } applied }] ? applied : throw new AcceptanceException($"draft: want one applied result, got {draft.BatchFailure} {string.Join("; ", draft.Results)}");
        s.RecordWire("draft", receipt);
        var job = receipt.Outcome?.Applied?.Observed?.Effect?.Job;
        if (job?.Issued != true || receipt.AdmittedContext?.Identity.MapId != home.MapId)
        {
            throw new AcceptanceException($"draft: want {colonist} drafted, admitted on map {home.MapId}, got {receipt}");
        }

        var pawns = await s.CallAsync(
            "pawn-after",
            NativeOps.ListPawns,
            new Observations.ListPawnsRequest { Scope = s.Scope, Filter = new Observations.PawnFilter { Ids = [colonist], Colonist = true } },
            cancellationToken).ConfigureAwait(false);
        var rows = TypedReads.Observed("pawn-after", NativeOps.ListPawns, pawns, pawns.Outcome?.Observed).Pawns.ToList();
        if (rows is not [{ Drafted: true } after] || after.Pawn.MapId != home.MapId || after.PawnSnapshot?.Token != job.ResultingSnapshotToken)
        {
            throw new AcceptanceException($"pawn-after: want {colonist} drafted on map {home.MapId} with the receipt's snapshot token {job.ResultingSnapshotToken}, got {string.Join("; ", rows)}");
        }
    }

    private static async Task<IReadOnlyList<Presentation.ColonistReference>> RosterAsync(TypedSession s, string label, Presentation.ColonistRosterRequest request, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadColonists, request, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Roster?.Colonists.ToList() ?? throw new AcceptanceException($"{label}: want a roster, got {reply.Outcome}");
    }

    // A STALE_IDENTITY refusal carrying the viewed map's context.
    private static void StaleOn(string label, Common.Failure? failure, int viewedMap, object? reply)
    {
        if (failure?.Code != Common.FailureCode.StaleIdentity || failure.ObservedContext?.Identity.MapId != viewedMap)
        {
            throw new AcceptanceException($"{label}: want STALE_IDENTITY observed on map {viewedMap}, got {reply}");
        }
    }

    // observations_read_status for identity answers with that identity's map in its context, whichever map is viewed.
    private static async Task StatusOnAsync(TypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(
            label,
            NativeOps.ReadStatus,
            new Observations.StatusRequest { Scope = new() { ExpectedIdentity = identity }, Colonists = false, Threats = false },
            cancellationToken).ConfigureAwait(false);
        var observed = TypedReads.Observed(label, NativeOps.ReadStatus, reply, reply.Outcome?.Observed);
        if (observed.Context?.Identity.MapId != identity.MapId)
        {
            throw new AcceptanceException($"{label}: want context map {identity.MapId}, got {observed.Context}");
        }
    }
}

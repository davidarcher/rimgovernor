using RimGovernor.Brain.NativeFacade;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>pawn/reads</c>: observations_list_pawns over the lab's three colonists and one staged wild Muffalo. Filters
/// select exactly, disabled detail families are absent, each row's CAS snapshots name the row's pawn at the read's
/// context, invalid filters and a stale identity fail with typed codes, and the read neither advances the paused game
/// nor changes when repeated.
/// </summary>
internal static class PawnReads
{
    private static readonly Observations.PawnDetails NoDetails = new()
    {
        Needs = false,
        Health = false,
        Equipment = false,
        Biography = false,
        Settings = false,
        Social = false,
        Animals = false,
    };

    internal static TypedCase Case() => new()
    {
        Name = "pawn/reads",
        Scope = "observations_list_pawns: exact id, colonist, animal and known-false filters; disabled detail families absent; snapshots "
            + "naming the row's pawn at the read context; INVALID_REQUEST for a duplicate id or negative distance and STALE_IDENTITY "
            + "for a stale load token; and a paused read pass that leaves the context and a repeated read unchanged.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite { Width = 3, Height = 3, Near = Fx.StageAnchor.MapCenter, OffsetZ = 4 },
            Pawns = [new Fx.StagePawn { Name = "muffalo", Kind = "Muffalo", Faction = Fx.StageFaction.None, Cell = new Fx.StageCell(1, 1) }],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        Common.ObservationContext? context = null;

        async Task<Observations.ListPawnsReplyOutcome?> CallAsync(string label, Observations.PawnFilter? filter, Observations.PawnDetails? details, Observations.ReadScope? scope = null)
        {
            var reply = await s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest { Scope = scope ?? s.Scope, Filter = filter, Details = details }, cancellationToken).ConfigureAwait(false);
            return reply.Outcome;
        }

        async Task<List<Observations.PawnState>> ReadAsync(string label, Observations.PawnFilter? filter, Observations.PawnDetails? details)
        {
            var observed = (await CallAsync(label, filter, details).ConfigureAwait(false))?.Observed
                ?? throw new AcceptanceException($"{label}: not observed");
            context ??= observed.Context;
            if (observed.Context != context)
            {
                problems.Add($"{label}: context {observed.Context} drifted from {context}");
            }

            foreach (var row in observed.Pawns)
            {
                if (row.Pawn.Id.Length == 0 || row.Pawn.DefName.Length == 0)
                {
                    problems.Add($"{label}: row without an id and def: {row.Pawn}");
                }

                if (row.PawnSnapshot is { } snapshot && (snapshot.EntityId != row.Pawn.Id || snapshot.Context != observed.Context || string.IsNullOrEmpty(snapshot.Token)))
                {
                    problems.Add($"{label}: {row.Pawn.Id} snapshot {snapshot} does not name the row at the read context");
                }
            }

            return [.. observed.Pawns];
        }

        async Task ExpectFailureAsync(string label, Common.FailureCode code, Observations.PawnFilter? filter, Observations.ReadScope? scope = null)
        {
            var outcome = await CallAsync(label, filter, NoDetails, scope).ConfigureAwait(false);
            if (outcome?.Failure?.Code != code)
            {
                problems.Add($"{label}: want {code}, got {outcome}");
            }
        }

        var muffalo = s.Staged?.Pawns["muffalo"].Id ?? throw new AcceptanceException("the stage made no muffalo");
        var baseline = await ReadAsync("bounded", null, NoDetails).ConfigureAwait(false);
        if (baseline.Count < 4)
        {
            problems.Add($"bounded read found {baseline.Count} pawns, want the 3 colonists and the muffalo");
        }

        var colonists = await ReadAsync("colonists", new Observations.PawnFilter { Colonist = true, Humanlike = true, Animal = false }, null).ConfigureAwait(false);
        if (colonists.Count != 3)
        {
            problems.Add($"colonist filter found {colonists.Count}, want 3");
        }

        foreach (var row in colonists.Where(r => string.IsNullOrEmpty(r.Health?.Snapshot.Token) || r.Social is null))
        {
            problems.Add($"colonist {row.Pawn.Id} lacks a health snapshot or social section");
        }

        var target = colonists.FirstOrDefault()?.Pawn.Id ?? throw new AcceptanceException("no colonist read");
        void ExpectCount(string label, IReadOnlyCollection<Observations.PawnState> rows, int want)
        {
            if (rows.Count != want)
            {
                problems.Add($"{label}: {rows.Count} rows, want {want}");
            }
        }

        ExpectCount("exact-id", await ReadAsync("exact-id", new Observations.PawnFilter { Ids = [target], Colonist = true, Animal = false }, NoDetails).ConfigureAwait(false), 1);
        ExpectCount("contradictory", await ReadAsync("contradictory", new Observations.PawnFilter { Ids = [target], Colonist = false }, NoDetails).ConfigureAwait(false), 0);
        ExpectCount("unknown-id", await ReadAsync("unknown-id", new Observations.PawnFilter { Ids = ["missing-pawn-id"] }, NoDetails).ConfigureAwait(false), 0);
        var animals = await ReadAsync("animals", new Observations.PawnFilter { Colonist = false, Animal = true }, NoDetails).ConfigureAwait(false);
        if (!animals.Any(a => a.Pawn.Id == muffalo && a.PawnSnapshot is not null))
        {
            problems.Add($"animal filter did not read the muffalo {muffalo} with a snapshot");
        }

        var disabled = await ReadAsync("details-disabled", new Observations.PawnFilter { Ids = [target] }, NoDetails).ConfigureAwait(false);
        if (disabled is not [{ Needs: null, Health: null, Equipment: null, Biography: null, Settings: null, Social: null, AnimalState: null }])
        {
            problems.Add($"details-disabled read a detail section: {string.Join("; ", disabled)}");
        }

        var knownFalse = await ReadAsync("known-false", new Observations.PawnFilter { Drafted = false, Downed = false }, NoDetails).ConfigureAwait(false);
        if (knownFalse.Any(p => p.Drafted || p.Downed))
        {
            problems.Add("known-false filter returned a drafted or downed pawn");
        }

        await ExpectFailureAsync("duplicate-id", Common.FailureCode.InvalidRequest, new Observations.PawnFilter { Ids = [target, target] }).ConfigureAwait(false);
        await ExpectFailureAsync("negative-distance", Common.FailureCode.InvalidRequest, new Observations.PawnFilter { WithinColonistDistance = -1 }).ConfigureAwait(false);
        await ExpectFailureAsync("stale-identity", Common.FailureCode.StaleIdentity, null, new Observations.ReadScope { ExpectedIdentity = s.Identity with { LoadToken = "stale-load" } }).ConfigureAwait(false);
        var repeat = await ReadAsync("repeat", null, NoDetails).ConfigureAwait(false);
        if (!repeat.SequenceEqual(baseline))
        {
            problems.Add("repeating the bounded read returned a different snapshot");
        }

        s.RecordWire("context", context);
        s.Record("pawns", baseline.Count);
        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}

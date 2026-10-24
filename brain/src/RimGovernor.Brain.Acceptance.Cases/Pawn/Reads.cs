using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>pawn/reads</c>: observations_list_pawns over the lab's three colonists and one staged wild Muffalo. Filters
/// select exactly, every row is whole (a colonist's animal section is NotAnimal, the muffalo's an animal state), each
/// row's CAS snapshots name the row's pawn at the read's
/// context, invalid filters and a stale identity fail with typed codes, and the read neither advances the paused game
/// nor changes when repeated.
/// </summary>
internal static class PawnReads
{

    internal static TypedCase Case() => new()
    {
        Name = "pawn/reads",
        Scope = "observations_list_pawns: exact id, colonist, animal and known-false filters; whole rows; snapshots "
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

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        Common.ObservationContext? context = null;

        Task<Observations.PawnSnapshot> CallAsync(string label, Observations.PawnFilter? filter, Observations.ReadScope? scope = null) =>
            s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest { Scope = scope ?? s.Scope, Filter = filter }, cancellationToken);

        async Task<List<Observations.PawnState>> ReadAsync(string label, Observations.PawnFilter? filter)
        {
            var observed = await CallAsync(label, filter).ConfigureAwait(false);
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
            try
            {
                var observed = await CallAsync(label, filter, scope).ConfigureAwait(false);
                problems.Add($"{label}: want {code}, got {observed.Pawns.Count} pawns");
            }
            catch (NativeFailureException refused) when (refused.Value.Code == code)
            {
                s.Record(label, refused.Message);
            }
        }

        var muffalo = s.Staged?.Pawns["muffalo"].Id ?? throw new AcceptanceException("the stage made no muffalo");
        var baseline = await ReadAsync("bounded", null).ConfigureAwait(false);
        if (baseline.Count < 4)
        {
            problems.Add($"bounded read found {baseline.Count} pawns, want the 3 colonists and the muffalo");
        }

        var colonists = await ReadAsync("colonists", new Observations.PawnFilter { Colonist = true, Humanlike = true, Animal = false }).ConfigureAwait(false);
        if (colonists.Count != 3)
        {
            problems.Add($"colonist filter found {colonists.Count}, want 3");
        }

        foreach (var row in colonists.Where(r => string.IsNullOrEmpty(r.Health.Snapshot.Token) || r.AnimalState is not Observations.PawnAnimalState.NotAnimal))
        {
            problems.Add($"colonist {row.Pawn.Id} lacks a health snapshot or reads an animal state");
        }

        var target = colonists.FirstOrDefault()?.Pawn.Id ?? throw new AcceptanceException("no colonist read");
        void ExpectCount(string label, IReadOnlyCollection<Observations.PawnState> rows, int want)
        {
            if (rows.Count != want)
            {
                problems.Add($"{label}: {rows.Count} rows, want {want}");
            }
        }

        ExpectCount("exact-id", await ReadAsync("exact-id", new Observations.PawnFilter { Ids = [target], Colonist = true, Animal = false }).ConfigureAwait(false), 1);
        ExpectCount("contradictory", await ReadAsync("contradictory", new Observations.PawnFilter { Ids = [target], Colonist = false }).ConfigureAwait(false), 0);
        ExpectCount("unknown-id", await ReadAsync("unknown-id", new Observations.PawnFilter { Ids = ["missing-pawn-id"] }).ConfigureAwait(false), 0);
        var animals = await ReadAsync("animals", new Observations.PawnFilter { Colonist = false, Animal = true }).ConfigureAwait(false);
        if (!animals.Any(a => a.Pawn.Id == muffalo && a.PawnSnapshot is not null && a.AnimalState is Observations.PawnAnimalState.Animal))
        {
            problems.Add($"animal filter did not read the muffalo {muffalo} with a snapshot and an animal state");
        }

        var knownFalse = await ReadAsync("known-false", new Observations.PawnFilter { Drafted = false, Downed = false }).ConfigureAwait(false);
        if (knownFalse.Any(p => p.Drafted || p.Downed))
        {
            problems.Add("known-false filter returned a drafted or downed pawn");
        }

        await ExpectFailureAsync("duplicate-id", Common.FailureCode.InvalidRequest, new Observations.PawnFilter { Ids = [target, target] }).ConfigureAwait(false);
        await ExpectFailureAsync("negative-distance", Common.FailureCode.InvalidRequest, new Observations.PawnFilter { WithinColonistDistance = -1 }).ConfigureAwait(false);
        await ExpectFailureAsync("stale-identity", Common.FailureCode.StaleIdentity, null, new Observations.ReadScope { ExpectedIdentity = s.Identity with { LoadToken = "stale-load" } }).ConfigureAwait(false);
        var repeat = await ReadAsync("repeat", null).ConfigureAwait(false);
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

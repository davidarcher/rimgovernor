using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Store.StorePackage;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxRoundsPayload = 1024 * 1024;

    private const int MaxRoundsStandards = 306;

    /// <summary>Reads and validates the stored review; an empty journal has the zero review (Go <c>loadRounds</c>).</summary>
    internal static Rounds LoadRounds(StoreTransaction tx)
    {
        var (data, found) = tx.QueryFirst("SELECT payload FROM rounds WHERE singleton=1", [], r => r.GetString(0));
        if (!found)
        {
            return new Rounds();
        }
        if (System.Text.Encoding.UTF8.GetByteCount(data!) > MaxRoundsPayload)
        {
            throw new CapacityException();
        }
        var r = StoreJson.Deserialize<Rounds>(data!);
        if (!string.Equals(StoreJson.Serialize(r), data, StringComparison.Ordinal) || r.Revision == 0 || !r.Snapshot.IsValid || r.Tick.Value < 0 || r.OrdinaryTick.Value < 0 || r.OrdinaryTick > r.Tick || r.Standards.Count > MaxRoundsStandards)
        {
            throw new StoreCorruptException("invalid rounds history");
        }
        r.MedicalCare.Validate();
        if (r.Dependencies.Count > MaxDependencyRecords)
        {
            throw new StoreCorruptException("invalid routine dependencies");
        }
        if (r.WallCells.Count > MaxWallCells)
        {
            throw new StoreCorruptException("invalid routine wall cells");
        }
        foreach (var d in r.Dependencies)
        {
            d.Validate();
        }
        r.MoodHistory().Validate();
        r.Disaster?.Validate();
        if (r.Disaster is { } disaster && disaster.Observed > r.Tick)
        {
            throw new StoreCorruptException("future disaster history");
        }
        ValidateRoundsRecovery(r);
        var proposals = r.Enabled ? MoodProposals(r.MoodHistory()) : [];
        if (proposals.Count != r.MoodMethods.Count)
        {
            throw new StoreCorruptException("invalid mood method review");
        }
        if (!EquatableArray.From(proposals).Equals(r.MoodMethods))
        {
            throw new StoreCorruptException("stale mood method proposal");
        }
        r.EventLoot.Validate();
        if (r.ReserveSupplies.Count > 4096 || (!r.Enabled && r.ReserveSupplies.Count != 0))
        {
            throw new StoreCorruptException("invalid reserve method history");
        }
        var reserveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in r.ReserveSupplies)
        {
            if (!PolicyFunctions.ReserveFoodDefinition(new Resource(row.Definition)) || !reserveIds.Add(row.Thing))
            {
                throw new StoreCorruptException("invalid reserve supply");
            }
            new StartingSupply { Thing = row.Thing, Definition = row.Definition }.Validate();
        }
        if (r.LarderSupplies.Count > 1 || (!r.Enabled && r.LarderSupplies.Count != 0))
        {
            throw new StoreCorruptException("invalid larder method history");
        }
        foreach (var row in r.LarderSupplies)
        {
            row.Validate();
        }
        r.Sleeping.Validate();
        if (r.Sleeping.Uses.Any(use => use.Tick > r.Tick))
        {
            throw new StoreCorruptException("future sleeping use history");
        }
        r.Comfort.Validate();
        if (r.Comfort.Dining.Tick > r.Tick || r.Comfort.Recreation.Tick > r.Tick)
        {
            throw new StoreCorruptException("future comfort use history");
        }
        if (r.Roster is { } roster
            && (roster.Tick > r.Tick
                || roster.Coverage.Count > 256
                || roster.Decaying.Count > 4096
                || roster.Profiles.Count > 256
                || (roster.Help is { } help && (help.Tick > roster.Tick || help.Idle.Count > 256 || help.Helpers.Count > 256 || help.Withheld.Count > 256))))
        {
            throw new StoreCorruptException("invalid routine roster history");
        }
        if (r.Progress.Count > MaxRoundsStandards)
        {
            throw new StoreCorruptException("invalid routine progress history");
        }
        var progressGoals = new HashSet<ConcernId>();
        foreach (var p in r.Progress)
        {
            if (!progressGoals.Add(p.Concern) || !TryValidate(() => PolicyFunctions.ValidateConcernProgress(p, r.Tick)))
            {
                throw new StoreCorruptException("invalid routine progress history");
            }
        }
        // The stored review is the fact: a binding is valid when it names a routine goal the table knows, not
        // when a second derivation from empty facts would also have assessed it. Response kinds live in the
        // incidents table; Standards binds Standards only.
        var seen = new HashSet<ConcernId>();
        var identities = new HashSet<StandardId>();
        foreach (var binding in r.Standards)
        {
            if (!PolicyFunctions.IsStandardKind(binding.Concern) || !seen.Add(binding.Concern) || !identities.Add(binding.Standard))
            {
                throw new StoreCorruptException("duplicate routine need");
            }
            var g = LoadStandard(tx, binding.Standard);
            if (g.Standard.Kind != binding.Concern)
            {
                throw new StoreCorruptException("routine standard ownership mismatch");
            }
        }
        ValidateRoundsIncidents(tx, r);
        return r;
    }

    private static bool TryValidate(Action validate)
    {
        try
        {
            validate();
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>The stored review; an empty journal has the zero review.</summary>
    public Rounds LoadRounds(CancellationToken cancellationToken) => Run(LoadRounds, cancellationToken);

    public Task<Rounds> LoadRoundsAsync(CancellationToken cancellationToken) => RunAsync(LoadRounds, cancellationToken);

    /// <summary>
    /// Commits the requested inspection scope and latch history atomically. It selects no methods and grants no
    /// execution authority. A disabled review of the same world suspends routine goals and leaves their in-flight
    /// work open for the next enabled review to resume; only world replacement or a tick rewind invalidates linked
    /// work through the same cancellation journal as Hands.
    /// </summary>
    public RoundsResult ReviewRounds(RoundsRequest request, CancellationToken cancellationToken)
    {
        if (!request.Current.IsValid || request.Tick.Value < 0)
        {
            throw new DomainException("invalid routine scope");
        }
        var settled = new Dictionary<(ColonyId Colony, MapId Map), Tick>();
        var result = Run(tx => ReviewRoundsIn(tx, request, settled), cancellationToken);
        Floors.Raise(settled);
        return result;
    }

    public Task<RoundsResult> ReviewRoundsAsync(RoundsRequest request, CancellationToken cancellationToken) => Task.Run(() => ReviewRounds(request, cancellationToken), cancellationToken);

    internal static RoundsResult ReviewRoundsIn(StoreTransaction tx, RoundsRequest request, Dictionary<(ColonyId Colony, MapId Map), Tick> settled)
    {
        var previous = LoadRounds(tx);
        if (previous.Revision != request.Revision)
        {
            throw new ConflictException();
        }
        if (previous.Revision == ulong.MaxValue)
        {
            throw new CapacityException();
        }
        var a = previous.Snapshot;
        var b = request.Current;
        var worldChanged = a.Colony != b.Colony || a.Load != b.Load || a.Map != b.Map || request.Tick < previous.Tick;
        var changed = previous.Revision != 0 && worldChanged;
        // Player direction invalidates work, not an observed shortage's recovery target. Only world replacement
        // or time rewind discards latch history.
        var reset = previous.Revision == 0 || worldChanged;
        var latches = previous.Latches;
        var medical = previous.MedicalCare;
        var loot = previous.EventLoot;
        var sleeping = previous.Sleeping;
        var comfort = previous.Comfort;
        var mood = previous.MoodHistory();
        var disaster = previous.Disaster;
        if (reset)
        {
            latches = new RoundsLatches();
            medical = new MedicalCareHistory();
            loot = new EventLootHistory();
            comfort = new ComfortHistory();
            sleeping = new SleepingHistory();
            mood = new MoodHistory();
            disaster = null;
        }
        var facts = request.Facts;
        var needs = new RoundsFindings();
        RecoveryRequest? recovery = null;
        RoundsDetection? detection = null;
        var workEvidence = reset ? null : previous.WorkEvidence;
        // Stopping routine work must not depend on a successful native observation.
        if (request.Enabled && !request.Immediate)
        {
            workEvidence = PolicyFunctions.WorkAdmissionEvidenceOf(facts,
                ProductionBatchesIn(tx, request.Current, request.Tick));
            facts = PolicyFunctions.ReviewWorkResources(facts, ProductionBatchesIn(tx, request.Current, request.Tick),
                WorkActionsIn(tx, request.Current));
            facts = facts with
            {
                ResourceRunways = ResourceRunways(request),
                ConstructionClaims = ConstructionClaimsIn(tx, request.Current, request.Tick),
                UpkeepIssued = RoundsUpkeepIssued(tx, request.Current),
            };
            var sleepingReview = PolicyFunctions.ReviewSleeping(facts.Sleeping, sleeping, request.Tick);
            sleeping = sleepingReview.History;
            facts = facts with { SleepingRecovered = sleepingReview.Recovered() };
            if (facts.BedroomsOwed.TryGetValue(out var owed) && owed)
            {
                facts = facts with { SleepingRecovered = Known(false) };
            }
            // The rooms family reads hosting recovery from this proof-of-use history.
            comfort = PolicyFunctions.ReviewComfort(facts.Comfort, comfort, request.Tick).History;
            var (lootCensus, held) = LootReachFilter(request with { Facts = facts });
            // The cohort is the admin/supply-safety Need's work (AdminRounds reads it from the review).
            var (lootHistory, _) = PolicyFunctions.ReviewEventLoot(lootCensus, loot);
            loot = lootHistory;
            // The food reserve owns the forbid state of reserve food; drop its stacks from the cohort so the
            // planners cannot flip them.
            loot = loot with { Held = held };
            loot = loot with { Pending = EquatableArray.From(PolicyFunctions.DropReserveHeld(loot.Pending, facts.FoodReserve)) };
            if (facts.Upkeep.Clearance.TryGetValue(out var rows))
            {
                var req = PolicyFunctions.ReviewRecoveryRequest(request.Policy, facts, rows, facts.Upkeep.RoomObstructions);
                recovery = req;
                // Only candidates that pass safety and storage checks are owed.
                var working = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var e in PolicyFunctions.RankRecovery(req).Entries)
                {
                    working[e.ID] = e.Status is RecoveryStatus.Admitted or RecoveryStatus.Queued;
                }
                var workable = rows.Where(row => working.TryGetValue(row.EntityId, out var ok) && ok).ToList();
                facts = facts with { Upkeep = facts.Upkeep with { Clearance = Known(EquatableArray.From(workable)) } };
            }
            medical = PolicyFunctions.ReviewMedicalCare(facts.MedicalPawns, medical);
            facts = facts with { MedicalCareRecovered = medical.Recovered() };
            mood = PolicyFunctions.ReviewMood(facts.MoodPawns, mood);
            facts = facts with { Mood = mood, Disaster = disaster, DisasterTick = request.Tick };
            if (!reset)
            {
                facts = facts with { Admitted = EquatableArray.From(PriorAdmitted(tx, previous, request.Tick)) };
            }
            detection = new RoundsDetection(facts, latches, request.Policy);
            needs = PolicyFunctions.InspectRounds(facts, latches, request.Policy);
            disaster = needs.Disaster;
        }
        if (request.Enabled && request.Immediate)
        {
            facts = facts with { Disaster = disaster, DisasterTick = reset ? new Tick(0) : previous.OrdinaryTick };
            detection = new RoundsDetection(facts, latches, request.Policy);
            needs = PolicyFunctions.InspectImmediateRounds(facts, latches, request.Policy);
        }
        request = request with { Facts = facts };
        var old = new Dictionary<ConcernId, StandardState>();
        var assessed = new HashSet<ConcernId>();
        foreach (var n in needs.All())
        {
            _ = assessed.Add(n.Id);
        }
        if (request.Enabled && !request.Immediate)
        {
            RetireRoundsPlans(tx, request.Current, request.Tick, request.Facts.CurrentConstruction, settled);
        }
        foreach (var binding in previous.Standards)
        {
            var g = LoadStandard(tx, binding.Standard);
            if (changed || (request.Enabled && !assessed.Contains(binding.Concern) && (!request.Immediate || PolicyFunctions.ImmediateConcern(binding.Concern))))
            {
                if (g.Standard.Status != StandardStatus.Voided)
                {
                    CancelMethods(tx, g);
                    g = SaveStandard(tx, g, g.Standard with { Status = StandardStatus.Voided });
                }
            }
            // Paused control keeps its goals active: the PauseSafeguard vetoes new work, native designations
            // already issued keep progressing and the resumed goal adopts the result.
            old[binding.Concern] = g;
        }
        // A replaced or rewound world ends its occurrences and their work, as it invalidates its goals.
        if (changed)
        {
            foreach (var binding in previous.Incidents)
            {
                AbandonIncident(tx, binding.Incident, previous.Tick);
            }
        }
        // Keep disabled bindings available for review. An enabled review replaces invalidated bindings, so their
        // clean history can leave active capacity.
        var retained = new HashSet<StandardId>();
        foreach (var g in old.Values)
        {
            if (!request.Enabled || g.Standard.Status != StandardStatus.Voided)
            {
                _ = retained.Add(g.Standard.Id);
            }
        }
        if (!request.Immediate || changed)
        {
            RetireRoundsStandards(tx, retained);
        }
        var r = new Rounds
        {
            Revision = previous.Revision + 1,
            Snapshot = b,
            Tick = request.Tick,
            Enabled = request.Enabled,
            Latches = needs.Latches,
            Immediate = request.Immediate,
            OrdinaryTick = !reset ? previous.OrdinaryTick : default,
            WorkEvidence = workEvidence,
        };
        if (request.Enabled && !request.Immediate)
        {
            r = r with { OrdinaryTick = request.Tick };
        }
        r = r with { MedicalCare = medical, BrewingFinished = PolicyFunctions.BrewingFinished(request.Facts.Research), EventLoot = loot };
        if (request.Facts.EventLoot.TryGetValue(out var lootRows))
        {
            r = r with { Unsafe = EquatableArray.From(PolicyFunctions.UnsafeLoot(lootRows)) };
        }
        else if (!reset)
        {
            r = r with { Unsafe = previous.Unsafe };
        }
        if (!reset)
        {
            r = r with { Built = previous.Built, WallCells = previous.WallCells };
        }
        if (!request.Immediate && request.Facts.CurrentConstruction.TryGetValue(out var census) && census.Colony)
        {
            var cells = EquatableArray.From(WallCellsIn(tx, census));
            r = r with { WallCells = cells };
            AbandonStuckWallRemovals(tx, cells, request.Tick);
        }
        if (!request.Immediate && request.Facts.CurrentConstruction.TryGetValue(out var builtCensus) && builtCensus.Colony)
        {
            r = r with { Built = EquatableArray.From(BuiltActions(tx, builtCensus)) };
        }
        if (request.Enabled && !request.Immediate)
        {
            var reserveSupplies = new List<ReserveSupply>();
            if (request.Facts.FoodReserve.TryGetValue(out var reserve))
            {
                var wanted = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var id in reserve.Hold)
                {
                    wanted[id] = true;
                }
                foreach (var id in reserve.Release)
                {
                    wanted[id] = false;
                }
                _ = request.Facts.FoodStorageUpkeep.Stocks.TryGetValue(out var stocks);
                foreach (var row in stocks)
                {
                    if (wanted.TryGetValue(row.Stock.Id, out var forbid) && PolicyFunctions.ReserveFoodDefinition(row.Stock.DefName))
                    {
                        reserveSupplies.Add(new ReserveSupply(row.Stock.Id, row.Stock.DefName.Value, forbid));
                    }
                }
            }
            r = r with { ReserveSupplies = EquatableArray.From(reserveSupplies) };
            var choice = PolicyFunctions.SelectCorpseLarder(request.Facts.FoodStorageUpkeep);
            if (choice is { Kind: "allow" or "forbid", Stock: { } stock, Handling: { } handling })
            {
                r = r with
                {
                    LarderSupplies = EquatableArray.From([new StartingSupply { Thing = stock.Id, Definition = stock.DefName.Value, Cell = handling.Cell, Forbid = choice.Kind == "forbid" }]),
                };
            }
        }
        r = r with { Comfort = comfort, Sleeping = sleeping, Mood = MoodRecord(mood), Roster = RoundsRoster(request, previous, reset) };
        r = r with { ResourceRunways = ResourceRunwayRecords(request.Facts.ResourceRunways) };
        if (!request.Enabled && !reset)
        {
            r = r with { ResourceRunways = previous.ResourceRunways };
        }
        r = r with { Disaster = disaster };
        if (request.Enabled)
        {
            r = r with { MoodMethods = EquatableArray.From(MoodProposals(mood)) };
        }
        var resultStandards = new List<StandardState>();
        var resultIncidents = new List<IncidentState>();
        // states is every assessment's row in assessment order, as the goal handles ranking and progress read.
        var states = new List<StandardState>();
        if (!request.Enabled)
        {
            r = r with { Standards = previous.Standards };
            if (!changed)
            {
                r = r with { Incidents = previous.Incidents };
                foreach (var binding in r.Incidents)
                {
                    resultIncidents.Add(LoadIncident(tx, binding.Incident));
                }
            }
            r = r with { Latches = latches };
            // Manual control retains progress and prerequisites in the same world.
            if (!reset)
            {
                r = r with { Progress = previous.Progress, Dependencies = previous.Dependencies, NoOps = previous.NoOps };
            }
            foreach (var binding in r.Standards)
            {
                resultStandards.Add(old[binding.Concern]);
            }
        }
        else
        {
            foreach (var n in needs.NoOps)
            {
                n.Reason.Validate();
            }
            r = r with { NoOps = EquatableArray.From(needs.NoOps) };
            var (bindings, incidentStates) = ReviewIncidents(tx, needs.Incidents, request, previous);
            r = r with { Incidents = bindings, Combat = ReviewCombat(tx, request) };
            resultIncidents.AddRange(incidentStates);
            var bound = new List<RoundsStandard>();
            foreach (var n in needs.Assessments)
            {
                var g = old.TryGetValue(n.Id, out var existing) ? existing : null;
                if (g is null || g.Standard.Status == StandardStatus.Voided)
                {
                    var id = MintRoundsStandardId(tx, new World(b.Colony, b.Load, b.Map), n.Id);
                    var goal = Standard.NewStandard(id, n.Id, b) with { Once = PolicyFunctions.IsOnceKind(n.Id) };
                    CreateStandard(tx, goal);
                    g = new StandardState(goal, 0, default, 0, default, false);
                }
                if (n.Finding == Finding.Met)
                {
                    CancelUndispatchedMethods(tx, g);
                }
                var open = StandardOpenWork(tx, g);
                var next = Standard.Review(g.Standard, b, n.Finding, open);
                g = SaveStandard(tx, g, next);
                bound.Add(new RoundsStandard(n.Id, g.Standard.Id));
                resultStandards.Add(g);
                states.Add(g);
            }
            r = r with { Standards = EquatableArray.From(bound) };
        }
        if (request.Enabled && !request.Immediate)
        {
            r = r with { Progress = RoundsProgress(tx, request, previous, reset, needs, states) };
            var records = reset ? default : previous.Dependencies;
            var (ready, dependencies) = ReviewWork(tx, request, needs, states, records);
            var unavailable = new Dictionary<ConcernId, bool>();
            foreach (var n in needs.All())
            {
                unavailable[n.Id] = n.MethodUnavailable;
            }
            foreach (var n in needs.Concerns)
            {
                unavailable[n.Id] = (unavailable.TryGetValue(n.Id, out var prior) && prior) || n.MethodUnavailable;
            }
            r = r with { Dependencies = dependencies, Progress = EquatableArray.From(PolicyFunctions.HoldProgress(r.Progress, unavailable)), ReadyWork = ready };
            r = r with { Recovery = RoundsRecoveryFor(tx, request.Facts, disaster, r, request.Tick) };
        }
        if (!request.Immediate && request.Facts.Upkeep.Clearance.TryGetValue(out var clearance))
        {
            // A disabled review never filtered the census.
            recovery ??= PolicyFunctions.ReviewRecoveryRequest(request.Policy, request.Facts, clearance, request.Facts.Upkeep.RoomObstructions);
            r = r with { RecoveryQueue = PolicyFunctions.RankRecovery(recovery) };
        }
        else if (!reset)
        {
            r = r with { RecoveryQueue = previous.RecoveryQueue };
        }
        var result = new RoundsResult
        {
            Needs = needs,
            Detection = detection,
            Standards = resultStandards,
            Incidents = resultIncidents,
        };
        if (request.Enabled && request.Immediate && !reset)
        {
            (r, result) = RetainOrdinaryRounds(tx, r, previous, result);
        }
        var data = StoreJson.Serialize(r);
        if (System.Text.Encoding.UTF8.GetByteCount(data) > MaxRoundsPayload)
        {
            throw new CapacityException();
        }
        _ = tx.Execute("INSERT INTO rounds(singleton,payload) VALUES(1,$payload) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", ("$payload", data));
        return result with { Review = r };
    }

    /// <summary>
    /// Records the roster planner's report when this review planned work, keeps the previous report while it did
    /// not (a disabled review, an unknown census) and drops it with the rest of the history on a world change
    /// or tick rewind.
    /// </summary>
    private static WorkRosterReport? RoundsRoster(RoundsRequest request, Rounds previous, bool reset)
    {
        if (!request.Enabled || !request.Facts.WorkRoster.TryGetValue(out var coverage))
        {
            return reset ? null : previous.Roster;
        }
        _ = request.Facts.WorkDecaying.TryGetValue(out var decaying);
        _ = request.Facts.WorkProfiles.TryGetValue(out var profiles);
        return new WorkRosterReport
        {
            Tick = request.Tick,
            Coverage = coverage,
            Decaying = decaying,
            Profiles = profiles,
            Help = request.Facts.WorkHelp is { } help && help.Tick <= request.Tick ? help : null,
        };
    }
}

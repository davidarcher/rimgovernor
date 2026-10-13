using System.Text;
using Receipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/boundary (boundary.go, intent.go, facts.go, schedule.go): the native-facing
// building-placement primitives and pawn/receipt fact helpers shared by every action-family boundary.
// Dropped: LeaseSource and its Lease call before every write (the native epoch lease is gone in-process, #3074),
// and fixture.go (a test fake; tests are not ported).

/// <summary>The native reads a building boundary makes (Go <c>boundary.Native</c>).</summary>
public interface IBoundaryNative
{
    Task<EmergencyObservation> ReadEmergencyAsync(PbCommon.Identity identity, CancellationToken ct);

    Task<BuildingPreview> PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct);

    Task<MapBounds> ReadMapBoundsAsync(PbCommon.Identity identity, Cell anchor, CancellationToken ct);
}

/// <summary>Sends intents through Actions/Apply (Go <c>boundary.ActionsWriter</c>, narrowing the bridge writer).</summary>
public interface IActionsWriter
{
    Task<PbOperations.ApplyReply> ApplyAsync(PbCommon.Identity identity, IReadOnlyList<PbOperations.Action> actions, CancellationToken ct);
}

/// <summary>The building boundary of the executor (Go <c>boundary.Boundary</c>).</summary>
public sealed class BuildingBoundary : IBoundary
{
    private readonly IBoundaryNative _native;
    private readonly IActionsWriter _writer;
    private readonly IClock _clock;
    private readonly string _session;

    public BuildingBoundary(IBoundaryNative native, IActionsWriter writer, IClock clock, string controllerSessionId)
    {
        if (!Boundary.ValidId(controllerSessionId))
        {
            throw new DomainException("invalid building boundary dependencies");
        }
        _native = native;
        _writer = writer;
        _clock = clock;
        _session = controllerSessionId;
    }

    public string Session => _session;

    /// <summary>The clock the boundary times its inspections with.</summary>
    public IClock Clock => _clock;

    /// <summary>
    /// Anchors an intent to the current world and tick with one map bounds read (at a building's cell, else the
    /// map origin). It previews nothing: native validates the intent when it applies it.
    /// </summary>
    public async Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct)
    {
        var started = _clock.Now();
        var anchor = target.Action.Building?.Cell ?? default;
        var bounds = await _native.ReadMapBoundsAsync(Boundary.Identity(target.Snapshot), anchor, ct).ConfigureAwait(false);
        var current = Boundary.Context(bounds.Context, target.Snapshot);
        var tick = new Tick(bounds.Context?.Tick ?? 0);
        var observed = _clock.Now();
        ct.ThrowIfCancellationRequested();
        return new IntentInspection { StartedAt = started, Current = current, Tick = tick, ObservedAt = observed };
    }

    /// <summary>Sends the intents through one Actions/Apply call.</summary>
    public Task<IReadOnlyList<Receipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct) =>
        Boundary.DispatchIntentsAsync(placements, _writer, ct);

    public PbCommon.AttemptKey Attempt(Placement p)
    {
        var key = new PbCommon.AttemptKey { ControllerSessionId = _session, ActionId = p.Action.Id.Value };
        key.AttemptId = p.Attempt.Value;
        return key;
    }
}

/// <summary>Shared helpers of the action-family boundaries (Go package-level functions of <c>boundary</c>).</summary>
public static class Boundary
{
    /// <summary>
    /// Whether s is a well-formed opaque identifier: valid UTF-8, non-blank, NUL-free and bounded, as required for
    /// session and native ids.
    /// </summary>
    public static bool ValidId(string? s) =>
        s is not null && !string.IsNullOrWhiteSpace(s) && Encoding.UTF8.GetByteCount(s) <= 256 && !s.Contains('\0', StringComparison.Ordinal);

    /// <summary>
    /// Writes one combat_order decision row per order result (Go <c>bridge.RecordCombatOrder</c>): applied (reason empty,
    /// attrs job) or refused (reason the refusal).
    /// </summary>
    private static void RecordCombatOrder(CombatOrderResult res) =>
        Tel.Decide(new Decision
        {
            Kind = "combat_order",
            Component = "combat-orders",
            Target = res.PawnId,
            Verdict = res.Applied ? "applied" : "refused",
            Reason = res.Applied ? "" : res.Refusal,
            Attrs = res.Applied
                ? new Dictionary<string, object?> { ["index"] = res.Index, ["job"] = res.JobDef }
                : new Dictionary<string, object?> { ["index"] = res.Index },
        });

    public static PbCommon.Identity Identity(GenerationSnapshot s) =>
        new() { ColonyId = s.Colony.Value, LoadToken = s.Load.Value, MapId = s.Map.Value };

    public static bool World(GenerationSnapshot a, GenerationSnapshot b) => a.Colony == b.Colony && a.Load == b.Load && a.Map == b.Map;

    /// <summary>
    /// Validates an observation context against the expected snapshot and returns the snapshot it describes.
    /// Throws <see cref="AuthorityException"/> when the native generation or the world differs.
    /// </summary>
    public static GenerationSnapshot Context(PbCommon.ObservationContext? v, GenerationSnapshot expected)
    {
        BridgeApi.ValidateContext(v);
        if (v is null || !v.HasNativeGeneration || v.NativeGeneration != expected.Native.Value)
        {
            throw new AuthorityException();
        }
        var output = expected with
        {
            Colony = new ColonyId(v.Identity.ColonyId),
            Load = new LoadId(v.Identity.LoadToken),
            Map = new MapId(v.Identity.MapId),
            Native = new NativeGeneration(v.NativeGeneration),
        };
        if (!World(output, expected))
        {
            throw new AuthorityException();
        }
        return output;
    }

    /// <summary>Checks that a receipt answers exactly the placement's attempt and was admitted in its world.</summary>
    public static void Admission(PbReceipts.Receipt? receipt, Placement placement, string session)
    {
        if (receipt is null || receipt.Attempt is null || receipt.Attempt.ControllerSessionId != session
            || receipt.Attempt.ActionId != placement.Action.Id.Value || receipt.Attempt.AttemptId != placement.Attempt.Value)
        {
            throw new EvidenceException();
        }
        _ = Context(receipt.AdmittedContext, placement.Snapshot);
    }

    /// <summary>
    /// Resolves a receipt-free ledger lookup under the attempt's load. Admission and lookup share the native main
    /// thread, so a missing entry proves no effect occurred and permits a fresh Pending attempt. In-flight
    /// entries remain held until their receipts arrive.
    /// </summary>
    public static Domain.Observation Unadmitted(PbReceipts.LookupReply lookup, Placement placement, GenerationSnapshot current)
    {
        var output = new Domain.Observation(placement.Action.Id, placement.Attempt, current, default, Effect.Unknown);
        switch (lookup.OutcomeCase)
        {
            case PbReceipts.LookupReply.OutcomeOneofCase.InFlight:
                throw new HeldException($"attempt {placement.Attempt} is admitted and still in flight");
            case PbReceipts.LookupReply.OutcomeOneofCase.Unknown:
                var observed = Context(lookup.Unknown.Context, current);
                if (lookup.Unknown.Context.Tick < placement.Tick.Value)
                {
                    throw new EvidenceException();
                }
                return output with { Snapshot = observed, Tick = new Tick(lookup.Unknown.Context.Tick), Causality = ObservationCausality.AfterDispatch, Effect = Effect.Absent };
            default:
                throw new EvidenceException();
        }
    }

    // ---- facts.go: decode typed native pawn/receipt evidence into domain facts. ----

    public static Fact<bool> FactBool(bool has, bool value) => has ? Known(value) : Unknown<bool>();

    public static Fact<uint> FactUint(bool has, uint value) => has ? Known(value) : Unknown<uint>();

    /// <summary>
    /// Handles fields (like MentalState) that native leaves unset, rather than reporting a false-ish value, when
    /// the fact is genuinely known not to apply. An unset value is only unknown when no matching NOT_APPLICABLE
    /// issue confirms the omission; otherwise it is known-absent.
    /// </summary>
    public static Fact<bool> FactPresence(bool has, IEnumerable<PbObservations.ReadIssue> issues, string field)
    {
        if (has)
        {
            return Known(true);
        }
        foreach (var issue in issues)
        {
            if (issue.Field == field && issue.Unavailable?.Reason == PbCommon.UnavailableReason.NotApplicable)
            {
                return Known(false);
            }
        }
        return Unknown<bool>();
    }

    public static string PawnToken(PbObservations.PawnState row, PbCommon.ObservationContext ctx)
    {
        var reference = row.PawnSnapshot;
        if (reference is null || reference.EntityId != row.Pawn?.Id || !reference.Context.Equals(ctx) || !ValidId(reference.Token))
        {
            throw new HeldException();
        }
        return reference.Token;
    }

    public static PbReceipts.JobEffect? ReceiptJob(PbReceipts.Receipt? receipt) => receipt?.OutcomeCase switch
    {
        PbReceipts.Receipt.OutcomeOneofCase.Applied => receipt.Applied.Observed?.Job,
        PbReceipts.Receipt.OutcomeOneofCase.NoChange => receipt.NoChange.Observed?.Job,
        PbReceipts.Receipt.OutcomeOneofCase.Uncertain => receipt.Uncertain.LastObserved?.Job,
        _ => null,
    };

    /// <summary>Whether issues names field: the shared "unresolved native issue" check of every pawn-order boundary.</summary>
    public static bool IssueField(IEnumerable<PbObservations.ReadIssue> issues, string field) => issues.Any(issue => issue.Field == field);

    /// <summary>
    /// Whether the observed timetable slots are exactly the wanted hour-indexed assignment list: every hour present
    /// once with that def, nothing else.
    /// </summary>
    public static bool ScheduleMatches(IReadOnlyList<PbObservations.TimetableSlot> slots, IReadOnlyList<string> wanted)
    {
        if (wanted.Count != 24 || slots.Count != 24)
        {
            return false;
        }
        var seen = new bool[24];
        foreach (var slot in slots)
        {
            if (slot is null || !slot.HasHour || !slot.HasAssignmentDefName || slot.Hour >= 24 || seen[slot.Hour] || slot.AssignmentDefName != wanted[(int)slot.Hour])
            {
                return false;
            }
            seen[slot.Hour] = true;
        }
        return true;
    }

    // ---- intent.go: dispatch an intent-mode action through Actions/Apply. ----

    /// <summary>
    /// An intent-mode attempt's idempotency key: the action and attempt. A stockpile rectangle uses only its
    /// immutable action id so a lost reply recovers every created identity from native replay. The action, not
    /// the plan, keys it: a plan's building actions each start at attempt 1, and the native replay window would
    /// answer the second with the first's result.
    /// </summary>
    public static string IntentKey(Placement p)
    {
        if (p.Action.CombatBatch is { } batch)
        {
            return batch.Key;
        }
        if (p.Action.ZoneCreate is { } z && z.Kind == Domain.ZoneKind.Stockpile)
        {
            return p.Action.Id.Value;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Action.Id}/{p.Attempt}");
    }

    /// <summary>Sends one intent-mode action through Actions/Apply: the one-element case of <see cref="DispatchIntentsAsync"/>.</summary>
    public static async Task<Receipt> DispatchIntentAsync(Placement p, IActionsWriter writer, CancellationToken ct) =>
        (await DispatchIntentsAsync([p], writer, ct).ConfigureAwait(false))[0];

    /// <summary>
    /// Sends intent-mode actions of one world in one Actions/Apply call. Only the identity is checked on the wire;
    /// native validates each intent against live state. Receipts are in input order: applied is accepted; refused
    /// and failed are refused, except an attempt conflict; a batch failure or a lost reply leaves every receipt
    /// unknown, which an idempotent intent may resend.
    /// </summary>
    public static async Task<IReadOnlyList<Receipt>> DispatchIntentsAsync(IReadOnlyList<Placement> placements, IActionsWriter writer, CancellationToken ct)
    {
        var output = new Receipt[placements.Count];
        for (var i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            output[i] = new Receipt { Action = p.Action.Id, Attempt = p.Attempt, Snapshot = p.Snapshot, Kind = Domain.Receipt.Unknown };
        }
        if (placements.Count == 0)
        {
            return output;
        }
        var snapshot = placements[0].Snapshot;
        var actions = new PbOperations.Action[placements.Count];
        for (var i = 0; i < placements.Count; i++)
        {
            if (!World(placements[i].Snapshot, snapshot))
            {
                throw new AuthorityException();
            }
            actions[i] = BridgeApi.IntentAction(IntentKey(placements[i]), placements[i].Action);
        }
        var reply = await writer.ApplyAsync(Identity(snapshot), actions, ct).ConfigureAwait(false);
        var results = reply.Results;
        if (results.Count != placements.Count)
        {
            throw new EvidenceException();
        }
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var applied = result.Applied?.Applied;
            if (applied is not null)
            {
                output[i].Kind = Domain.Receipt.Accepted;
                if (placements[i].Action.Kind == ActionKind.CombatBatch)
                {
                    foreach (var r in BridgeApi.CombatOrderResults(result.Applied!, actions[i].CombatOrders))
                    {
                        RecordCombatOrder(r);
                        output[i].Combat.Add(new CombatResult { Index = r.Index, PawnId = r.PawnId, Applied = r.Applied, Refusal = r.Refusal, JobDef = r.JobDef });
                    }
                }
                if (placements[i].Action.Kind == ActionKind.ZoneCreate)
                {
                    var effect = applied.Observed?.Zone;
                    var zone = placements[i].Action.ZoneCreate!;
                    if (zone.Kind == Domain.ZoneKind.Stockpile)
                    {
                        foreach (var row in effect?.Created ?? [])
                        {
                            var cells = new List<Cell>();
                            foreach (var c in row.Cells)
                            {
                                if (c is null || !c.HasX || !c.HasZ)
                                {
                                    throw new EvidenceException();
                                }
                                cells.Add(new Cell(c.X, c.Z));
                            }
                            output[i].Stockpiles.Add(new CreatedZone(row.ZoneId, cells));
                        }
                        try
                        {
                            _ = new CreatedZones(zone, output[i].Stockpiles);
                        }
                        catch (DomainException)
                        {
                            throw new EvidenceException();
                        }
                    }
                    else
                    {
                        output[i].Zone = effect?.ZoneId ?? "";
                    }
                }
                var kind = placements[i].Action.Kind;
                if (kind is ActionKind.ProductionBill or ActionKind.Surgery)
                {
                    output[i].Bill = AppliedBill(applied.Observed);
                }
            }
            else if (result.Refused is { } refused)
            {
                output[i].Kind = Domain.Receipt.Refused;
                output[i].Refusal = NativeRefusalOf(refused.Code, refused.Reason, refused.RefusalClass);
            }
            else if (result.Failed is { } failed && failed.Code != PbCommon.FailureCode.AttemptConflict)
            {
                output[i].Kind = Domain.Receipt.Refused;
                output[i].Refusal = NativeRefusalOf(failed.Code, failed.Detail, failed.RefusalClass);
            }
        }
        return output;
    }

    /// <summary>The native bill id a bill-placing receipt's evidence names: BillEffect for an ordinary or mech bill, SurgeryEffect for a medical one.</summary>
    private static string AppliedBill(PbReceipts.EffectEvidence? evidence)
    {
        var id = evidence?.Bill?.Bill?.Id ?? "";
        return id.Length != 0 ? id : evidence?.SurgeryBill?.Bill?.Id ?? "";
    }

    /// <summary>A refused receipt's journal record: native's failure code, its reason text and the class it assigned. A refusal naming no class is unknown.</summary>
    private static NativeRefusal NativeRefusalOf(PbCommon.FailureCode code, string reason, PbCommon.RefusalClass cls) =>
        new(ProtoNames.Of(code), reason, cls switch
        {
            PbCommon.RefusalClass.Permanent => RefusalClass.Permanent,
            PbCommon.RefusalClass.Transient => RefusalClass.Transient,
            _ => RefusalClass.Unknown,
        });
}

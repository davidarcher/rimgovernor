using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>
/// There is one author of orders. Control is a pause flag: <see cref="Resume"/> enables autonomous play for a
/// world, <see cref="Pause"/> halts it.
/// </summary>
public enum ControlKind
{
    Resume,
    Pause,
}

public enum ControlPhase
{
    Pending,
    Running,
    Paused,
    Refused,
    Uncertain,
}

public sealed record ControlRequest(string RequestId, ControlKind Kind, World World)
{
    internal void Validate()
    {
        Submissions.ValidateId(RequestId);
        World.Validate();
        DomainException.Require(Wire.IsDefined(Kind), "unknown control kind");
    }
}

/// <summary>
/// Journals one control intent and its outcome. Records are ordered by insertion; there is no compare-and-swap
/// between them because there is one author of orders.
/// </summary>
public sealed record ControlRecord(ControlRequest Request, ControlPhase Phase, NativeGeneration NativeGeneration);

public sealed partial class Store
{
    private const int ControlCapacity = 4096;

    private const string ControlColumns = "request_id,kind,colony,load_token,map_id,phase,native_generation";

    internal static bool ValidControlResult(ControlKind kind, ControlPhase phase, NativeGeneration generation) =>
        phase switch
        {
            ControlPhase.Running => kind == ControlKind.Resume && generation.Value > 0,
            ControlPhase.Paused => kind == ControlKind.Pause && generation.Value == 0,
            ControlPhase.Refused or ControlPhase.Uncertain => generation.Value == 0,
            _ => false,
        };

    private static ControlRecord? ScanControl(StoreTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var (row, found) = tx.QueryFirst(
            sql,
            parameters,
            r => (Id: r.GetString(0), Kind: r.GetString(1), Colony: r.GetString(2), Load: r.GetString(3), Map: r.GetInt32(4), Phase: r.GetString(5), Generation: r.UnsignedText(6))
        );
        if (!found)
        {
            return null;
        }
        if (!Wire.TryParse<ControlKind>(row.Kind, out var kind) || !Wire.TryParse<ControlPhase>(row.Phase, out var phase))
        {
            throw new StoreCorruptException("corrupt control kind or phase");
        }
        var record = new ControlRecord(new ControlRequest(row.Id, kind, new World(new ColonyId(row.Colony), new LoadId(row.Load), new MapId(row.Map))), phase, new NativeGeneration(row.Generation));
        record.Request.Validate();
        if (phase == ControlPhase.Pending ? record.NativeGeneration.Value != 0 : !ValidControlResult(kind, phase, record.NativeGeneration))
        {
            throw new StoreCorruptException("corrupt control result");
        }
        return record;
    }

    internal static ControlRecord? LookupControlIn(StoreTransaction tx, string id) =>
        ScanControl(tx, "SELECT " + ControlColumns + " FROM control_intents WHERE request_id=$id", ("$id", id));

    internal static ControlRecord? CurrentControlIn(StoreTransaction tx) =>
        ScanControl(tx, "SELECT " + ControlColumns + " FROM control_intents ORDER BY rowid DESC LIMIT 1");

    /// <summary>
    /// Journals intent before native work. Replay is historical evidence, never permission to dispatch again;
    /// unresolved Pending records remain uncertain. <c>Created</c> is false on a replay.
    /// </summary>
    public (ControlRecord Record, bool Created) BeginControl(ControlRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return Run(
            tx =>
            {
                if (LookupControlIn(tx, request.RequestId) is { } old)
                {
                    return old.Request == request ? (old, false) : throw new ConflictException();
                }
                if (tx.Scalar("SELECT count(*) FROM control_intents") >= ControlCapacity)
                {
                    throw new CapacityException();
                }
                _ = tx.Execute(
                    "INSERT INTO control_intents(" + ControlColumns + ") VALUES($id,$kind,$colony,$load,$map,$phase,'0')",
                    ("$id", request.RequestId),
                    ("$kind", Wire.Name(request.Kind)),
                    ("$colony", request.World.Colony),
                    ("$load", request.World.Load),
                    ("$map", request.World.Map),
                    ("$phase", Wire.Name(ControlPhase.Pending))
                );
                return (new ControlRecord(request, ControlPhase.Pending, default), true);
            },
            cancellationToken
        );
    }

    public Task<(ControlRecord Record, bool Created)> BeginControlAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return Task.Run(() => BeginControl(request, cancellationToken), cancellationToken);
    }

    public ControlRecord LookupControl(string id, CancellationToken cancellationToken)
    {
        Submissions.ValidateId(id);
        return Run(tx => LookupControlIn(tx, id) ?? throw new NotFoundException(), cancellationToken);
    }

    public Task<ControlRecord> LookupControlAsync(string id, CancellationToken cancellationToken) => Task.Run(() => LookupControl(id, cancellationToken), cancellationToken);

    /// <summary>The newest control record; <see cref="NotFoundException"/> when none was ever journaled.</summary>
    public ControlRecord CurrentControl(CancellationToken cancellationToken) => Run(tx => CurrentControlIn(tx) ?? throw new NotFoundException(), cancellationToken);

    public Task<ControlRecord> CurrentControlAsync(CancellationToken cancellationToken) => Task.Run(() => CurrentControl(cancellationToken), cancellationToken);

    public ControlRecord CompleteControl(string id, ControlPhase phase, NativeGeneration generation, CancellationToken cancellationToken)
    {
        Submissions.ValidateId(id);
        return Run(
            tx =>
            {
                var r = LookupControlIn(tx, id) ?? throw new NotFoundException();
                DomainException.Require(ValidControlResult(r.Request.Kind, phase, generation), "invalid control completion");
                if (r.Phase != ControlPhase.Pending)
                {
                    return r.Phase == phase && r.NativeGeneration == generation ? r : throw new ConflictException();
                }
                _ = tx.Execute(
                    "UPDATE control_intents SET phase=$phase,native_generation=$generation WHERE request_id=$id",
                    ("$phase", Wire.Name(phase)),
                    ("$generation", generation.Value.ToString(CultureInfo.InvariantCulture)),
                    ("$id", id)
                );
                return r with { Phase = phase, NativeGeneration = generation };
            },
            cancellationToken
        );
    }

    public Task<ControlRecord> CompleteControlAsync(string id, ControlPhase phase, NativeGeneration generation, CancellationToken cancellationToken) =>
        Task.Run(() => CompleteControl(id, phase, generation, cancellationToken), cancellationToken);

    /// <summary>
    /// Names the empty plan whose identity carries autonomous authority for one world. Routine methods and player
    /// submissions are authorized against it; it never holds actions of its own.
    /// </summary>
    public static PlanId RootPlanId(World w) => new($"root/{w.Colony.Value}/{w.Load.Value}/{w.Map.Value.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The world's root plan, created on first Resume.</summary>
    public PlanState EnsureRootPlan(World w, CancellationToken cancellationToken)
    {
        w.Validate();
        var id = RootPlanId(w);
        return Run(
            tx =>
            {
                try
                {
                    return LoadIn(tx, id);
                }
                catch (NotFoundException)
                {
                    CreatePlanIn(tx, new PlanSpec(id, new PlanRevision(1), []));
                    return LoadIn(tx, id);
                }
            },
            cancellationToken
        );
    }

    public Task<PlanState> EnsureRootPlanAsync(World w, CancellationToken cancellationToken) => Task.Run(() => EnsureRootPlan(w, cancellationToken), cancellationToken);
}

internal static partial class StoreSchema
{
    internal static readonly SchemaPart ControlPart = new(
        "CREATE TABLE control_intents(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, phase TEXT NOT NULL, native_generation TEXT NOT NULL) STRICT;",
        ["SELECT request_id,kind,colony,load_token,map_id,phase,native_generation FROM control_intents LIMIT 0"]
    );
}

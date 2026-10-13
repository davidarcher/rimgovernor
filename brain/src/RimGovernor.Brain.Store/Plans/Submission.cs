using System.Globalization;
using System.Security.Cryptography;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>A player's building intent, keyed by an opaque request id.</summary>
public sealed record SubmissionRequest(string RequestId, World World, Building Building)
{
    internal void Validate()
    {
        Submissions.ValidateId(RequestId);
        World.Validate();
        _ = new Building(Building.Definition, Building.Cell, Building.Rotation, Building.Stuff);
    }
}

public sealed record Submission(SubmissionRequest Request, PlanId Plan, ActionId Action, PlanRevision Revision);

internal sealed record SubmissionHeader(string Kind, World World, PlanId Plan, ActionId Action, PlanRevision Revision);

internal static class Submissions
{
    /// <summary>A submission or control id reuses the plan-id format rules to validate an opaque token's character set and length.</summary>
    public static void ValidateId(string id) => _ = new PlanSpec(new PlanId(id), new PlanRevision(1), []);
}

public sealed partial class Store
{
    private const string BuildingKind = "building";

    /// <summary>
    /// Commits one immutable player intent and its plan together. Replay never creates another plan or grants
    /// authority; equality is typed semantics. <c>Created</c> is false on a replay.
    /// </summary>
    public (Submission Submission, bool Created) SubmitBuilding(SubmissionRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return Run(
            tx =>
            {
                if (LookupSubmissionIn(tx, request.RequestId) is { } existing)
                {
                    return existing.Request == request ? (existing, false) : throw new ConflictException();
                }
                var entropy = RandomNumberGenerator.GetBytes(32);
                var result = new Submission(
                    request,
                    new PlanId("building-" + Convert.ToHexStringLower(entropy.AsSpan(0, 16))),
                    new ActionId("building-action-" + Convert.ToHexStringLower(entropy.AsSpan(16))),
                    new PlanRevision(1)
                );
                var action = DomainAction.NewBuildingAction(result.Action, request.Building, ConstructionTier.Expand);
                CreatePlanIn(tx, new PlanSpec(result.Plan, result.Revision, [action]));
                _ = tx.ExecuteOrConflict(
                    "INSERT INTO submissions(request_id,kind,colony,load_token,map_id,plan_id,action_id,revision) VALUES($id,$kind,$colony,$load,$map,$plan,$action,'1')",
                    ("$id", request.RequestId),
                    ("$kind", BuildingKind),
                    ("$colony", request.World.Colony),
                    ("$load", request.World.Load),
                    ("$map", request.World.Map),
                    ("$plan", result.Plan),
                    ("$action", result.Action)
                );
                var b = request.Building;
                _ = tx.ExecuteOrConflict(
                    "INSERT INTO building_submissions(request_id,definition,x,z,rotation,stuff) VALUES($id,$definition,$x,$z,$rotation,$stuff)",
                    ("$id", request.RequestId),
                    ("$definition", b.Definition),
                    ("$x", b.Cell.X),
                    ("$z", b.Cell.Z),
                    ("$rotation", Wire.Name(b.Rotation)),
                    ("$stuff", b.Stuff)
                );
                return (result, true);
            },
            cancellationToken
        );
    }

    public Task<(Submission Submission, bool Created)> SubmitBuildingAsync(SubmissionRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        return Task.Run(() => SubmitBuilding(request, cancellationToken), cancellationToken);
    }

    public Submission LookupSubmission(string requestId, CancellationToken cancellationToken)
    {
        Submissions.ValidateId(requestId);
        return Run(tx => LookupSubmissionIn(tx, requestId) ?? throw new NotFoundException(), cancellationToken);
    }

    public Task<Submission> LookupSubmissionAsync(string requestId, CancellationToken cancellationToken) =>
        Task.Run(() => LookupSubmission(requestId, cancellationToken), cancellationToken);

    private static SubmissionHeader? LookupSubmissionHeader(StoreTransaction tx, string id, string kind)
    {
        var (row, found) = tx.QueryFirst(
            "SELECT kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions WHERE request_id=$id",
            [("$id", id)],
            r => (Kind: r.GetString(0), Colony: r.GetString(1), Load: r.GetString(2), Map: r.GetInt32(3), Plan: r.GetString(4), Action: r.GetString(5), Revision: r.GetString(6))
        );
        if (!found)
        {
            return null;
        }
        if (row.Revision != "1")
        {
            throw new StoreCorruptException("invalid submitted revision");
        }
        if (row.Kind != BuildingKind)
        {
            throw new StoreCorruptException("invalid submission kind");
        }
        if (kind.Length > 0 && row.Kind != kind)
        {
            throw new ConflictException();
        }
        var header = new SubmissionHeader(row.Kind, new World(new ColonyId(row.Colony), new LoadId(row.Load), new MapId(row.Map)), new PlanId(row.Plan), new ActionId(row.Action), new PlanRevision(1));
        header.World.Validate();
        return header;
    }

    internal static Submission? LookupSubmissionIn(StoreTransaction tx, string id)
    {
        if (LookupSubmissionHeader(tx, id, BuildingKind) is not { } h)
        {
            return null;
        }
        var (row, found) = tx.QueryFirst(
            "SELECT definition,x,z,rotation,stuff FROM building_submissions WHERE request_id=$id",
            [("$id", id)],
            r => (Definition: r.GetString(0), X: r.GetInt32(1), Z: r.GetInt32(2), Rotation: r.GetString(3), Stuff: r.GetString(4))
        );
        if (!found)
        {
            return null;
        }
        if (!Wire.TryParse<Rotation>(row.Rotation, out var rotation))
        {
            throw new StoreCorruptException("invalid submitted rotation");
        }
        var request = new SubmissionRequest(id, h.World, new Building(row.Definition, new Cell(row.X, row.Z), rotation, row.Stuff));
        request.Validate();
        var state = LoadIn(tx, h.Plan);
        var actions = state.Spec.Actions;
        if (state.Spec.Revision.Value != 1 || actions.Count != 1 || actions[0].Id != h.Action)
        {
            throw new StoreCorruptException("submission plan identity is corrupt");
        }
        return actions[0].Building == request.Building ? new Submission(request, h.Plan, h.Action, h.Revision) : throw new StoreCorruptException("submission plan differs from intent");
    }

    /// <summary>
    /// Verifies that <paramref name="target"/> is a player-submitted plan for the root world so it may be dispatched
    /// under the root's authority. There is one author of orders: a submission is guidance the bot executes, not a
    /// separate grant, so it needs no control record of its own. Throws <see cref="ConflictException"/> otherwise.
    /// </summary>
    public void AuthorizePlayerPlan(GenerationSnapshot root, GenerationSnapshot target, CancellationToken cancellationToken)
    {
        if (!root.IsValid || !target.IsValid || root.Native.Value == 0 || root.Plan == target.Plan)
        {
            throw new ConflictException();
        }
        if (target with { Plan = root.Plan, Revision = root.Revision } != root)
        {
            throw new ConflictException();
        }
        Run(
            tx =>
            {
                var id = tx.ScalarText("SELECT request_id FROM submissions WHERE plan_id=$plan", ("$plan", target.Plan)) ?? throw new ConflictException();
                var h = LookupSubmissionHeader(tx, id, "") ?? throw new ConflictException();
                if (LookupSubmissionIn(tx, id) is null)
                {
                    throw new NotFoundException();
                }
                if (h.World != new World(root.Colony, root.Load, root.Map) || h.Revision != target.Revision)
                {
                    throw new ConflictException();
                }
                if (tx.Scalar("SELECT retired FROM plans WHERE id=$plan", ("$plan", target.Plan)) != 0)
                {
                    throw new ConflictException();
                }
            },
            cancellationToken
        );
    }

    public Task AuthorizePlayerPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken cancellationToken) =>
        Task.Run(() => AuthorizePlayerPlan(root, target, cancellationToken), cancellationToken);

    /// <summary>
    /// Lists the player guidance plans for one world with their submitted revisions, so reviewers can treat them as
    /// selected intent under the world's root plan.
    /// </summary>
    public IReadOnlyDictionary<PlanId, ulong> PlayerPlans(World w, CancellationToken cancellationToken)
    {
        w.Validate();
        return Run(
            tx =>
                (IReadOnlyDictionary<PlanId, ulong>)
                    tx.Query(
                            "SELECT plan_id,revision FROM submissions WHERE kind='building' AND colony=$colony AND load_token=$load AND map_id=$map",
                            [("$colony", w.Colony), ("$load", w.Load), ("$map", w.Map)],
                            r => (Plan: new PlanId(r.GetString(0)), Revision: ulong.Parse(r.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture))
                        )
                        .ToDictionary(p => p.Plan, p => p.Revision),
            cancellationToken
        );
    }

    public Task<IReadOnlyDictionary<PlanId, ulong>> PlayerPlansAsync(World w, CancellationToken cancellationToken) => Task.Run(() => PlayerPlans(w, cancellationToken), cancellationToken);
}

internal static partial class StoreSchema
{
    internal static readonly SchemaPart SubmissionPart = new(
        """
        CREATE TABLE submissions(request_id TEXT PRIMARY KEY, kind TEXT NOT NULL, colony TEXT NOT NULL, load_token TEXT NOT NULL, map_id INTEGER NOT NULL, plan_id TEXT NOT NULL UNIQUE REFERENCES plans(id), action_id TEXT NOT NULL UNIQUE REFERENCES actions(id), revision TEXT NOT NULL) STRICT;
        CREATE TABLE building_submissions(request_id TEXT PRIMARY KEY REFERENCES submissions(request_id), definition TEXT NOT NULL, x INTEGER NOT NULL, z INTEGER NOT NULL, rotation TEXT NOT NULL, stuff TEXT NOT NULL) STRICT;
        """,
        [
            "SELECT request_id,kind,colony,load_token,map_id,plan_id,action_id,revision FROM submissions LIMIT 0",
            "SELECT request_id,definition,x,z,rotation,stuff FROM building_submissions LIMIT 0",
        ]
    );
}

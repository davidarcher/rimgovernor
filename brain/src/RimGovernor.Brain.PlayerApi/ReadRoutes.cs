using RimGovernor.Brain.Domain;
using RimGovernor.Brain.HttpChannel;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The read-only controller routes (Go <c>httpapi</c> <c>server.go</c> and <c>player.go</c>): state, one plan, the
/// routine diagnostics and the colony census. Like every route here they carry no token; the channel's request guard
/// is the only access check (issue #3083). A route whose provider is not given is not registered, so it answers 404.
/// </summary>
public static class ReadRoutes
{
    public const string StatePath = "/api/state";

    public const string PlanPath = "/api/plan";

    public const string RoutinesPath = "/api/routines";

    public const string ColonyPath = "/api/player/colony";

    private static readonly RouteOptions Bounded = new() { MaxBodyBytes = StrictJson.Limit };

    /// <summary>Registers the state and plan routes, and the routines and colony routes when their providers are given; dispose the result to remove them all.</summary>
    public static IDisposable Register(RouteTable routes, ISnapshotProvider snapshots, IPlanReader plans, IRoundsProvider? rounds, IColonyStatusReader? colony)
    {
        var registrations = new List<IDisposable>
        {
            routes.Map("GET", StatePath, (request, ct) => StateAsync(snapshots, request, ct), Bounded),
            routes.Map("GET", PlanPath, (request, ct) => PlanAsync(plans, request, ct), Bounded),
        };
        if (rounds is not null)
        {
            registrations.Add(routes.Map("GET", RoutinesPath, (request, ct) => RoutinesAsync(rounds, request, ct), Bounded));
        }

        if (colony is not null)
        {
            registrations.Add(routes.Map("GET", ColonyPath, (request, ct) => ColonyAsync(colony, request, ct), Bounded));
        }

        return new Registrations(registrations);
    }

    private static async Task<ChannelResponse> StateAsync(ISnapshotProvider snapshots, ChannelRequest request, CancellationToken ct)
    {
        if (request.Query.Length != 0)
        {
            return ChannelResponse.Failure(400, "invalid_query", "This route accepts no query parameters");
        }

        try
        {
            var snapshot = await snapshots.SnapshotAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return ChannelResponse.Json(200, PlayerJson.Serialize(ReadProjections.State(snapshot)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> RoutinesAsync(IRoundsProvider rounds, ChannelRequest request, CancellationToken ct)
    {
        if (request.Query.Length != 0)
        {
            return ChannelResponse.Failure(400, "invalid_query", "This route accepts no query parameters");
        }

        try
        {
            var status = await rounds.RoundsStatusAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return ChannelResponse.Json(200, PlayerJson.Serialize(ReadProjections.Rounds(status)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> PlanAsync(IPlanReader plans, ChannelRequest request, CancellationToken ct)
    {
        // Exactly one parameter, named id: a second parameter (or a repeated id) is not a plan read.
        var parts = request.Query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var id = parts.Length == 1 && parts[0].StartsWith("id=", StringComparison.Ordinal) ? request.QueryValue("id") : null;
        if (id is null || string.IsNullOrWhiteSpace(id) || id.Length > 256 || System.Text.Encoding.UTF8.GetByteCount(id) > 256)
        {
            return ChannelResponse.Failure(400, "invalid_query", "One explicit plan id is required");
        }

        try
        {
            var stored = await plans.LoadPlanAsync(new PlanId(id), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return ChannelResponse.Json(200, PlayerJson.Serialize(ReadProjections.Plan(stored)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ReadFailure(e);
        }
    }

    private static async Task<ChannelResponse> ColonyAsync(IColonyStatusReader colony, ChannelRequest request, CancellationToken ct)
    {
        if (request.Query.Length != 0)
        {
            return ChannelResponse.Failure(400, "invalid_request", "Colony status accepts no query");
        }

        try
        {
            var report = await colony.ReadAsync(ct).ConfigureAwait(false);
            return ChannelResponse.Json(200, PlayerJson.Serialize(ReadProjections.Colony(report)));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A diagnostic read names its cause: the harness records the body, and "unavailable" alone cannot be acted on.
            return ChannelResponse.Failure(503, "unavailable", "Colony status read failed: " + e.Message);
        }
    }

    /// <summary>Go <c>readFailure</c> for the reads that are not cancellations: an unknown plan is 404, anything else is unavailable.</summary>
    internal static ChannelResponse ReadFailure(Exception cause) => cause is NotFoundException
        ? ChannelResponse.Failure(404, "not_found", "Plan not found")
        : ChannelResponse.Failure(503, "unavailable", "Controller data is unavailable");

    internal sealed class Registrations(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}

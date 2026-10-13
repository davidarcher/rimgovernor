using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What the clearance cases share (Go <c>clearance.go</c>): the service lifecycle helpers and the native clearance reads.</summary>
internal static class ClearanceKit
{
    /// <summary>The wait the clearance cases poll the service journal under.</summary>
    internal static Wait ServiceWait(ServiceProcess service) => new()
    {
        Ceiling = TimeSpan.FromMinutes(2),
        Stall = TimeSpan.FromSeconds(60),
        Interval = TimeSpan.FromSeconds(1),
        Terminal = service.Exited,
    };

    /// <summary>Starts the case's declared serve spec and takes and keeps authority; a failed acquire stops the service.</summary>
    internal static async Task<ServiceProcess> StartAsync(ISession s, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        service.KeepAuthority(cancellationToken);
        return service;
    }

    /// <summary>Takes the game back from a stopped service and pauses it for the audit.</summary>
    internal static async Task ReattachAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Reattach();
        _ = await h.CallAsync("pause-audit", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The native clearance target census.</summary>
    internal static Task<JsonObject> CensusAsync(ISession s, string label, CancellationToken cancellationToken) =>
        TradeKit.ObserveAsync(s.Harness, label, "observations_get_clearance_targets", s.Identity, null, cancellationToken);

    /// <summary>The native chunk audit of the staged fixture, recorded on the report under <paramref name="label"/>.</summary>
    internal static async Task<JsonObject> AuditAsync(ISession s, JsonObject fixture, string label, CancellationToken cancellationToken)
    {
        var chunks = string.Join(';', fixture["chunks"].AsSlice().Select(raw => raw.AsString()));
        var result = await s.Harness.CallAsync(label, "test/clearance_audit", new JsonObject { ["target"] = fixture["target"].AsString(), ["chunks"] = chunks }, cancellationToken).ConfigureAwait(false);
        s.Report[label] = result.DeepClone();
        return result;
    }

    /// <summary>The JSON text of a value; a missing node reads <c>null</c>.</summary>
    internal static string Show(JsonNode? node) => node is null ? "null" : node.ToJsonString();
}

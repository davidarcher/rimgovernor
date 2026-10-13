using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>smoke/identity</c> (Go <c>identity.go</c>): the shared runner opens the blank lab (#731), freezes needs and hands
/// the case a loaded identity; the clock advances, the lab census reads blank, and the typed clearance and shrine reads
/// decode against the live game.
/// </summary>
internal static class SmokeIdentity
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "smoke/identity",
        Scope = "Runner smoke: the shared runner opens the blank lab (#731), freezes needs and hands the case a loaded identity.",
        Start = new LabStart(),

        // Boot plus a handful of reads on a kept process.
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var identity = s.Identity;
        foreach (var key in new[] { "colonyId", "loadToken" })
        {
            if (identity[key].AsString().Length == 0)
            {
                throw new AcceptanceException($"identity has no {key}: {identity.ToJson()}");
            }
        }

        if (!Dyn.Contains(s.Names, "rimgovernor/operations_apply"))
        {
            throw new AcceptanceException("missing rimgovernor/operations_apply in discovery");
        }

        // A second read through the same session must agree with the runner's: the game is loaded and stable under
        // the case.
        var h = s.Harness;
        var reply = await h.WireAsync("identity-again", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        var again = loaded["context"].Member("identity").AsMap();
        if (again?["loadToken"].AsString() != identity["loadToken"].AsString())
        {
            throw new AcceptanceException($"loadToken changed under the case: {identity["loadToken"].ToJson()} then {again?["loadToken"].ToJson()}");
        }

        s.Report["identity_again"] = again.DeepClone();

        // The clock advances: AdvanceGame fails unless the native tick budget completes exactly, so a game that loads
        // but does not tick (a frozen 1.7 main loop) fails here, not in a later case.
        try
        {
            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"the clock did not advance 300 ticks: {e.Message}", e);
        }

        // Exercise the typed read against the live game, including the complete-empty result on a fresh map. This does
        // not admit removal. The census scopes the scan to Home; on the lab it reads an empty map.
        var census = await h.CallAsync("map-census", "test/debug_map_census", [], cancellationToken).ConfigureAwait(false);
        s.Report["map_census"] = census.DeepClone();
        var cells = census["mapCells"].AsNumber();
        var buildings = census["nonPlayerBuildings"].AsNumber();
        if (cells != LabWorld.LabMapSize * LabWorld.LabMapSize || buildings != 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the lab is not blank: {cells} cells, {buildings} non-player buildings"));
        }

        var scope = new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } };
        var clearance = await h.WireAsync("clearance", "observations_get_clearance_targets", scope, cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(clearance, "observed");
        _ = Decode<RimGovernor.Contracts.Wire.Observations.ClearanceTargetsSnapshot>("clearance targets", observed, identity);
        s.Report["clearance"] = observed.DeepClone();

        var shrines = await h.WireAsync("shrines", "observations_get_ancient_shrines", scope.DeepClone(), cancellationToken).ConfigureAwait(false);
        (_, observed) = Harness.Outcome(shrines, "observed");
        _ = Decode<RimGovernor.Contracts.Wire.Observations.AncientShrinesSnapshot>("ancient shrines", observed, identity);
        s.Report["shrines"] = observed.DeepClone();
    }

    // Go decoded the section into its protobuf type (strict about unknown fields) and ran the bridge validator, whose
    // first check is that the read context is the expected world; the typed decode and that identity check are what the
    // C# harness can hold the reply to.
    private static T Decode<T>(string what, JsonObject observed, JsonObject identity)
    {
        T snapshot;
        try
        {
            snapshot = WireCodec.ToRecord<T>(observed);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{what}: {e.Message}", e);
        }

        if (!Dyn.DeepEqual(observed["context"].Member("identity"), identity))
        {
            throw new AcceptanceException($"{what}: invalid context: the read is not for the observed world");
        }

        return snapshot;
    }
}

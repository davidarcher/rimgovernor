using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>research/reads</c> (Go <c>research/reads.go</c>): typed research-project reads, with a private fingerprint
/// fixture that proves the typed read never mutates saved research state.
/// </summary>
internal static class ResearchReads
{
    private static readonly string[] FingerprintFields = ["current", "progress", "knowledge", "slots", "techprints", "tick", "paused"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "research/reads",
        Scope = "Private read-only research fingerprint fixture; typed read invariance before separate native getter audit. No selection, save or pawn work.",
        Start = new LabStart(),

        // A read-only fingerprint; the wild map is unobserved.
        QuietWorld = true,
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var names = s.Names;
        if (!Dyn.Contains(names, "rimgovernor/observations_read_research"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_read_research in discovery");
        }

        if (!Dyn.Contains(names, "test/research_observation_fingerprint"))
        {
            throw new AcceptanceException("missing test/research_observation_fingerprint fixture in discovery");
        }

        var identityBefore = await h.WireAsync("identity-before", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, before) = Harness.Outcome(identityBefore, "loaded");
        if (before["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        var beforeContext = before["context"].AsMap();
        var identity = beforeContext?["identity"].AsMap() ?? [];

        var fingerprintBefore = await h.CallAsync("fingerprint-before", "test/research_observation_fingerprint", null, cancellationToken).ConfigureAwait(false);
        if (fingerprintBefore["success"].AsBool() != true)
        {
            throw new AcceptanceException("research fingerprint fixture refused");
        }

        JsonObject Scope() => new() { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } };
        JsonObject Full() => Dyn.Merge(Scope(), new JsonObject { ["includeLocked"] = true, ["includeFinished"] = true, ["includeCapability"] = true });

        var fullReply = await h.WireAsync("full", "observations_read_research", Full(), cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(fullReply, "observed");
        if (!observed.ContainsKey("snapshot"))
        {
            throw new AcceptanceException("expected a populated research snapshot on the full read");
        }

        try
        {
            NativeAssert.RequireSnapshot(observed["snapshot"]);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"research read missing a populated CAS snapshot: {e.Message}", e);
        }

        var projects = observed["projects"].AsSlice();
        if (projects.Count <= 1)
        {
            throw new AcceptanceException("expected more than 1 research project");
        }

        var repeatReply = await h.WireAsync("repeat", "observations_read_research", Full(), cancellationToken).ConfigureAwait(false);
        var (_, repeatObserved) = Harness.Outcome(repeatReply, "observed");
        if (!Dyn.DeepEqual(repeatObserved, observed))
        {
            throw new AcceptanceException("repeating the full read returned a different snapshot");
        }

        var defaultsReply = await h.WireAsync("defaults", "observations_read_research", Scope(), cancellationToken).ConfigureAwait(false);
        var (_, defaults) = Harness.Outcome(defaultsReply, "observed");
        if (defaults["benches"].AsSlice().Count != 0 || defaults["researchers"].AsSlice().Count != 0)
        {
            throw new AcceptanceException("default (unrequested) benches/researchers were unexpectedly populated");
        }

        var needle = projects[0].AsMap()?["project"].AsMap()?["defName"].AsString() ?? "";
        var filteredReply = await h.WireAsync("filtered", "observations_read_research", Dyn.Merge(Full(), new JsonObject { ["nameContains"] = needle }), cancellationToken).ConfigureAwait(false);
        var (_, filtered) = Harness.Outcome(filteredReply, "observed");
        if (filtered["projects"].AsSlice().Count == 0)
        {
            throw new AcceptanceException($"filtered read for \"{needle}\" returned no projects");
        }

        var fingerprintAfter = await h.CallAsync("fingerprint-after", "test/research_observation_fingerprint", null, cancellationToken).ConfigureAwait(false);
        JsonObject fpBefore;
        JsonObject fpAfter;
        try
        {
            fpBefore = FingerprintState(fingerprintBefore);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"fingerprint-before: {e.Message}", e);
        }

        try
        {
            fpAfter = FingerprintState(fingerprintAfter);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"fingerprint-after: {e.Message}", e);
        }

        if (!Dyn.DeepEqual(fpBefore, fpAfter))
        {
            throw new AcceptanceException("typed research read mutated saved research state");
        }

        report["saved_state_unchanged"] = true;

        var identityAfterReply = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, after) = Harness.Outcome(identityAfterReply, "loaded");
        if (after["paused"].AsBool() != true)
        {
            throw new AcceptanceException("game unexpectedly resumed during the read pass");
        }

        if (!Dyn.DeepEqual(after["context"], beforeContext))
        {
            throw new AcceptanceException("identity/tick changed during a read-only pass");
        }

        report["projects"] = projects.Count;
        report["researchers"] = observed["researchers"].AsSlice().Count;
        report["benches"] = observed["benches"].AsSlice().Count;
    }

    private static JsonObject FingerprintState(JsonObject value)
    {
        if (value["success"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture did not report success=true: {value["success"].ToJson()}");
        }

        var state = new JsonObject();
        foreach (var field in FingerprintFields)
        {
            if (!value.TryGetPropertyValue(field, out var found))
            {
                throw new AcceptanceException($"fixture missing declared field \"{field}\"");
            }

            state[field] = found?.DeepClone();
        }

        return state;
    }
}

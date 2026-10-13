using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/runtime-fault</c> (Go <c>lifecycle.go</c> runRuntimeFault, the former runtimefaultaccept M3): a required
/// authority invalidation hook that goes missing at runtime invalidates authority as HOOKS_UNAVAILABLE and is
/// reinstalled by the next admission without a game restart. Requires a build with <c>-Fixture RuntimeFaultFixture</c>
/// for <c>test/runtime_fault_unpatch</c>.
/// </summary>
internal static class LifecycleRuntimeFault
{
    /// <summary>The required authority hook this case removes.</summary>
    private const string Hook = "Pawn_DraftController.Drafted";

    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/runtime-fault",
        Scope = "A required authority hook removed at runtime: the fixture observes it missing, authority goes Inactive(HOOKS_UNAVAILABLE) at generation+1 and the hook is reinstalled by the game's own update poll, and a SetMode(Auto) at that generation is granted (a grant requires every required hook installed).",
        Start = new LabStart(),
        Reason = "the fixture unpatches a Harmony hook: process-scoped static state no later case should inherit",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var identity = s.Identity;
        LifecycleCases.RequireTools(s, "rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/clock_read_status", "rimgovernor/clock_read_events", "test/runtime_fault_unpatch");

        // Setup: clock watchers installed (a status read, as a controller's first call does; the authority seam publishes
        // journal rows only once they are), whole hooks, Auto granted.
        _ = await h.WireAsync("clock-status", "clock_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (generation, state, _) = await LifecycleCases.ReadAuthorityAsync(h, "status-fresh", identity, cancellationToken).ConfigureAwait(false);
        if (state == "unavailable")
        {
            generation = 1;
        }

        var granted = await LifecycleCases.GrantAsync(h, "grant", identity, generation, cancellationToken).ConfigureAwait(false);
        report["setup"] = new JsonObject { ["generationGranted"] = granted };

        // Case 1: remove one required hook. The fixture reports the hook count it observed on the game thread the instant
        // after removal; the game's own UpdatePlay poll then invalidates authority as HOOKS_UNAVAILABLE and reinstalls
        // the hook on that same poll, without any further call.
        var unpatched = await h.CallAsync("unpatch", "test/runtime_fault_unpatch", new JsonObject { ["hook"] = Hook }, cancellationToken).ConfigureAwait(false);
        var required = unpatched["required"].AsNumber();
        if (unpatched["removed"].AsBool() != true || required < 1 || unpatched["health"].AsNumber() != required - 1)
        {
            throw new AcceptanceException($"unpatch: expected {required - 1}/{required} hooks right after removal, got {unpatched.ToJson()}");
        }

        var clock = Stopwatch.StartNew();
        ulong afterGeneration;
        JsonObject value;
        for (var attempt = 0; ; attempt++)
        {
            (afterGeneration, state, value) = await LifecycleCases.ReadAuthorityAsync(h, string.Create(CultureInfo.InvariantCulture, $"status-broken-{attempt}"), identity, cancellationToken).ConfigureAwait(false);
            if (state == "inactive")
            {
                var reason = value["inactive"].AsMap()?["reason"].AsString();
                if (reason != "REVOCATION_REASON_HOOKS_UNAVAILABLE")
                {
                    throw new AcceptanceException($"status-broken: expected REVOCATION_REASON_HOOKS_UNAVAILABLE, got {value["inactive"].ToJson()}");
                }

                break;
            }

            if (state == "unavailable")
            {
                // Hooks not ready reads as unavailable on the status projection; the generation still advanced. Accept
                // either projection.
                break;
            }

            if (clock.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new AcceptanceException($"status-broken: authority still {value.ToJson()} after the hook was removed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        if (afterGeneration != granted + 1)
        {
            throw new AcceptanceException($"status-broken: expected generation {granted + 1}, got {afterGeneration}");
        }

        report["case_hook_removed"] = new JsonObject { ["unpatched"] = unpatched.DeepClone(), ["state"] = value.DeepClone(), ["generation"] = afterGeneration };

        // Case 2: the next admission reinstalls the hook and is granted; no restart, no fixture involvement.
        var regranted = await LifecycleCases.GrantAsync(h, "regrant", identity, afterGeneration, cancellationToken).ConfigureAwait(false);
        if (regranted != afterGeneration + 1)
        {
            throw new AcceptanceException($"regrant: expected generation {afterGeneration + 1}, got {regranted}");
        }

        report["case_reinstalled"] = new JsonObject { ["generation"] = regranted };
    }
}

using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What a case's Run receives: the open, prepared game (Go <c>cases.Session</c>). The runner implements it over the
/// session opener. The service and checkpoint members (Stage, Resumed, Staged, Launch, Serve, Runtime, Advance, ...)
/// arrive with the slices that port the machinery behind them; adding a member is additive for cases.
/// </summary>
public interface ISession
{
    /// <summary>The run's resolved configuration (root, output, profile, startup log); an Owned case opens its own game with it.</summary>
    Config Config { get; }

    /// <summary>Records evidence under the run's output directory.</summary>
    Harness Harness { get; }

    /// <summary>The discovered native tool names.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>The loaded colony's identity (colonyId, loadToken, mapId...).</summary>
    JsonObject Identity { get; }

    /// <summary>A Fixture start's reply, null otherwise.</summary>
    JsonObject? Prepared { get; }

    /// <summary>The run's report; the case adds its own fields.</summary>
    Report Report { get; }

    /// <summary>The case's declared Serve spec, or the zero spec for a native-only case.</summary>
    ServeSpec Spec { get; }

    /// <summary>The run-level RNG seed (-run-seed), 0 for none.</summary>
    int RunSeed { get; }

    /// <summary>
    /// <paramref name="baseId"/> on a fresh run and suffixed with the run's id on a resumed one: the restored store
    /// already holds the fresh run's submissions, and a replay under the resumed world's identity is a different request
    /// the store answers 409 conflict. Stable within a run. Use it for every deterministic requestId a Run body submits.
    /// </summary>
    string RequestId(string baseId);
}

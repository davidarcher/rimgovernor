using System.Text.Json.Nodes;
using System.Xml.Linq;
using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the start-claim cases share (<c>lifecycle/new-colony*</c>, <c>quiet-storyteller</c>, <c>ideoligion-design</c>,
/// <c>scenario-start-parts</c>): the pinned spec, a launched main-menu game, the typed outcome assertions, the colonist
/// read and the structural reads of the generated save for what no observation reports.
/// </summary>
internal static class NewColonyStart
{
    /// <summary>The ten-minute bound a start runs under.</summary>
    internal const uint TimeoutMs = 600_000;

    /// <summary>The biomes the pinned spec prefers, in order.</summary>
    internal static readonly string[] Biomes = ["TemperateForest", "BorealForest", "AridShrubland"];

    /// <summary>The small pinned spec (150 map, 0.05 coverage) the start claims begin from.</summary>
    internal static Lifecycle.NewColonySpec Spec() => new()
    {
        Scenario = "Crashlanded",
        ColonistCount = 3,
        Seed = "newcolony-accept",
        Biomes = [.. Biomes],
        FlatTile = true,
        Difficulty = "Rough",
        Storyteller = "Cassandra",
        MapSize = 150,
        PlanetCoverage = 0.05f,
        SaveName = "newcolony-accept",
        MinTemperature = new Lifecycle.TemperatureLimit.OpenCase(),
        MaxTemperature = new Lifecycle.TemperatureLimit.OpenCase(),
        GovernorIdeoligion = false,
        PlayerFaction = Lifecycle.ScenarioPlayerFaction.Base,
        ArrivalMethod = Lifecycle.ScenarioArrivalMethod.Base,
    };

    /// <summary>A <c>lifecycle_new_colony</c> request over <paramref name="spec"/>.</summary>
    internal static Lifecycle.NewColonyRequest Request(string id, Lifecycle.NewColonySpec spec, uint timeoutMs = TimeoutMs) =>
        new() { RequestId = id, Spec = spec, TimeoutMs = timeoutMs };

    /// <summary>Launches a game at the main menu, runs <paramref name="body"/> against it and retires it; retiring is best effort and the body's result stands.</summary>
    internal static async Task MenuAsync(TypedSession s, string label, Func<Task> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(body);
        var reuse = await s.LaunchAsync(label + "-launch", cancellationToken).ConfigureAwait(false);
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            using var retire = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await reuse.RetireAsync(label + " complete", retire.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                Tolerated.Note("retire", tolerated);
            }
            catch (OperationCanceledException tolerated)
            {
                Tolerated.Note("retire", tolerated);
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="request"/> (through the brain's player route when <paramref name="throughBrain"/>, which a
    /// spec the brain resolves needs) and requires it to complete; a failed start fails <paramref name="label"/> with its
    /// code and phases.
    /// </summary>
    internal static async Task<(Lifecycle.NewColonyCompleted Completed, IReadOnlyList<Lifecycle.NewColonyPhase> Phases)> CompleteAsync(
        TypedSession s, string label, Lifecycle.NewColonyRequest request, CancellationToken cancellationToken, bool throughBrain = false)
    {
        var result = throughBrain
            ? await s.NewColonyThroughBrainAsync(label, request, cancellationToken).ConfigureAwait(false)
            : await s.NewColonyAsync(label, request, cancellationToken).ConfigureAwait(false);
        return result.Completed is { } completed
            ? (completed, result.Phases)
            : throw new AcceptanceException($"{label}: the start failed after phases [{string.Join(' ', result.Phases)}]: {result.Failure}");
    }

    /// <summary>Starts <paramref name="request"/> and requires a failure with code <paramref name="want"/>; returns the phases reported before it.</summary>
    internal static async Task<IReadOnlyList<Lifecycle.NewColonyPhase>> FailsAsync(
        TypedSession s, string label, Lifecycle.NewColonyRequest request, Common.FailureCode want, CancellationToken cancellationToken)
    {
        var result = await s.NewColonyAsync(label, request, cancellationToken).ConfigureAwait(false);
        return result.Failure?.Code == want
            ? result.Phases
            : throw new AcceptanceException($"{label}: want {want}, got {(object?)result.Failure ?? result.Completed} after phases [{string.Join(' ', result.Phases)}]");
    }

    /// <summary>Requires <paramref name="request"/> refused with INVALID_REQUEST before generation: no phase reported.</summary>
    internal static async Task RefusedAsync(TypedSession s, string label, Lifecycle.NewColonyRequest request, CancellationToken cancellationToken)
    {
        var phases = await FailsAsync(s, label, request, Common.FailureCode.InvalidRequest, cancellationToken).ConfigureAwait(false);
        if (phases.Count > 0)
        {
            throw new AcceptanceException($"{label}: refused only after generation began (phases [{string.Join(' ', phases)}])");
        }
    }

    /// <summary>The colonists <c>observations_list_pawns</c> reports for <paramref name="identity"/>, with biography.</summary>
    internal static async Task<IReadOnlyList<Observations.PawnState>> ColonistsAsync(TypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(
            label,
            NativeOps.ListPawns,
            new Observations.ListPawnsRequest
            {
                Scope = TypedReads.Scope(identity),
                Filter = new Observations.PawnFilter { Colonist = RimGovernor.Contracts.Wire.Observations.PawnMembership.Only },
            },
            cancellationToken).ConfigureAwait(false);
        return [.. reply.Pawns];
    }

    /// <summary>Requires the colony's one colonist to carry <paramref name="trait"/>; records and returns its traits.</summary>
    internal static async Task<IReadOnlyList<string>> ForcedTraitAsync(TypedSession s, string trait, CancellationToken cancellationToken)
    {
        var (identity, _, _) = await TypedLifecycle.IdentityAsync(s, "trait-identity", cancellationToken).ConfigureAwait(false);
        var pawns = await ColonistsAsync(s, "trait-pawns", identity, cancellationToken).ConfigureAwait(false);
        if (pawns is not [var pawn])
        {
            throw new AcceptanceException($"trait-pawns: want 1 colonist, got {pawns.Count}");
        }

        var traits = pawn.Biography.Traits.Select(t => t.DefName).ToList();
        s.Record("traits", Strings(traits));
        return traits.Contains(trait, StringComparer.Ordinal)
            ? traits
            : throw new AcceptanceException($"the forced trait {trait} did not land: colonist traits [{string.Join(' ', traits)}]");
    }

    /// <summary>A JSON array of strings for the report.</summary>
    internal static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>The sum of <paramref name="def"/>'s stacks among the save's map things (an omitted stackCount is 1).</summary>
    internal static int ThingTotal(XDocument save, string def) =>
        save.Descendants("thing").Where(t => (string?)t.Element("def") == def).Sum(t => (int?)t.Element("stackCount") ?? 1);

    /// <summary>The save's storyteller def.</summary>
    internal static string? Storyteller(XDocument save) => (string?)save.Descendants("storyteller").Elements("def").FirstOrDefault();
}

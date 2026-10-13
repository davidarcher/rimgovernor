#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Host.Sdk;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Read-only decoded reads for the out-of-process acceptance harness (#3117): the colony facts, blight census, first
    /// layout plan and map survey, and two catalog tables, which only exist decoded inside the game process. Each tool
    /// runs <see cref="AcceptanceReads"/> over the published facade and returns the JSON in <c>payload</c>; none writes,
    /// takes authority or needs the in-process brain to be running. The harness reaches them through the generic
    /// <c>POST /native/rimgovernor/&lt;tool&gt;</c> route.
    /// </summary>
    public sealed class AcceptanceReadTools
    {
        private static readonly Lazy<AcceptanceReads> Reads = new Lazy<AcceptanceReads>(
            () => AcceptanceReads.Create(NativeFacadeRegistry.Require(), NativeCatalogFactory.Create), LazyThreadSafetyMode.ExecutionAndPublication);

        private static async Task<object> Payload(Func<AcceptanceReads, Task<string>> read)
            => new Dictionary<string, object?> { ["payload"] = await read(Reads.Value).ConfigureAwait(false) };

        [Tool("rimgovernor/acceptance_read_colony_facts", Title = "Acceptance: decoded colony facts",
            Description = "Acceptance harness read. JSON payload: the decoded sleeping temperature band (sleepingMin/sleepingMax, Celsius) and power coverage (powerRequired, powerHeadroom, disabledConsumers); a member is absent when the colony read did not carry it. Read-only.")]
        public Task<object> ReadColonyFacts(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => Payload(reads => reads.ColonyFactsAsync(cancellationToken));

        [Tool("rimgovernor/acceptance_read_blight", Title = "Acceptance: blight census",
            Description = "Acceptance harness read. JSON payload: the planning window's blighted plants (id, def, x, z, zone, designated, eligible) and the tick read at; unavailable true when the census is unread. Read-only.")]
        public Task<object> ReadBlight(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => Payload(reads => reads.BlightAsync(cancellationToken));

        [Tool("rimgovernor/acceptance_read_starter_site", Title = "Acceptance: first layout plan and map survey",
            Description = "Acceptance harness read. JSON payload: tick, colonists, map width/height and the first layout plan (no ledger, no stock: the seed rooms and outskirts) in the store codec; with cells true also the scored map survey. Read-only; records nothing.")]
        public Task<object> ReadStarterSite(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Include the scored map survey cells (about two megabytes).")] bool cells = false)
            => Payload(reads => reads.StarterSiteAsync(cells, cancellationToken));

        [Tool("rimgovernor/acceptance_read_impressiveness", Title = "Acceptance: Impressiveness score stages",
            Description = "Acceptance harness read. JSON payload: the minimum scores of the Impressiveness room stat's named stages (dull, mediocre, decent, slightlyImpressive) and every stage in order. Read-only.")]
        public Task<object> ReadImpressiveness(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => Payload(reads => reads.ImpressivenessAsync(cancellationToken));

        [Tool("rimgovernor/acceptance_read_animal_races", Title = "Acceptance: animal race catalog",
            Description = "Acceptance harness read. JSON payload: one row per animal race of the definition catalog (def, wildness, bodySize, minimumHandlingSkill, milk amount and intervalDays); a fact the catalog did not read is absent. Read-only.")]
        public Task<object> ReadAnimalRaces(IRimBridgeContext ctx, CancellationToken cancellationToken)
            => Payload(reads => reads.AnimalRacesAsync(cancellationToken));
    }
}

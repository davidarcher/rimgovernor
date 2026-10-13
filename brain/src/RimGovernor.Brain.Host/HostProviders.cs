using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Host;

/// <summary>The live colony census the colony route fronts (Go <c>buildingruntime.ColonyStatus</c> as <c>httpapi.ColonyStatus</c>).</summary>
internal sealed class HostColonyStatus(ColonyStatus status) : IColonyStatusReader
{
    public Task<ColonyStatusReport> ReadAsync(CancellationToken cancellationToken) => status.ReadAsync(cancellationToken);
}

/// <summary>
/// A read-only, runtime-queryable view of which composed routine planner families this process wired up at startup
/// and the durable review cursor's progress (Go <c>serviceRoundsDiagnostics</c>). It makes no native call and mutates
/// nothing.
/// </summary>
public sealed class RoundsDiagnostics(StoreJournal journal, bool reviewsEnabled, bool methodsEnabled, IReadOnlyList<string> families, FactStore sections) : IRoundsProvider
{
    public async Task<RoundsStatus> RoundsStatusAsync(CancellationToken cancellationToken)
    {
        var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
        var status = new RoundsStatus
        {
            ReviewsEnabled = reviewsEnabled,
            MethodsEnabled = methodsEnabled,
            ActiveFamilies = families,
            LastReviewTick = review.Revision != 0 ? Known(review.Tick) : Unknown<Tick>(),
            Sections = sections.Status(),
            LootHolds = review.EventLoot.Held,
        };

        // Read-only diagnostics: missing complete extent geometry stays unknown. This does not widen any planner or
        // dispatch surface.
        if (FactStore.TryGet<ColonyProjection>(sections, Section.Colony, out var held) && held.Complete)
        {
            var f = held.Value.Facts;
            var threat = Unknown<bool>();
            if (f.Hostiles.TryGetValue(out var count))
            {
                threat = Known(count > 0);
            }

            status = status with
            {
                Extent = PolicyFunctions.DeriveColonyExtent(new ColonyExtentRequest
                {
                    Bounds = Known(held.Value.Bounds),
                    Construction = f.CurrentConstruction,
                    Claims = f.ConstructionClaims,
                    Home = f.HomeCoverage,
                }),
            };
            var id = held.Value.Identity;
            if (IsValid(id))
            {
                var history = await journal.EstablishedColonyExtentAsync(
                    new GenerationSnapshot { Colony = id.Colony, Map = id.Map, Load = id.Load, Plan = new PlanId("extent-diagnostics") },
                    id.Tick,
                    cancellationToken).ConfigureAwait(false);
                var extent = new ColonyExtent { Regions = EquatableArray.From(history.Select(row => row.Region)) };
                var eligibility = new ExtentEligibilityRequest { Extent = Known(extent), Threat = threat };
                if (f.CurrentConstruction.TryGetValue(out var buildings) && buildings.Colony && f.HomeCoverage.TryGetValue(out var home))
                {
                    var ids = new List<string>(buildings.Buildings.Select(b => b.Id));

                    // Every census stockpile: a target with zone geometry.
                    foreach (var target in home.Targets)
                    {
                        if (target.ExtentGeometry.TryGetValue(out var geometry) && geometry.Zone.Count > 0)
                        {
                            ids.Add(target.Id);
                        }
                    }

                    eligibility = eligibility with { Facilities = Known(EquatableArray.From(ids)) };
                }

                // No region route census is currently held: route_unknown is a hold.
                status = status with { ExtentEligibility = eligibility };
            }
        }

        if (review.Revision != 0)
        {
            status = status with
            {
                Progress = review.Progress,
                Emergency = review.Emergency,
                NoOps = review.NoOps,
                Stage = review.Stage,
                Roster = review.Roster,
                ResourceRunways = review.ResourceRunwayState(),
            };
        }

        return status;
    }

    private static bool IsValid(RimGovernor.Brain.Observation.Identity id)
    {
        try
        {
            id.Validate();
            return true;
        }
        catch (ObservationContractException)
        {
            return false;
        }
    }
}

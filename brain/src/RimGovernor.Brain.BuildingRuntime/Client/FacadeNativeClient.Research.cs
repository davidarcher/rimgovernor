using RimGovernor.Brain.Bridge.Contracts;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;

namespace RimGovernor.Brain.BuildingRuntime;

// Go research_select_read.go: Client.ReadResearch, researchRequest and readResearchSnapshot.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadResearch</c>: the native research snapshot translated into the economy family's research shapes. It
    /// reads the definition catalog first (every project's static facts) and merges the progress rows over it.
    /// </summary>
    internal async Task<ResearchRead> ReadResearchAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var request = new PbObservations.ResearchRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, ProgressOnly = true };
        var reply = await Facade.ReadResearchAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        return ResearchReadSnapshot(outcome.Observed!, catalog);
    }

    private static ResearchRead ResearchReadSnapshot(PbObservations.ResearchSnapshot v, BridgeCatalog catalog)
    {
        var projects = new Dictionary<string, Policy.ResearchProjectFacts>(StringComparer.Ordinal);
        foreach (var (name, facts) in catalog.Research)
        {
            projects[name] = facts;
        }

        var current = "";
        foreach (var row in v.Projects)
        {
            var name = row.Project.DefName ?? "";
            if (!projects.TryGetValue(name, out var facts))
            {
                throw BridgeContractException.Contract($"research project {name} outside the definition catalog");
            }

            // The census computes lock_reasons from the same predicates as the native CanStartNow; an absent list is a
            // project that can start. A row carries can_start exactly when the census computed its lock reasons
            // (current projects and unfinished knowledge ones).
            facts = facts with { LockReasons = row.LockReasons, Census = row.CanStart is not null };
            foreach (var reason in row.LockReasons)
            {
                if (reason == Policy.PolicyFunctions.ResearchLockHidden)
                {
                    facts = facts with { Hidden = true };
                }
            }

            projects[name] = facts;
            // A knowledge project is current in its category slot, read below: only the ordinary slot's project is the
            // current project.
            if (row.Current && facts.KnowledgeCategory.Length == 0)
            {
                current = name;
            }
        }

        var knowledge = new List<Policy.KnowledgeSlot>();
        foreach (var slot in v.Slots)
        {
            if (slot.Category is null)
            {
                continue;
            }

            knowledge.Add(new Policy.KnowledgeSlot { Category = slot.Category, Current = new Policy.ResearchProjectId(slot.CurrentProject ?? "") });
        }

        knowledge.Sort((a, b) => string.CompareOrdinal(a.Category, b.Category));
        return new ResearchRead
        {
            Context = v.Context,
            CurrentProject = current,
            Projects = projects,
            Finished = [.. v.Finished],
            Knowledge = knowledge,
        };
    }
}

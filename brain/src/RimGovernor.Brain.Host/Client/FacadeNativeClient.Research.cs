using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using static RimGovernor.Brain.Domain.Fact;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;

namespace RimGovernor.Brain.Host;

// Go research_select_read.go: Client.ReadResearch, researchRequest and readResearchSnapshot.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadResearch</c>: the native research snapshot translated into EnsureResearch's policy shapes. It
    /// reads the definition catalog first (every project's static facts) and merges the progress rows over it.
    /// </summary>
    internal async Task<(ResearchRead Value, BridgeResult Receipt)> ReadResearchAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var request = new PbObservations.ResearchRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, ProgressOnly = true };
        var reply = await Facade.ReadResearchAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Observed is { } observed)
        {
            return (ResearchReadSnapshot(observed, identity, catalog), NoReceipt);
        }

        throw Contract("missing research outcome");
    }

    internal static ResearchRead ResearchReadSnapshot(PbObservations.ResearchSnapshot? v, PbCommon.Identity identity, BridgeCatalog? catalog)
    {
        if (v is null || !G3ContextValid(v.Context) || !SameIdentity(v.Context!.Identity, identity))
        {
            throw Contract("invalid research context");
        }

        if (v.Snapshot is null || !IsValidId(v.Snapshot.Token ?? ""))
        {
            throw Contract("invalid research snapshot token");
        }

        if (catalog is null)
        {
            throw Contract("research read without a definition catalog");
        }

        var projects = new Dictionary<string, Policy.ResearchProjectFacts>(StringComparer.Ordinal);
        foreach (var (name, facts) in catalog.Research)
        {
            projects[name] = facts;
        }

        var finished = new HashSet<string>(StringComparer.Ordinal);
        var finishedList = new List<string>();
        foreach (var name in v.Finished)
        {
            if (!IsValidId(name) || !finished.Add(name))
            {
                throw Contract("invalid finished research project");
            }

            finishedList.Add(name);
        }

        var current = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Projects)
        {
            if (row is null || row.Project is null || !IsValidId(row.Project.DefName ?? ""))
            {
                throw Contract("invalid research project identity");
            }

            var name = row.Project.DefName ?? "";
            if (!seen.Add(name))
            {
                throw Contract("duplicate research project");
            }

            if (!projects.TryGetValue(name, out var facts))
            {
                throw Contract($"research project {name} outside the definition catalog");
            }

            // The census computes lock_reasons from the same predicates as the native CanStartNow; an absent list is a
            // project that can start. A row carries can_start exactly when the census computed its lock reasons
            // (current projects and unfinished knowledge ones).
            facts = facts with { LockReasons = row.LockReasons, Census = row.CanStart is not null };
            foreach (var reason in row.LockReasons)
            {
                if (reason == Policy.PolicyFunctions.ResearchLockHidden)
                {
                    facts = facts with { Hidden = Known(true) };
                }
            }

            projects[name] = facts;
            // A knowledge project is current in its category slot, read below: only the ordinary slot's project is the
            // current project.
            if (row.Current == true && facts.KnowledgeCategory.Length == 0)
            {
                if (current.Length != 0)
                {
                    throw Contract("multiple current research projects");
                }

                current = name;
            }
        }

        var knowledge = new List<Policy.KnowledgeSlot>();
        var slotted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in v.Slots)
        {
            if (slot is null)
            {
                throw Contract("invalid research slot");
            }

            if (slot.Category is null)
            {
                continue;
            }

            var category = slot.Category;
            if (!IsValidId(category) || !slotted.Add(category) || (slot.CurrentProject is not null && !IsValidId(slot.CurrentProject)))
            {
                throw Contract("invalid research knowledge slot");
            }

            if (slot.CurrentProject is not null && (!projects.TryGetValue(slot.CurrentProject, out var project) || project.KnowledgeCategory != category))
            {
                throw Contract($"research slot {category} holds project {slot.CurrentProject} of another category");
            }

            knowledge.Add(new Policy.KnowledgeSlot { Category = category, Current = new Policy.ResearchProjectId(slot.CurrentProject ?? "") });
        }

        knowledge.Sort((a, b) => string.CompareOrdinal(a.Category, b.Category));
        var researchers = new List<string>();
        foreach (var researcher in v.Researchers)
        {
            if (researcher is null || !IsValidId(researcher.Pawn?.Id ?? ""))
            {
                throw Contract("invalid researcher identity");
            }

            researchers.Add(researcher.Pawn!.Id!);
        }

        return new ResearchRead
        {
            Context = v.Context,
            CurrentProject = current,
            Projects = projects,
            Finished = finishedList,
            Researchers = researchers,
            Knowledge = knowledge,
        };
    }
}

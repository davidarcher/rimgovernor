package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ResearchRead is the native research snapshot ReadResearch translates into
// EnsureResearch's policy shapes: which project (if any) is already current,
// the finished projects, and every project's facts from the definition
// catalog (#1340) with the current projects' native lock reasons. It
// reviews only the fields EnsureResearch's dispatch vertical consumes.
type ResearchRead struct {
	Context        *c.ObservationContext
	CurrentProject string
	Projects       map[string]policy.ResearchProjectFacts
	Finished       []string
	Benches        []policy.ResearchBench
	Researchers    []string
}

// researchRequest is the exact request ReadResearch issues, the key the
// bundle seeds its research section under: the progress rows, whose
// static facts are the catalog's.
func researchRequest(identity *c.Identity) *o.ResearchRequest {
	return &o.ResearchRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, ProgressOnly: proto.Bool(true)}
}

func (client *Client) ReadResearch(ctx context.Context, identity *c.Identity) (ResearchRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ResearchRead{}, Result{}, err
	}
	catalog, err := client.DefinitionCatalog(ctx, identity)
	if err != nil {
		return ResearchRead{}, Result{}, err
	}
	request := researchRequest(identity)
	reply := &o.ResearchReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_research", request, reply)
	if err != nil {
		return ResearchRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return ResearchRead{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ResearchReply_Failure:
		return ResearchRead{}, raw, failure(v.Failure, raw)
	case *o.ResearchReply_Unavailable:
		return ResearchRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.ResearchReply_Observed:
		out, err := readResearchSnapshot(v.Observed, identity, catalog)
		return out, raw, err
	default:
		return ResearchRead{}, raw, contract("missing research outcome")
	}
}

func readResearchSnapshot(v *o.ResearchSnapshot, identity *c.Identity, catalog *DefinitionCatalog) (ResearchRead, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return ResearchRead{}, contract("invalid research context")
	}
	if v.Snapshot == nil || validID(v.Snapshot.GetToken()) != nil {
		return ResearchRead{}, contract("invalid research snapshot token")
	}
	if catalog == nil {
		return ResearchRead{}, contract("research read without a definition catalog")
	}
	out := ResearchRead{Context: v.Context, Projects: make(map[string]policy.ResearchProjectFacts, len(catalog.Research))}
	for name, facts := range catalog.Research {
		out.Projects[name] = facts
	}
	finished := map[string]bool{}
	for _, name := range v.Finished {
		if validID(name) != nil || finished[name] {
			return ResearchRead{}, contract("invalid finished research project")
		}
		finished[name] = true
		out.Finished = append(out.Finished, name)
	}
	current := ""
	seen := map[string]bool{}
	for _, row := range v.Projects {
		if row == nil || row.Project == nil || validID(row.Project.GetDefName()) != nil {
			return ResearchRead{}, contract("invalid research project identity")
		}
		name := row.Project.GetDefName()
		if seen[name] {
			return ResearchRead{}, contract("duplicate research project")
		}
		seen[name] = true
		facts, ok := out.Projects[name]
		if !ok {
			return ResearchRead{}, contract("research project %s outside the definition catalog", name)
		}
		// The census computes lock_reasons from the same predicates as the
		// native CanStartNow; an absent list is a project that can start.
		facts.LockReasons = append([]string(nil), row.GetLockReasons()...)
		out.Projects[name] = facts
		if row.GetCurrent() {
			if current != "" {
				return ResearchRead{}, contract("multiple current research projects")
			}
			current = name
		}
	}
	out.CurrentProject = current
	for _, bench := range v.Benches {
		building := bench.GetBuilding()
		if bench == nil || building == nil || validID(building.GetBuilding().GetDefName()) != nil || building.GetService() == nil || building.GetService().PowerOn == nil {
			return ResearchRead{}, contract("invalid research bench")
		}
		row := policy.ResearchBench{DefName: building.GetBuilding().GetDefName(), Powered: building.GetService().GetPowerOn()}
		for _, facility := range bench.GetFacilities() {
			if facility == nil || facility.Definition == nil || validID(facility.Definition.GetDefName()) != nil {
				return ResearchRead{}, contract("invalid research bench facility")
			}
			row.Facilities = append(row.Facilities, policy.ResearchBenchFacility{DefName: facility.Definition.GetDefName(), Active: facility.GetActive()})
		}
		out.Benches = append(out.Benches, row)
	}
	for _, researcher := range v.Researchers {
		if researcher == nil || validID(researcher.GetPawn().GetId()) != nil {
			return ResearchRead{}, contract("invalid researcher identity")
		}
		out.Researchers = append(out.Researchers, researcher.GetPawn().GetId())
	}
	return out, nil
}

func toProjectIDs(names []string) []policy.ResearchProjectID {
	if len(names) == 0 {
		return nil
	}
	out := make([]policy.ResearchProjectID, len(names))
	for i, name := range names {
		out[i] = policy.ResearchProjectID(name)
	}
	return out
}

package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ResearchRead is the native research snapshot ReadResearch translates into
// EnsureResearch's policy shapes: the fresh CAS token SelectResearch needs,
// which project (if any) is already current, and the project/bench facts
// policy.ResearchPrerequisiteQueue and policy.UsableResearchLaboratories
// inspect. Unlike ReadColonyFacts's exhaustive census validation, this
// reviews only the fields EnsureResearch's dispatch vertical consumes; wider
// research-tab facts (unlocks, techprints, tech level) remain unreviewed and
// are an open native-acceptance item alongside G01.12.
type ResearchRead struct {
	Context        *c.ObservationContext
	SnapshotToken  string
	CurrentProject string
	Projects       map[string]policy.ResearchProjectFacts
	Finished       []string
	Benches        []policy.ResearchBench
	Researchers    []string
}

// researchRequest is the exact request ReadResearch issues, the key the
// bundle seeds its research section under.
func researchRequest(identity *c.Identity) *o.ResearchRequest {
	return &o.ResearchRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, IncludeLocked: proto.Bool(true), IncludeFinished: proto.Bool(true), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
}

func (client *Client) ReadResearch(ctx context.Context, identity *c.Identity) (ResearchRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
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
		out, err := readResearchSnapshot(v.Observed, identity)
		return out, raw, err
	default:
		return ResearchRead{}, raw, contract("missing research outcome")
	}
}

func readResearchSnapshot(v *o.ResearchSnapshot, identity *c.Identity) (ResearchRead, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return ResearchRead{}, contract("invalid research context")
	}
	if v.Snapshot == nil || validID(v.Snapshot.GetToken()) != nil {
		return ResearchRead{}, contract("invalid research snapshot token")
	}
	if len(v.Projects) > 4096 || len(v.Benches) > 256 || len(v.Researchers) > 256 {
		return ResearchRead{}, contract("research snapshot exceeds bound")
	}
	out := ResearchRead{Context: v.Context, SnapshotToken: v.Snapshot.GetToken(), Projects: map[string]policy.ResearchProjectFacts{}}
	current := ""
	for _, row := range v.Projects {
		if row == nil || row.Project == nil || validID(row.Project.GetDefName()) != nil {
			return ResearchRead{}, contract("invalid research project identity")
		}
		name := row.Project.GetDefName()
		if _, exists := out.Projects[name]; exists {
			return ResearchRead{}, contract("duplicate research project")
		}
		facts := policy.ResearchProjectFacts{Name: policy.ResearchProjectID(name)}
		// The wire ResearchProject message does not expose RimWorld's own
		// ResearchProjectDef.hidden flag (unlike knowledgeCategory, which is
		// present as Category). Every listed project is therefore treated as
		// not-hidden here; a genuinely hidden, category-less project (e.g.
		// anomaly-gated research) would be wrongly admitted as an ordinary
		// prerequisite by ResearchPrerequisiteQueue. This is an open native
		// acceptance gap alongside G01.12, not a verified equivalence.
		facts.Hidden = domain.Known(false)
		facts.KnowledgeCategory = row.GetCategory()
		// The native census always emits both lists and never raises an
		// issue for them; a project with no prerequisites (Smithing) is an
		// absent repeated field on the wire, which is known-empty, not
		// unread.
		facts.Prerequisites = domain.Known(toProjectIDs(row.GetPrerequisites()))
		facts.HiddenPrerequisites = domain.Known(toProjectIDs(row.GetHiddenPrerequisites()))
		// The census computes lock_reasons from the same predicates as the
		// native CanStartNow; an absent list is a project that can start.
		if len(row.GetLockReasons()) > 256 {
			return ResearchRead{}, contract("research lock reasons exceed bound")
		}
		facts.RequiredBuilding = row.GetRequiredBuilding()
		facts.LockReasons = append([]string(nil), row.GetLockReasons()...)
		out.Projects[name] = facts
		if row.GetFinished() {
			out.Finished = append(out.Finished, name)
		}
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

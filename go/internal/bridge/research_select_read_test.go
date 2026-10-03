package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func researchReadCatalog() *DefinitionCatalog {
	return &DefinitionCatalog{Research: map[string]policy.ResearchProjectFacts{
		"Electricity":          {Name: "Electricity", Hidden: domain.Known(false)},
		"BioferriteExtraction": {Name: "BioferriteExtraction", Hidden: domain.Known(false), KnowledgeCategory: "Basic", Cost: 5},
		"BioferriteShaping":    {Name: "BioferriteShaping", Hidden: domain.Known(false), KnowledgeCategory: "Basic", Cost: 20},
		"BioferriteGenerator":  {Name: "BioferriteGenerator", Hidden: domain.Known(false), KnowledgeCategory: "Advanced", Cost: 40},
	}}
}

func researchReadRow(name string, current bool, locks ...string) *o.ResearchProject {
	return &o.ResearchProject{Project: &o.DefinitionRef{DefName: proto.String(name)}, Current: proto.Bool(current), CanStart: proto.Bool(len(locks) == 0), LockReasons: locks}
}

// The ordinary slot's project is the current project; a knowledge slot's
// project is its category's, never a second current project (#1745). Rows
// the census computed locks for carry them, and a hidden project is marked
// hidden for the prerequisite queue.
func TestReadResearchSnapshotSeparatesKnowledgeSlotsFromTheCurrentProject(t *testing.T) {
	snapshot := &o.ResearchSnapshot{
		Context:  authorityTestContext(7),
		Snapshot: &o.SnapshotRef{Token: proto.String("research-token")},
		Slots: []*o.ResearchSlot{
			{CurrentProject: proto.String("Electricity")},
			{Category: proto.String("Basic"), CurrentProject: proto.String("BioferriteExtraction")},
			{Category: proto.String("Advanced")},
		},
		Projects: []*o.ResearchProject{
			researchReadRow("Electricity", true),
			researchReadRow("BioferriteExtraction", true),
			researchReadRow("BioferriteShaping", false, "prerequisite:BioferriteExtraction"),
			researchReadRow("BioferriteGenerator", false, "hidden"),
		},
	}
	read, err := readResearchSnapshot(snapshot, pbIdentity(), researchReadCatalog())
	if err != nil {
		t.Fatal(err)
	}
	if read.CurrentProject != "Electricity" {
		t.Fatal("ordinary current project", read.CurrentProject)
	}
	want := []policy.KnowledgeSlot{{Category: "Advanced"}, {Category: "Basic", Current: "BioferriteExtraction"}}
	if len(read.Knowledge) != 2 || read.Knowledge[0] != want[0] || read.Knowledge[1] != want[1] {
		t.Fatal("knowledge slots", read.Knowledge)
	}
	if shaping := read.Projects["BioferriteShaping"]; !shaping.Census || len(shaping.LockReasons) != 1 {
		t.Fatal("census locks", shaping)
	}
	if generator := read.Projects["BioferriteGenerator"]; generator.Hidden != domain.Known(true) {
		t.Fatal("hidden lock", generator)
	}
	if got := policy.KnowledgePick(read.Projects, read.Finished, read.Knowledge); got != "" {
		t.Fatal("the advanced slot's only project is hidden and basic is full", got)
	}
}

func TestReadResearchSnapshotRefusesAKnowledgeSlotHoldingAnotherCategoryProject(t *testing.T) {
	for name, slots := range map[string][]*o.ResearchSlot{
		"other category":  {{Category: proto.String("Advanced"), CurrentProject: proto.String("BioferriteExtraction")}},
		"outside catalog": {{Category: proto.String("Basic"), CurrentProject: proto.String("Missing")}},
		"duplicate":       {{Category: proto.String("Basic")}, {Category: proto.String("Basic")}},
	} {
		snapshot := &o.ResearchSnapshot{Context: authorityTestContext(7), Snapshot: &o.SnapshotRef{Token: proto.String("research-token")}, Slots: slots}
		if _, err := readResearchSnapshot(snapshot, pbIdentity(), researchReadCatalog()); err == nil {
			t.Fatal(name, "accepted")
		}
	}
}

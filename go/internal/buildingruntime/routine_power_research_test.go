package buildingruntime

import (
	"context"
	"testing"

	"google.golang.org/protobuf/proto"
)

// With every generator unavailable and the research it requires unfinished,
// the power goal reports the project it waits on rather than
// no_affordable_generator: EnsureResearch's roadmap is the method (#230).
func TestRoutinePowerReportsTheResearchItWaitsOn(t *testing.T) {
	t.Parallel()
	p, _, n, _ := powerFixture(t, false)
	for _, d := range n.reply.GetObserved().Planning.GetObserved().Definitions {
		if d.Definition.GetDefName() == "WoodFiredGenerator" {
			d.Available = proto.Bool(false)
			d.ResearchPrerequisites = []string{"Electricity"}
		}
	}
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != researchWaitReason("Electricity") {
		t.Fatal(result, err)
	}
}

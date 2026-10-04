package buildingruntime

import (
	"context"
	"testing"
)

// With every generator unavailable and the research it requires unfinished,
// the power goal reports the project it waits on rather than
// no_affordable_generator: EnsureResearch's roadmap is the method (#230).
func TestRoundsPowerReportsTheResearchItWaitsOn(t *testing.T) {
	t.Parallel()
	p, _, n, _ := powerFixture(t, false)
	n.catalogRow("WoodFiredGenerator").Research = []string{"Electricity"}
	n.finished = []string{}
	result, err := p.Step(context.Background())
	if err != nil || result.Verdict != researchWait("Electricity") {
		t.Fatal(result, err)
	}
}

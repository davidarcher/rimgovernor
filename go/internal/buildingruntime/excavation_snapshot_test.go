package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// excavationRound is the initial shelter planner's first step on the
// mountain fixture (Neolithic tribal8 baseline, predig 6), recorded from
// acceptance run shelter/excavation-round at 4528e874f (#745): the dig
// search, the chosen target's verification, stage and stage-support site
// reads and the dig-or-shell choice. The excavation-hazard, -reroute and
// -breach cases replaced here edit its site reads into the changed map
// each staged, since their mountain fixture no longer finds a site.
const excavationRound = "testdata/excavation-round.json.gz"

func loadExcavation(t *testing.T) snapshot.Planner {
	t.Helper()
	p, err := snapshot.LoadPlanner(excavationRound)
	if err != nil {
		t.Fatal(err)
	}
	return p
}

// siteRead is the step's recorded read for purpose.
func siteRead(t *testing.T, p snapshot.Planner, purpose string) snapshot.ExcavationRead {
	t.Helper()
	for _, r := range p.Sites {
		if r.Purpose == purpose {
			r.Site.Cells = slices.Clone(r.Site.Cells)
			return r
		}
	}
	t.Fatalf("no %s site read recorded", purpose)
	return snapshot.ExcavationRead{}
}

// A neolithic colony beside rock digs a round room, and chooses the dig
// over the open-site shell.
func TestExcavationSnapshotDigsARoundRoom(t *testing.T) {
	t.Parallel()
	p := loadExcavation(t)
	if len(p.Excavation) == 0 {
		t.Fatal("no dig search recorded")
	}
	targets, err := policy.ExcavationSites(p.Excavation[0])
	if err != nil || len(targets) == 0 {
		t.Fatal(targets, err)
	}
	if shape := targets[0].Shape; shape.Kind != policy.ExcavationEllipse {
		t.Fatalf("first target is %+v, want the radius-4 round room", shape)
	}
	verify := siteRead(t, p, "verify")
	if verify.Target.Key() != targets[0].Key() || !excavationVerified(verify.Target, false, verify.Site) {
		t.Fatalf("the round target %s did not verify", verify.Target.Key())
	}
	if len(p.Choices) == 0 || !p.Choices[0].Excavate {
		t.Fatalf("choices %+v: the dig should beat the shell", p.Choices)
	}
	stage := siteRead(t, p, "stage")
	review, reason, door := excavationNext(stage.Target, stage.Site)
	if reason != "" || door || len(review.Stage) == 0 {
		t.Fatal(review, reason, door)
	}
}

// Two fogged interior cells unfog into an ancient wall as the dig reaches
// them: no stage designates them and the room is dug around them.
func TestExcavationSnapshotDigsAroundRevealedHazards(t *testing.T) {
	t.Parallel()
	p := loadExcavation(t)
	stage := siteRead(t, p, "stage")
	interior := stage.Target.InteriorCells()
	hazards := map[domain.Cell]bool{interior[len(interior)/2]: true, interior[len(interior)/2+1]: true}
	for i, c := range stage.Site.Cells {
		if hazards[c.Cell] {
			stage.Site.Cells[i] = bridge.ExcavationSiteCell{Cell: c.Cell, Definition: "AncientConcreteWall", Roof: c.Roof, HoldsRoof: true}
		}
	}
	review, reason, door := excavationNext(stage.Target, stage.Site)
	if reason != "" || door || len(review.Stage) == 0 {
		t.Fatal(review, reason, door)
	}
	for _, c := range review.Stage {
		if hazards[c] {
			t.Fatalf("stage %v designates the hazard %v", review.Stage, c)
		}
	}
	for c := range hazards {
		if !slices.Contains(review.Kept, c) {
			t.Fatalf("hazard %v not kept: %v", c, review.Kept)
		}
	}
	// A project already under way digs on around them; a fresh target
	// showing them is passed over.
	if !excavationVerified(stage.Target, true, stage.Site) || excavationVerified(stage.Target, false, stage.Site) {
		t.Fatal("hazards: resumed project should hold, fresh target should not")
	}
}

// The corridor mouth is walled shut after stage 0: the project in flight
// ends and is not resumed, so the next review searches afresh (the
// round fixture has one lane, so which second lane wins is not replayed).
func TestExcavationSnapshotReroutesAroundASealedCorridor(t *testing.T) {
	t.Parallel()
	p := loadExcavation(t)
	stage := siteRead(t, p, "stage")
	stage.Site.AccessReachable = false
	if _, reason, _ := excavationNext(stage.Target, stage.Site); reason != BuildingExcavationBlocked {
		t.Fatalf("sealed corridor: reason %q, want %q", reason, BuildingExcavationBlocked)
	}
	if excavationVerified(stage.Target, true, stage.Site) {
		t.Fatal("the sealed project was resumed")
	}
}

// The rock around the room is levelled after stage 0 so the rest of the
// dig would leave its roof unsupported: the project ends, is not resumed,
// and with no dig on offer the shelter is the open-site shell.
func TestExcavationSnapshotBreachFallsBackToTheShell(t *testing.T) {
	t.Parallel()
	p := loadExcavation(t)
	stage := siteRead(t, p, "stage")
	stage.Site.Support = policy.ExcavationSupportUnsupported
	if _, reason, _ := excavationNext(stage.Target, stage.Site); reason != BuildingExcavationBlocked {
		t.Fatalf("unsupported: reason %q, want %q", reason, BuildingExcavationBlocked)
	}
	if excavationVerified(stage.Target, true, stage.Site) {
		t.Fatal("the unsupported project was resumed")
	}
	choice := p.Choices[0]
	if choice.Shell == nil || policy.ChooseExcavation(choice.Anchor, choice.Shell, nil) {
		t.Fatalf("with no dig the shell %+v should be chosen", choice.Shell)
	}
	// A collapse still pending holds the project instead of ending it.
	stage.Site.CollapsePending = true
	if _, reason, _ := excavationNext(stage.Target, stage.Site); reason != BuildingMethodUnknown {
		t.Fatalf("collapse pending: reason %q", reason)
	}
}

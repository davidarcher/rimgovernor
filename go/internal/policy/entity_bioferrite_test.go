package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func extractor(id domain.PawnID, perDay float64) CapturableEntity {
	e := heldEntity(id, domain.Known(false))
	e.Mode, e.ExtractBioferrite, e.HarvesterAttached, e.BioferritePerDay = domain.Known(ContainmentStudy), domain.Known(false), domain.Known(false), domain.Known(perDay)
	return e
}

func researched(done bool) domain.Fact[ResearchFacts] {
	f := ResearchFacts{Projects: []ResearchProjectID{ResearchBioferriteExtraction}}
	if done {
		f.Finished = []ResearchProjectID{ResearchBioferriteExtraction}
	}
	return domain.Known(f)
}

func TestBioferriteFlagIsSetOnceResearchIsDoneAndNoHarvesterIsAttached(t *testing.T) {
	p := studyPlanning(extractor("e2", 1.5), extractor("e1", 1))
	got := BioferriteHarvestOwed(p, researched(true))
	if len(got.Enable) != 2 || got.Enable[0] != "e1" || got.Enable[1] != "e2" || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
	if !entityBioferriteOwed(p, researched(true)) {
		t.Fatal("a flag to set is a standing work")
	}
	if got := BioferriteHarvestOwed(p, researched(false)); len(got.Enable) != 0 || len(got.Issues) != 0 {
		t.Fatalf("before the research: %+v", got)
	}
}

// An attached harvester forces the flag false every tick, so setting it
// there is pointless.
func TestBioferriteFlagIsNotSetBesideAHarvester(t *testing.T) {
	e := extractor("e1", 1)
	e.HarvesterAttached = domain.Known(true)
	if got := BioferriteHarvestOwed(studyPlanning(e), researched(true)); len(got.Enable) != 0 || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
}

// The yield is floor(perDay * 4): below a quarter bioferrite per day an
// extraction yields nothing. The cooldown is the game's own: while
// BioferriteExtracted stands the per-day figure is 0, so the flag waits for
// the 8 days to end instead of being set into a dead job.
func TestBioferriteFlagWaitsForAYieldAndTheCooldown(t *testing.T) {
	if BioferriteYield(.25) != 1 || BioferriteYield(.2) != 0 || BioferriteYield(0) != 0 || BioferriteYield(1.9) != 7 {
		t.Fatal("yield is floor(perDay*4)")
	}
	for name, perDay := range map[string]float64{"cooling down": 0, "too small": .2} {
		if got := BioferriteHarvestOwed(studyPlanning(extractor("e1", perDay)), researched(true)); len(got.Enable) != 0 || len(got.Issues) != 0 {
			t.Fatalf("%s: %+v", name, got)
		}
	}
	if got := BioferriteHarvestOwed(studyPlanning(extractor("e1", .25)), researched(true)); len(got.Enable) != 1 {
		t.Fatalf("%+v", got)
	}
}

func TestBioferriteFlagSkipsReleaseSetFlagsAndUnheldEntities(t *testing.T) {
	release := extractor("e1", 1)
	release.Mode = domain.Known(ContainmentRelease)
	set := extractor("e2", 1)
	set.ExtractBioferrite = domain.Known(true)
	unheld := extractor("e3", 1)
	unheld.Held = domain.Known(false)
	dead := extractor("e4", 1)
	dead.Dead = domain.Known(true)
	if got := BioferriteHarvestOwed(studyPlanning(release, set, unheld, dead), researched(true)); len(got.Enable) != 0 || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
	// Studying is unaffected by the flag: every mode but Release is harvested.
	for _, mode := range []ContainmentMode{ContainmentMaintainOnly, ContainmentStudy, ContainmentExecute} {
		e := extractor("e1", 1)
		e.Mode = domain.Known(mode)
		if got := BioferriteHarvestOwed(studyPlanning(e), researched(true)); len(got.Enable) != 1 {
			t.Fatalf("%s: %+v", mode, got)
		}
	}
	// No Anomaly: nothing is held.
	if got := BioferriteHarvestOwed(ContainmentPlanning{Entities: domain.Unknown[[]CapturableEntity]()}, researched(true)); len(got.Enable) != 0 || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
}

// An unread fact is a loud issue and never an order.
func TestBioferriteUnreadInputsAreIssues(t *testing.T) {
	for name, mutate := range map[string]func(*CapturableEntity){
		"mode":      func(e *CapturableEntity) { e.Mode = domain.Unknown[ContainmentMode]() },
		"flag":      func(e *CapturableEntity) { e.ExtractBioferrite = domain.Unknown[bool]() },
		"harvester": func(e *CapturableEntity) { e.HarvesterAttached = domain.Unknown[bool]() },
		"yield":     func(e *CapturableEntity) { e.BioferritePerDay = domain.Unknown[float64]() },
		"held":      func(e *CapturableEntity) { e.Held = domain.Unknown[bool]() },
	} {
		e := extractor("e1", 1)
		mutate(&e)
		got := BioferriteHarvestOwed(studyPlanning(e), researched(true))
		if len(got.Enable) != 0 || len(got.Issues) != 1 || !strings.Contains(got.Issues[0], "e1") {
			t.Fatalf("%s: %+v", name, got)
		}
	}
	got := BioferriteHarvestOwed(studyPlanning(extractor("e1", 1)), domain.Unknown[ResearchFacts]())
	if len(got.Enable) != 0 || len(got.Issues) != 1 || !strings.Contains(got.Issues[0], "BioferriteExtraction") {
		t.Fatalf("research unread: %+v", got)
	}
}

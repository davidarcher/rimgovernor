package main

import "testing"

func TestGearWorkshopAndResearchDoNotRequireResourceTargets(t *testing.T) {
	c := serveConfig{roundsGearPlans: true, roundsWorkshopPlans: true, roundsResearchPlans: true}
	if c.resourceTargetsConfigured() || !c.workshopPlans() || !c.researchPlans() {
		t.Fatal("gear prerequisites must compose without resource targets")
	}
	c.roundsGearPlans = false
	if c.workshopPlans() {
		t.Fatal("unconfigured production enabled prerequisite planners")
	}
	c.roundsGearPlans, c.roundsWorkshopPlans = true, false
	if c.workshopPlans() {
		t.Fatal("disabled workshop enabled gear prerequisites")
	}
	// Research follows its family alone: the default ladder always has rungs.
	if !c.researchPlans() {
		t.Fatal("the research family did not compose EnsureResearch")
	}
}

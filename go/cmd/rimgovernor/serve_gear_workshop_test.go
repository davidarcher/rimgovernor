package main

import "testing"

func TestGearWorkshopAndResearchDoNotRequireResourceTargets(t *testing.T) {
	c := serveConfig{routineGearPlans: true, routineWorkshopPlans: true, routineResearchPlans: true}
	if c.resourceTargetsConfigured() || !c.workshopPlans() || !c.researchPlans() {
		t.Fatal("gear prerequisites must compose without resource targets")
	}
	c.routineGearPlans = false
	if c.workshopPlans() || c.researchPlans() {
		t.Fatal("unconfigured production enabled prerequisite planners")
	}
	c.routineGearPlans, c.routineWorkshopPlans = true, false
	if c.workshopPlans() || c.researchPlans() {
		t.Fatal("disabled workshop enabled gear prerequisites")
	}
}

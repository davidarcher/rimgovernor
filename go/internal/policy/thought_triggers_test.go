package policy

import (
	"strings"
	"testing"
)

// TestThoughtTriggerTableOwnsProvisionedThoughts checks the generated
// thought-to-trigger table (go run ./cmd/thoughtaudit) carries, for the
// thoughts the mood review provisions, the state they assume and the
// hand-kept owner. HighExpectations, SkyHighExpectations and AteAwfulMeal
// are not ThoughtDefs in the installed game, so the table has no rows for
// them (and no owner entry exists for them).
func TestThoughtTriggerTableOwnsProvisionedThoughts(t *testing.T) {
	owned := map[string]struct {
		dependency string
		owner      ConcernID
	}{
		"AteRawFood":      {"other", EnsureCooking},
		"AteWithoutTable": {"room_stat:Impressiveness", EnsureComfort},
		"NeedJoy":         {"need:joy", EnsureComfort},
		"SleptOnGround":   {"room_role:Bedroom", MaintainHousing},
		"SleptOutside":    {"room_role:Bedroom", MaintainHousing},
		"EnvironmentDark": {"light", MaintainLighting},
		"EnvironmentCold": {"temperature", EnsureTemperatureSafety},
		"EnvironmentHot":  {"temperature", EnsureTemperatureSafety},
		"NeedBeauty":      {"need:beauty", MaintainCleanFacilities},
		"NeedRoomSize":    {"room", MaintainHousing},
	}
	for def, want := range owned {
		row, ok := ThoughtAuditRow(def)
		if !ok {
			t.Errorf("table has no row for %s", def)
			continue
		}
		if !strings.Contains(row.Dependency, want.dependency) {
			t.Errorf("%s dependency %q lacks %q", def, row.Dependency, want.dependency)
		}
		if owners := thoughtOwners(def); len(owners) != 1 || owners[0] != want.owner {
			t.Errorf("%s owners = %v, want %s", def, owners, want.owner)
		}
	}
	for _, def := range []string{"HighExpectations", "SkyHighExpectations", "AteAwfulMeal"} {
		if _, ok := ThoughtAuditRow(def); ok {
			t.Errorf("%s became a ThoughtDef; give it a dependency check", def)
		}
	}
	for _, def := range []string{"SleptInBarracks", "ApparelDamaged", "Insulted"} {
		if _, ok := ThoughtAuditRow(def); !ok || len(thoughtOwners(def)) != 0 {
			t.Errorf("%s should be an audited thought with no owner", def)
		}
	}
	for _, concern := range []ConcernID{EnsureCooking, EnsureComfort, MaintainHousing, MaintainLighting, EnsureTemperatureSafety, MaintainCleanFacilities} {
		if !MoodProvisionConcern(concern) {
			t.Errorf("%s owns no audited thought", concern)
		}
	}
}

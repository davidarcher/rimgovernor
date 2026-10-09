package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Snapshot tests for the facility and medical cases whose decision
// the rounds's own facts carry; see rounds_snapshot_test.go.

// facility/basic-comfort: the fixture's hut stands bare, so the foothold
// comfort goal furnishes it with a table first, under the roof.
func TestSnapshotBasicComfortFurnishesBareHut(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "basic-comfort-bare-hut")
	selected, reason, err := recordedPlanner(r, policy.EnsureComfort).selectBasicComfort(*r.Projection)
	if err != nil || selected == nil || selected.definition != "Table1x2c" || selected.environment != policy.PlacementIndoors {
		t.Fatalf("basic comfort: selected %+v reason %q err %v, want an indoor Table1x2c", selected, reason, err)
	}
}

// facility/comfort: the tribal8 colony past its startup ladder has no
// dining room, so EnsureComfort builds a table the census can score as one.
func TestSnapshotComfortBuildsDiningTable(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "comfort-no-dining")
	if c, known := r.Facts.Comfort.Value(); !known || len(c.Dining) != 0 {
		t.Fatalf("comfort census %+v: want a known census with no dining facility", c)
	}
	selected, reason, err := recordedPlanner(r, policy.EnsureComfort).selectComfort(*r.Projection, policy.ComfortHistory{})
	if err != nil || selected == nil || selected.definition != "Table1x2c" || selected.environment != policy.PlacementIndoors ||
		selected.facility == nil || selected.facility.Role != policy.RoomRoleDiningRoom {
		t.Fatalf("comfort: selected %+v reason %q err %v, want a Table1x2c hosted by a dining room", selected, reason, err)
	}
}

// medical/disease: two Plague patients with industrial medicine in stock.
// The care cap holds both one tier above the colonists' standing
// cap while the plague lasts.
func TestSnapshotDiseaseRaisesCareCap(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "disease-plague-patients")
	colonists, _ := r.Facts.MedicalPawns.Value()
	base, ok := policy.ColonistCareBase(policy.CoreItemFacts(), r.Facts.Resources, int64(len(colonists)), policy.DefaultMedicalReservePolicy())
	if !ok {
		t.Fatal("standing cap unknown")
	}
	raised := 0
	for _, s := range policy.MedicalCareChanges(r.Facts, policy.DefaultMedicalReservePolicy()) {
		if s.MedicalCare() == base.Raised() {
			raised++
		}
	}
	if raised != 2 {
		t.Fatalf("raised %d colonists to %s, want the two Plague patients: %+v", raised, base.Raised(), policy.MedicalCareChanges(r.Facts, policy.DefaultMedicalReservePolicy()))
	}
}

package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Snapshot tests (#750) for the facility and medical cases whose decision
// the routine review's own facts carry; see routine_snapshot_test.go.

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
// The medical family raises both, and only them, to NormalOrWorse care.
func TestSnapshotDiseaseSelectsIndustrialCare(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "disease-plague-patients")
	work := medicineTierAssignments(r.Facts)
	if len(work) != 2 {
		t.Fatalf("medicine tier assignments %+v: want the two Plague patients", work)
	}
	seen := map[string]bool{}
	for _, w := range work {
		if w.MedicalCare() != string(policy.MedicineIndustrialTier) {
			t.Errorf("%s assigned %s, want %s", w.Pawn(), w.MedicalCare(), policy.MedicineIndustrialTier)
		}
		seen[string(w.Pawn())] = true
	}
	if len(seen) != 2 {
		t.Fatalf("assignments repeat a patient: %+v", work)
	}
}

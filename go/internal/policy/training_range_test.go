package policy

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The mod's defs are the source of the names the template uses, and the retired
// partition and room role are gone from it.
func TestRangeDefNamesAreDefinedByTheNativeMod(t *testing.T) {
	root := filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "Defs")
	things, err := os.ReadFile(filepath.Join(root, "ThingDefs", "TrainingRange.xml"))
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{RangeWeaponDef, RangeDefNames[RangeStand], RangeDefNames[RangeDummy]} {
		if !strings.Contains(string(things), "<defName>"+name+"</defName>") {
			t.Fatal("ThingDef missing from the native mod", name)
		}
	}
	if strings.Contains(string(things), "RimGovernor_TrainingPartition") {
		t.Fatal("the range has no partitions")
	}
	if _, err := os.Stat(filepath.Join(root, "RoomRoleDefs", "TrainingRange.xml")); err == nil {
		t.Fatal("the range is no scored room: its RoomRoleDef is deleted")
	}
}

// The native training job gates on the same skill target the Concern raises the
// range for, and the mod defines the work type, giver and jobs it runs under.
func TestNativeTrainingJobMirrorsTheTarget(t *testing.T) {
	native := filepath.Join("..", "..", "..", "integrations", "rimgovernor-native")
	for dir, names := range map[string][]string{
		"WorkTypeDefs":  {"RimGovernorTraining"},
		"WorkGiverDefs": {"RimGovernor_TrainRange"},
		"JobDefs":       {"RimGovernor_TrainShooting"},
	} {
		def, err := os.ReadFile(filepath.Join(native, "Defs", dir, "RimGovernorTraining.xml"))
		if err != nil {
			t.Fatal(err)
		}
		for _, name := range names {
			if !strings.Contains(string(def), "<defName>"+name+"</defName>") {
				t.Fatal("def missing from the native mod", dir, name)
			}
		}
		if strings.Contains(string(def), "RimGovernor_TrainMelee") {
			t.Fatal("melee leaves the range (#2676)", dir)
		}
	}
}

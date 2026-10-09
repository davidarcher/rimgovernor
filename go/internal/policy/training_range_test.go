package policy

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestRangeLayoutIsTheStaticLaneTemplate(t *testing.T) {
	origin := domain.Cell{X: 10, Z: 20}
	pieces := RangeLayout(origin)
	counts := map[RangePieceKind]int{}
	seen := map[domain.Cell]bool{}
	for _, p := range pieces {
		counts[p.Kind]++
		if seen[p.Cell] {
			t.Fatal("two pieces on one cell", p)
		}
		seen[p.Cell] = true
		if p.Cell.X < origin.X || p.Cell.X >= origin.X+RangeWidth || p.Cell.Z < origin.Z || p.Cell.Z >= origin.Z+RangeLaneLength {
			t.Fatal("piece outside the range footprint", p)
		}
	}
	if counts[RangeStand] != RangeLanes || counts[RangeDummy] != RangeLanes || counts[RangePartition] != (RangeLanes-1)*RangeLaneLength {
		t.Fatal(counts)
	}
	for lane := 0; lane < RangeLanes; lane++ {
		stand, dummy := pieces[2*lane], pieces[2*lane+1]
		if stand.Kind != RangeStand || dummy.Kind != RangeDummy || stand.Lane != lane || dummy.Lane != lane ||
			stand.Cell.X != dummy.Cell.X || dummy.Cell.Z-stand.Cell.Z != RangeLaneLength-1 {
			t.Fatal("lane is not a stand facing a dummy", lane, stand, dummy)
		}
	}
	// A partition column sits between every two lanes, so no stand or dummy is
	// beside another lane's piece.
	for _, p := range pieces {
		if p.Kind == RangePartition && (p.Cell.X-origin.X)%2 != 1 {
			t.Fatal("partition not between lanes", p)
		}
	}
}

func TestRangeLayoutIsPureAndTranslates(t *testing.T) {
	a, b := RangeLayout(domain.Cell{}), RangeLayout(domain.Cell{X: 5, Z: -3})
	if len(a) != len(b) {
		t.Fatal("size differs")
	}
	for i := range a {
		if a[i].Kind != b[i].Kind || b[i].Cell.X-a[i].Cell.X != 5 || b[i].Cell.Z-a[i].Cell.Z != -3 {
			t.Fatal("layout did not translate", a[i], b[i])
		}
	}
}

// The mod's defs and role are the source of the names the template uses.
func TestRangeDefNamesAreDefinedByTheNativeMod(t *testing.T) {
	root := filepath.Join("..", "..", "..", "integrations", "rimgovernor-native", "Defs")
	things, err := os.ReadFile(filepath.Join(root, "ThingDefs", "TrainingRange.xml"))
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range append([]string{RangeWeaponDef}, RangeDefNames[RangeStand], RangeDefNames[RangeDummy], RangeDefNames[RangePartition]) {
		if !strings.Contains(string(things), "<defName>"+name+"</defName>") {
			t.Fatal("ThingDef missing from the native mod", name)
		}
	}
	role, err := os.ReadFile(filepath.Join(root, "RoomRoleDefs", "TrainingRange.xml"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(role), "<defName>"+string(RoomRoleTrainingRange)+"</defName>") {
		t.Fatal("RoomRoleDef missing from the native mod")
	}
}

func TestTrainingRangeCatalogRow(t *testing.T) {
	f, err := Facility(RoomRoleTrainingRange)
	if err != nil || f.Content != "" || f.Status != FacilityPending || !f.Hosts(RoomRoleTrainingRange) || f.Hosts(RoomRoleRoom) {
		t.Fatal(f, err)
	}
}

package buildingruntime

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ladderReading adds the autocannon and uranium slug turret definitions to
// the turret reading, each behind its own research.
func ladderReading(points float64, finished ...policy.ResearchProjectID) observation.RoutineReading {
	read := turretReading()
	read.Projection.Definitions = append(read.Projection.Definitions,
		observation.PlanningDefinition{Name: policy.TurretAutocannon, Available: domain.Known(true), Research: []string{"HeavyTurrets"}, PowerW: domain.Known(150.0), Size: domain.Known(policy.Bounds{Width: 2, Height: 2}), Costs: domain.Known([]policy.Amount{{Resource: "Steel", Count: 100}})},
		observation.PlanningDefinition{Name: policy.TurretSniper, Available: domain.Known(true), Research: []string{"SniperTurret"}, PowerW: domain.Known(150.0), Size: domain.Known(policy.Bounds{Width: 2, Height: 2}), Costs: domain.Known([]policy.Amount{{Resource: "Steel", Count: 120}})})
	read.Projection.Facts.RaidPoints = domain.Known(points)
	read.Projection.Facts.Research = domain.Known(policy.ResearchFacts{Finished: append([]policy.ResearchProjectID{"Electricity", "GunTurrets"}, finished...)})
	return read
}

func TestDefenseTurretRungFollowsArmoryTierAndResearch(t *testing.T) {
	t.Parallel()
	all := []policy.ResearchProjectID{"Smithing", "Machining", "Fabrication", "HeavyTurrets", "SniperTurret"}
	for _, tc := range []struct {
		name     string
		points   float64
		finished []policy.ResearchProjectID
		want     string
	}{
		{"low threat", 200, all, policy.TurretMini},
		{"machining threat", 1000, all, policy.TurretAutocannon},
		{"fabrication threat", 3000, all, policy.TurretSniper},
		{"no machining", 3000, []policy.ResearchProjectID{"Smithing", "HeavyTurrets", "SniperTurret"}, policy.TurretMini},
		// Fabrication allows the slug turret, but its own research is not
		// finished: the autocannon is the highest open rung.
		{"no sniper research", 3000, []policy.ResearchProjectID{"Smithing", "Machining", "Fabrication", "HeavyTurrets"}, policy.TurretAutocannon},
	} {
		request := defenseTurretRequest(ladderReading(tc.points, tc.finished...))
		if request.Turret.Definition != tc.want {
			t.Fatalf("%s: %s want %s", tc.name, request.Turret.Definition, tc.want)
		}
		if tc.want != policy.TurretMini && (request.Turret.Size != policy.Bounds{Width: 2, Height: 2} || len(request.UnitCosts[tc.want]) == 0) {
			t.Fatalf("%s: %+v", tc.name, request)
		}
	}
}

// TestTurretReplacementReplaysDeconstructThenRebuild walks one in-place
// upgrade through the record: the removal tier comes before the turret
// tier, and once the census no longer finds the mini turret the removal
// leaves and the turret tier re-opens on the autocannon at the same anchor.
func TestTurretReplacementReplaysDeconstructThenRebuild(t *testing.T) {
	t.Parallel()
	west, east := domain.Cell{X: 5, Z: 23}, domain.Cell{X: 13, Z: 23}
	conduit := domain.Cell{X: 14, Z: 26}
	record := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{{Name: policy.TierTurrets, Built: true, Reserved: []domain.Cell{west, east}, Buildings: []store.DefenseBuilding{
		{Definition: policy.TurretMini, Cell: west, Rotation: domain.North}, {Definition: policy.TurretMini, Cell: east, Rotation: domain.North}, {Definition: defenseConduitDefinition, Cell: conduit, Rotation: domain.North}}}}}
	if !defenseReplaceDue(record, 5000) {
		t.Fatal("built mini tier not due")
	}
	old, _ := domain.NewBuilding(policy.TurretMini, east, domain.North, "")
	next, _ := domain.NewBuilding(policy.TurretAutocannon, east, domain.North, "")
	standing := []policy.StandingTurret{{Building: old, Cells: []domain.Cell{east}}}
	w, _ := domain.NewBuilding(policy.TurretMini, west, domain.North, "")
	standing = append([]policy.StandingTurret{{Building: w, Cells: []domain.Cell{west}}}, standing...)
	applyTurretReplacement(&record, old, next, standing, policy.Bounds{Width: 2, Height: 2})
	if defenseReplaceDue(record, 5000) || !defenseTurretReplacing(record) {
		t.Fatal("a second replacement could start")
	}
	turrets, _, _ := record.Tier(policy.TierTurrets)
	if turrets.Buildings[1].Definition != policy.TurretAutocannon || turrets.Buildings[1].Cell != east || !reflect.DeepEqual(turrets.Reserved, []domain.Cell{west, {X: 13, Z: 23}, {X: 13, Z: 24}, {X: 14, Z: 23}, {X: 14, Z: 24}}) {
		t.Fatalf("%+v", turrets)
	}
	removal := record.Tiers[1]
	if !removal.Remove || removal.Name != "turrets-replace-13-23-1" || !reflect.DeepEqual(removal.Buildings, []store.DefenseBuilding{{Definition: policy.TurretMini, Cell: east, Rotation: domain.North}}) {
		t.Fatalf("%+v", removal)
	}
	if g := defenseRecordGeometry(record); slices.Contains(g.Reserved, east) {
		t.Fatal("replacement tier reserved in the geometry")
	}
	// The mini turret still stands: the removal is pending, the turret tier
	// re-opens on the missing autocannon, which admission holds behind the
	// removal in the tier order.
	census := &defenseCensus{edifice: map[domain.Cell]string{west: policy.TurretMini, east: policy.TurretMini}, conduits: map[domain.Cell]bool{conduit: true}}
	defenseTierCensus(&record, census)
	if len(record.Tiers) != 2 {
		t.Fatal(record.Tiers)
	}
	// Deconstructed: the removal leaves and the rebuild is the only gap.
	delete(census.edifice, east)
	defenseTierCensus(&record, census)
	turrets, buildings, _ := record.Tier(policy.TierTurrets)
	if len(record.Tiers) != 1 || turrets.Built {
		t.Fatal(record.Tiers)
	}
	missing := defenseMissingBuildings(buildings, census)
	if len(missing) != 1 || missing[0].Definition() != policy.TurretAutocannon || missing[0].Cell() != east {
		t.Fatal(missing)
	}
	// Rebuilt: the tier stands again.
	census.edifice[east] = policy.TurretAutocannon
	defenseTierCensus(&record, census)
	if turrets, _, _ := record.Tier(policy.TierTurrets); !turrets.Built {
		t.Fatal("rebuilt tier not built")
	}
}

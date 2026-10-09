package policy

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestEveryPlannedRoleHasATier reads the package's PlannedRole constants, so a
// new room role without a tier row fails here.
func TestEveryPlannedRoleHasATier(t *testing.T) {
	fset := token.NewFileSet()
	entries, err := os.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	seen := 0
	for _, entry := range entries {
		name := entry.Name()
		if !strings.HasSuffix(name, ".go") || strings.HasSuffix(name, "_test.go") {
			continue
		}
		file, err := parser.ParseFile(fset, name, nil, 0)
		if err != nil {
			t.Fatal(err)
		}
		{
			for _, decl := range file.Decls {
				gen, ok := decl.(*ast.GenDecl)
				if !ok || gen.Tok != token.CONST {
					continue
				}
				var typ string
				for _, spec := range gen.Specs {
					vs := spec.(*ast.ValueSpec)
					if id, ok := vs.Type.(*ast.Ident); ok {
						typ = id.Name
					} else if vs.Type != nil {
						typ = ""
					}
					if typ != "PlannedRole" || len(vs.Values) != 1 {
						continue
					}
					lit, ok := vs.Values[0].(*ast.BasicLit)
					if !ok {
						continue
					}
					seen++
					if _, ok := plannedRoleTier[PlannedRole(strings.Trim(lit.Value, `"`))]; !ok {
						t.Errorf("planned role %s has no construction tier", vs.Names[0].Name)
					}
				}
			}
		}
	}
	if seen != len(plannedRoleTier) {
		t.Errorf("tier table has %d rows for %d planned roles", len(plannedRoleTier), seen)
	}
}

func TestRoomTierLadder(t *testing.T) {
	for role, want := range map[PlannedRole]domain.ConstructionTier{
		PlannedShelter: domain.TierSurvive, PlannedKitchen: domain.TierSurvive,
		PlannedBedroom: domain.TierSustain, PlannedHospital: domain.TierSustain, PlannedLab: domain.TierSustain, PlannedFreezer: domain.TierSustain,
		PlannedDining: domain.TierComfort, PlannedThrone: domain.TierComfort, PlannedBarn: domain.TierComfort,
		PlannedWorkshop:  domain.TierProduce,
		PlannedGraveyard: domain.TierExpand, PlannedWasteYard: domain.TierExpand,
		PlannedRole("never-seen"): domain.TierExpand,
	} {
		if got := RoomTier(role); got != want {
			t.Errorf("RoomTier(%s) = %d, want %d", role, got, want)
		}
	}
}

func TestPlannerTier(t *testing.T) {
	for _, c := range []struct {
		concern ConcernID
		phase   Phase
		want    domain.ConstructionTier
	}{
		{MaintainHousing, HousingShelter, domain.TierSurvive},
		{MaintainHousing, HousingExpansion, domain.TierSurvive},
		{MaintainHousing, HousingSleeping, domain.TierSustain},
		{EnsureCooking, "", domain.TierSurvive},
		{MaintainRefrigeration, "", domain.TierSustain},
		{EnsureComfort, "", domain.TierComfort},
		{MaintainResource, "", domain.TierProduce},
		{EnsureBasicPower, "", domain.TierExpand},
	} {
		if got := PlannerTier(c.concern, c.phase); got != c.want {
			t.Errorf("PlannerTier(%s, %s) = %d; want %d", c.concern, c.phase, got, c.want)
		}
	}
	for _, concern := range []ConcernID{EnsureBasicDefense, EnsureDefensiveLayout, ConcernID("never-seen")} {
		if got := PlannerTier(concern, ""); got != domain.TierExpand {
			t.Errorf("PlannerTier(%s) = %d; unlisted concerns are Expand until #2527", concern, got)
		}
	}
}

// An adopted site keeps the tier native reads on it; one without is Expand.
func TestAdoptedTier(t *testing.T) {
	tiered := ConstructionSite{Tier: domain.Known(domain.TierSustain)}
	if got := AdoptedTier(tiered); got != domain.TierSustain {
		t.Errorf("AdoptedTier(tiered) = %d, want Sustain", got)
	}
	if got := AdoptedTier(ConstructionSite{}); got != domain.TierExpand {
		t.Errorf("AdoptedTier(untiered) = %d, want Expand", got)
	}
}

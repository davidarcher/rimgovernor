package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// concaveClaims builds the Wall/Door claims of two 4x4 chambers joined by
// a one-cell connector, centred on c.
func concaveClaims(t *testing.T, plan domain.PlanID, c domain.Cell) ([]policy.ConstructionClaim, domain.RoomFootprint) {
	t.Helper()
	shell, err := domain.UnionFootprint([]domain.InteriorRect{{X: c.X - 5, Z: c.Z - 2, Width: 4, Height: 4}, {X: c.X - 1, Z: c.Z, Width: 3, Height: 1}, {X: c.X + 2, Z: c.Z - 2, Width: 4, Height: 4}}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	var claims []policy.ConstructionClaim
	for _, cell := range shell.Walls() {
		def := "Wall"
		if cell == shell.Door() {
			def = "Door"
		}
		b, err := domain.NewBuilding(def, cell, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		claims = append(claims, policy.ConstructionClaim{Plan: plan, Building: b})
	}
	return claims, shell
}

func TestShellInteriorsCoverThePlannedRingsFloor(t *testing.T) {
	claims, shell := concaveClaims(t, "starter-shell", domain.Cell{X: 40, Z: 40})
	var actions []domain.Action
	for i, claim := range claims {
		a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("starter-shell-%d", i)), claim.Building)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	spec, err := domain.NewPlan("starter-shell", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	// Every wall is still pending: the floor is protected before the
	// shell stands, not only once its walls are completed claims.
	var progress []domain.Progress
	for _, a := range actions {
		p, err := domain.NewProgress(spec, a.ID())
		if err != nil {
			t.Fatal(err)
		}
		progress = append(progress, p)
	}
	b := shell.Bounds()
	// The same floor is covered once the shelter goal has left the review
	// and only the completed claims remain (#217: the batch drawn after
	// the hut stood went inside it).
	for name, cells := range map[string][]domain.Cell{
		"pending plan":     shellInteriors([]store.PlanState{{Spec: spec, Progress: progress}}, nil),
		"completed claims": shellInteriors(nil, claims),
	} {
		inside := map[domain.Cell]bool{}
		for _, cell := range cells {
			inside[cell] = true
		}
		if len(inside) != int(b.Width*b.Height) {
			t.Fatalf("%s: protected %d cells, want the %dx%d bounds", name, len(inside), b.Width, b.Height)
		}
		for x := b.X; x < b.X+b.Width; x++ {
			for z := b.Z; z < b.Z+b.Height; z++ {
				if !inside[domain.Cell{X: x, Z: z}] {
					t.Fatalf("%s: cell %d,%d of the shell's floor is not protected", name, x, z)
				}
			}
		}
	}
	if len(shellInteriors(nil, nil)) != 0 {
		t.Fatal("no plans protected cells")
	}
}

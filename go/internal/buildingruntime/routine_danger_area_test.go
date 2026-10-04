package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The NoDanger area leaves out the cells a fight covered for the whole
// cooldown, then takes them back (#1802).
func TestNoDangerAreaFollowsTheDangerWindow(t *testing.T) {
	home := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}, {X: 200, Z: 1}}
	room := policy.Room{ID: "1", Enclosed: domain.Known(true), Roofed: domain.Known(true), Cells: home[:1]}
	projection := observation.ColonyProjection{Rooms: domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{room}})}
	projection.Facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Home: domain.Known(home)})
	noDanger := func(m *safeAreaMemory) int {
		t.Helper()
		if _, err := m.review("w", projection); err != nil {
			t.Fatal(err)
		}
		m.mu.Lock()
		defer m.mu.Unlock()
		return len(m.areas[policy.NoDangerAreaKey].want)
	}
	var m safeAreaMemory
	if got := noDanger(&m); got != 3 {
		t.Fatal("no danger yet: the whole home", got)
	}
	// A hostile live near the first two cells.
	seeds := []domain.Cell{{X: 1, Z: 1}}
	if window, _ := m.dangerWindow("w", domain.Known(int64(1)), seeds, 100).Value(); !window {
		t.Fatal("a live hostile opens the window")
	}
	if got := noDanger(&m); got != 1 {
		t.Fatal("the fight's reach leaves home", got)
	}
	// Dead, but inside the cooldown: the cells stay out.
	if window, _ := m.dangerWindow("w", domain.Known(int64(0)), nil, 100+policy.DangerCooldown-1).Value(); !window {
		t.Fatal("the cooldown holds the window")
	}
	if got := noDanger(&m); got != 1 {
		t.Fatal("the cooldown keeps the cells out", got)
	}
	if window, _ := m.dangerWindow("w", domain.Known(int64(0)), nil, 100+policy.DangerCooldown).Value(); window {
		t.Fatal("the cooldown ends")
	}
	if got := noDanger(&m); got != 3 {
		t.Fatal("a closed window gives the cells back", got)
	}
}

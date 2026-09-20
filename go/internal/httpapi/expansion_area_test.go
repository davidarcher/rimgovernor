package httpapi

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
)

type extentPlayer struct {
	*playerFixture
	removed bool
	cells   []domain.Cell
}

func (p *extentPlayer) ChangeExpansionArea(_ context.Context, _ store.World, _, _ string, cells []domain.Cell, remove bool) error {
	p.calls++
	p.removed = remove
	p.cells = cells
	return nil
}

func TestExpansionAreaAuthenticatedAddRemove(t *testing.T) {
	s, f := playerAPI(t)
	p := &extentPlayer{playerFixture: f}
	s.player = p
	body := `{"expected":{"colonyId":"colony","loadToken":"load","mapId":1},"id":"east","reason":"reserve farmland","cells":[{"x":12,"z":13}]}`
	if got := playerCall(s, "POST", "/api/player/expansion-area/add", body, ""); got.Code != 403 || p.calls != 0 {
		t.Fatalf("unauthorized mutation: %d", got.Code)
	}
	if got := playerCall(s, "POST", "/api/player/expansion-area/add", body, s.playerToken); got.Code != 200 || p.calls != 1 || len(p.cells) != 1 || p.cells[0] != (domain.Cell{X: 12, Z: 13}) {
		t.Fatalf("add: %d %s", got.Code, got.Body)
	}
	body = `{"expected":{"colonyId":"colony","loadToken":"load","mapId":1},"id":"east","reason":"clear reservation"}`
	if got := playerCall(s, "POST", "/api/player/expansion-area/remove", body, s.playerToken); got.Code != 200 || !p.removed || p.calls != 2 {
		t.Fatalf("remove: %d %s", got.Code, got.Body)
	}
	for _, cells := range []string{`[{}]`, `[{"x":1}]`, `[{"x":-1,"z":1}]`, `[{"x":1,"z":1},{"x":1,"z":1}]`, `[{"x":1,"z":1,"extra":true}]`} {
		body = `{"expected":{"colonyId":"colony","loadToken":"load","mapId":1},"id":"east","reason":"test","cells":` + cells + `}`
		if got := playerCall(s, "POST", "/api/player/expansion-area/add", body, s.playerToken); got.Code != 400 || p.calls != 2 {
			t.Fatalf("invalid cells accepted: %s, %d", cells, got.Code)
		}
	}
}

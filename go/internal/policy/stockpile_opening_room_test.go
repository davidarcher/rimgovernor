package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStorageRoomSiteCentresOnUnobservedRoom(t *testing.T) {
	open := newStockpileOpen(StockpileRequest{Bounds: Bounds{Width: 300, Height: 300}})
	room := Rectangle{X: 155, Z: 115, Width: 9, Height: 7}
	site, ok := storageRoomSite(open, room, 5)
	if !ok || site != (Rectangle{157, 116, 5, 5}) {
		t.Fatalf("site %v ok=%v", site, ok)
	}
	if _, ok := storageRoomSite(open, Rectangle{X: 1, Z: 1, Width: 4, Height: 4}, 5); ok {
		t.Fatal("a 4x4 room took a 5x5 zone")
	}
	_ = domain.Cell{}
}

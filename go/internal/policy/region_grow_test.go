package policy

import "testing"

func ringRegion() Region {
	return RectRegion(Rectangle{X: 4, Z: 4, Width: 9, Height: 9}).minus(RectRegion(Rectangle{X: 6, Z: 6, Width: 5, Height: 5}))
}

func TestRegionGrowOfARectangleIsItsPad(t *testing.T) {
	r := Rectangle{X: 3, Z: 5, Width: 4, Height: 2}
	if got := RectRegion(r).Grow(2); !got.Equal(RectRegion(pad(r, 2))) {
		t.Fatal("grown rectangle is not padded", got.Bounds())
	}
}

func TestRingRegionIsNotItsBoundingBoxWhenMarkedOrOverlapped(t *testing.T) {
	ring := ringRegion()
	if ring.OverlapsRect(Rectangle{X: 7, Z: 7, Width: 3, Height: 3}) {
		t.Fatal("a rectangle in the hole overlaps the ring")
	}
	if !ring.OverlapsRect(Rectangle{X: 7, Z: 7, Width: 3, Height: 6}) {
		t.Fatal("a rectangle reaching the ring misses it")
	}
	const w, h = 20, 20
	fp := make([]bool, w*h)
	markRegion(fp, w, h, ring)
	if fp[8*w+8] || !fp[4*w+4] || !fp[8*w+4] {
		t.Fatal("markRegion marked the hole or missed the ring")
	}
}

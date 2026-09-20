package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestExtentWindowUnknownFallsBackToFocus(t *testing.T) {
	t.Parallel()
	r := ExtentWindowRequest{Extent: domain.Unknown[ColonyExtent](), Focus: domain.Cell{X: 10, Z: 10}, Bounds: Bounds{Width: 30, Height: 30}, Half: 4}
	w, source, err := ExtentWindow(r)
	if err != nil || source != ExtentWindowFocus || w != (Rectangle{X: 6, Z: 6, Width: 9, Height: 9}) {
		t.Fatalf("%+v %s %v", w, source, err)
	}
	// A known empty extent is the same fallback, clipped at the map edge.
	r.Extent, r.Focus = domain.Known(ColonyExtent{}), domain.Cell{X: 1, Z: 28}
	if w, source, err = ExtentWindow(r); err != nil || source != ExtentWindowFocus || w != (Rectangle{X: 0, Z: 24, Width: 6, Height: 6}) {
		t.Fatalf("%+v %s %v", w, source, err)
	}
}

func TestExtentWindowCentresOnExtentAndKeepsFocus(t *testing.T) {
	t.Parallel()
	extent, err := DeriveColonyExtent(extentFixture(t, domain.Cell{X: 20, Z: 20}, domain.Cell{X: 24, Z: 22}))
	if err != nil {
		t.Fatal(err)
	}
	r := ExtentWindowRequest{Extent: extent, Focus: domain.Cell{X: 21, Z: 21}, Bounds: Bounds{Width: 30, Height: 30}, Half: 3}
	w, source, err := ExtentWindow(r)
	if err != nil || source != ExtentWindowExtent || w != (Rectangle{X: 19, Z: 18, Width: 7, Height: 7}) {
		t.Fatalf("%+v %s %v", w, source, err)
	}
	// Focus far from the extent shifts the window just enough to hold it.
	r.Focus = domain.Cell{X: 10, Z: 21}
	if w, source, err = ExtentWindow(r); err != nil || source != ExtentWindowExtent || w != (Rectangle{X: 10, Z: 18, Width: 7, Height: 7}) {
		t.Fatalf("%+v %s %v", w, source, err)
	}
	// Expansion areas join the bounding box even with an unknown extent.
	r = ExtentWindowRequest{Extent: domain.Unknown[ColonyExtent](), Areas: [][]domain.Cell{{{X: 4, Z: 4}}, {{X: 8, Z: 6}}}, Focus: domain.Cell{X: 6, Z: 5}, Bounds: Bounds{Width: 30, Height: 30}, Half: 2}
	if w, source, err = ExtentWindow(r); err != nil || source != ExtentWindowExtent || w != (Rectangle{X: 4, Z: 3, Width: 5, Height: 5}) {
		t.Fatalf("%+v %s %v", w, source, err)
	}
	// Identical inputs produce an identical window.
	again, _, _ := ExtentWindow(r)
	if again != w {
		t.Fatalf("%+v != %+v", again, w)
	}
}

func TestExtentWindowValidation(t *testing.T) {
	t.Parallel()
	good := ExtentWindowRequest{Focus: domain.Cell{X: 1, Z: 1}, Bounds: Bounds{Width: 10, Height: 10}, Half: 2}
	for _, bad := range []ExtentWindowRequest{
		{Focus: good.Focus, Bounds: Bounds{}, Half: 2},
		{Focus: good.Focus, Bounds: good.Bounds, Half: -1},
		{Focus: domain.Cell{X: 10, Z: 1}, Bounds: good.Bounds, Half: 2},
	} {
		if _, _, err := ExtentWindow(bad); err == nil {
			t.Fatalf("accepted %+v", bad)
		}
	}
	if _, _, err := ExtentWindow(good); err != nil {
		t.Fatal(err)
	}
}

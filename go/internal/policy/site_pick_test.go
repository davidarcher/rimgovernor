package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The dark room splits into two native rooms: the block stays inside the
// room nearest the anchor while it has room, and the picked cells come back
// as disjoint rectangles.
func TestSitePickGroupsByRoomAndMergesRectangles(t *testing.T) {
	r := siteFixture(0.1)
	for i := range r.Field.Site.Cells {
		c := &r.Field.Site.Cells[i]
		if c.Cell.X < 12 && c.Cell.Z < 12 && c.Cell.X >= 6 {
			c.Room = domain.Known("east")
		}
	}
	fungus := fieldCrop("Plant_Fungus", 6, 0.5, 0.5, 0.4)
	fungus.MinGlow = domain.Known(0.0)
	fungus.DietAllowed = domain.Known(true)
	anchor := domain.Cell{X: 0, Z: 0}
	r.Field.Site.Anchor = anchor
	plan := sitePick(r.Field.Site, viableCrop{crop: fungus, needed: 30}, func(s SiteCell) bool { return positive(s.Roofed) && positive(s.Indoors) }, nil, true)
	if plan.Cells != 30 || plan.Unplanted != 0 {
		t.Fatal(plan.Explain())
	}
	seen := map[domain.Cell]bool{}
	for _, patch := range plan.Patches {
		for _, c := range rectCells(patch) {
			if seen[c] || c.X >= 6 || c.Z >= 12 {
				t.Fatal("cell outside the west room or overlapping", c, plan.Explain())
			}
			seen[c] = true
		}
	}
	// Needing more than one room holds spills into the next room.
	plan = sitePick(r.Field.Site, viableCrop{crop: fungus, needed: 100}, func(s SiteCell) bool { return positive(s.Roofed) && positive(s.Indoors) }, nil, true)
	if plan.Cells != 100 {
		t.Fatal(plan.Explain())
	}
	// A cell with no known room is never a candidate for a room kind.
	for i := range r.Field.Site.Cells {
		r.Field.Site.Cells[i].Room = domain.Unknown[string]()
	}
	if plan = sitePick(r.Field.Site, viableCrop{crop: fungus, needed: 10}, func(s SiteCell) bool { return positive(s.Roofed) }, nil, true); plan.Cells != 0 {
		t.Fatal(plan.Explain())
	}
}

func TestSiteMergeRectsCoversSetOnce(t *testing.T) {
	cells := map[domain.Cell]bool{}
	for x := int32(0); x < 4; x++ {
		for z := int32(0); z < 3; z++ {
			cells[domain.Cell{X: x, Z: z}] = true
		}
	}
	cells[domain.Cell{X: 0, Z: 3}] = true
	rects := siteMergeRects(cells)
	if len(rects) != 2 || rects[0] != (Rectangle{0, 0, 4, 3}) || rects[1] != (Rectangle{0, 3, 1, 1}) {
		t.Fatal(rects)
	}
}

// Basin placement is unchanged by the picker: a hydroponics basin still
// sites inside the indoor room.
func TestPlanSiteTypeHydroponicsBasinSitesIndoors(t *testing.T) {
	r := siteFixture(0.1)
	r.Field.Climate = CropClimate{Sowing: domain.Known(false), DaysRemaining: domain.Unknown[float64]()}
	r.Environment = domain.Known(siteEnv(21, siteLamp(domain.Cell{X: 6, Z: 6}, true)))
	c := siteCandidateOf(mustSitePlan(t, r), SiteHydroponics, "Plant_Rice")
	if len(c.Buildings) == 0 {
		t.Fatal(c.Reason)
	}
	for _, b := range c.Buildings {
		for _, f := range siteBasinFootprint(b.Cell) {
			if f.X >= 12 || f.Z >= 12 || f.X < 0 || f.Z < 0 {
				t.Fatal("basin outside the room", b)
			}
		}
	}
}

func mustSitePlan(t *testing.T, r SiteTypeRequest) SiteTypePlan {
	t.Helper()
	plan, _ := PlanSiteType(r)
	return plan
}

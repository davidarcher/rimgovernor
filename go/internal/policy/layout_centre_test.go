package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// TestSiteCoreStaysOffBorderMountain: a mountain along the whole south
// border saves wall and is out of raiders' reach, so edgeCost never charges
// it. The Centre term keeps the core from siting in it anyway: the hauling
// and pawn trips to the rest of the map are the cost.
func TestSiteCoreStaysOffBorderMountain(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	s := zoningSurvey(250, func(x, z int32) SurveyCell {
		if z >= 205 {
			return SurveyCell{Rock: true, Fertility: 1}
		}
		return SurveyCell{Walkable: true, Fertility: 1, Footing: FootingFirm}
	})
	p := SiteCore(LayoutPlan{Zones: Zone(s)}, s, 3, 1, BuildTierCamp)
	if len(p.AllRooms()) == 0 {
		t.Fatal("no rooms")
	}
	sum, n := 0, 0
	for _, r := range p.AllRooms() {
		for _, c := range rectCells(r.Interior) {
			sum += int(c.Z)
			n++
		}
	}
	t.Log(Score(p, s), "mean z", sum/n)
	if sum/n >= 160 {
		t.Fatal("core sited at mean z", sum/n, "of 250, against the southern mountain")
	}
}

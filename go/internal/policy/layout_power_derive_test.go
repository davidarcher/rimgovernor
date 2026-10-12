package policy

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// testPowerModel is Core's rows: a 2300 W turbine in clear weather, a 1700 W
// solar plot, a 1000 Wd battery at half efficiency, and the market value of a
// conduit and a wall cell.
func testPowerModel() PowerModel {
	return PowerModel{
		Wind:    SupplyRow{NominalW: 2300, Build: 300, Profile: WindProfile(0.04, 2, 1)},
		Solar:   SupplyRow{NominalW: 1700, Build: 170, Profile: SolarPowerProfile},
		Battery: PowerBattery{CapacityWD: 1000, Efficiency: 0.5},
		Conduit: 1.9, Wall: 9.5,
	}
}

// withPowerSupply gives the facts the lamp, the power rows and the steel price
// the site count is derived from.
func withPowerSupply(f *StationFacts) {
	f.Catalog.Light = LightModel{Standing: LampModel{30, 38, 12, 214}, Wall: LampModel{30, 28.5, 11, 214}}
	f.Catalog.Power = testPowerModel()
	if f.Items.Market == nil {
		f.Items.Market = map[Resource]float64{}
	}
	f.Items.Market[siteSteelUnit] = 1.9
}

// siteCounts advises a first plan and counts the turbines and solar plots it
// sites.
func siteCounts(t *testing.T, f StationFacts) (turbines, solar int) {
	t.Helper()
	f.Derive = true
	advice, err := AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	for _, n := range advice.Graph.Sites {
		if _, ok := advice.Sites.Site(n.ID); !ok {
			continue
		}
		switch n.Kind {
		case NodeTurbine:
			turbines++
		case NodeSolar:
			solar++
		}
	}
	return turbines, solar
}

func drawFacts(drawW float64) StationFacts {
	f := stationFactsFixture()
	withPowerSupply(&f)
	for _, def := range []string{"Bench", "Bed", "Shelf"} {
		s := f.Catalog.Stations[def]
		s.BaseW = -drawW
		f.Catalog.Stations[def] = s
	}
	return f
}

func TestPowerSiteCountRisesWithDemand(t *testing.T) {
	prev := -1
	var first, last int
	for _, w := range []float64{50, 800, 3000} {
		wind, solar := siteCounts(t, drawFacts(w))
		if wind+solar <= prev {
			t.Errorf("draw %v W sites %d, not above %d", w, wind+solar, prev)
		}
		if prev < 0 {
			first = wind + solar
		}
		prev, last = wind+solar, wind+solar
	}
	if first < 1 || last <= first {
		t.Errorf("counts from %d to %d", first, last)
	}
}

// Thin wind delivers less per turbine, so the turbines give way to solar and
// the count in all rises.
func TestPowerSiteCountRisesAsWindThins(t *testing.T) {
	good := drawFacts(1500)
	thin := drawFacts(1500)
	thin.Catalog.Power.Wind.Profile = WindProfile(0.04, 0.4, 1)
	gw, gs := siteCounts(t, good)
	tw, ts := siteCounts(t, thin)
	if tw >= gw || gw == 0 {
		t.Errorf("turbines %d with good wind, %d with thin", gw, tw)
	}
	if ts <= gs || tw+ts < gw+gs {
		t.Errorf("good wind %d turbines %d solar; thin wind %d turbines %d solar", gw, gs, tw, ts)
	}
}

func TestPowerSiteCountUnknownSupplyDoesNotDefault(t *testing.T) {
	for name, edit := range map[string]func(*StationFacts){
		"no power rows": func(f *StationFacts) { f.Catalog.Power = PowerModel{} },
		"no wind row":   func(f *StationFacts) { f.Catalog.Power.Wind = SupplyRow{} },
		"no conduit":    func(f *StationFacts) { f.Catalog.Power.Conduit = 0 },
		"no steel":      func(f *StationFacts) { delete(f.Items.Market, siteSteelUnit) },
		"no lamp":       func(f *StationFacts) { f.Catalog.Light = LightModel{} },
	} {
		f := drawFacts(800)
		edit(&f)
		f.Derive = true
		if _, err := AdviseStations(f); !errors.Is(err, ErrStationFacts) {
			t.Errorf("%s: err %v", name, err)
		}
	}
}

// Two turbines' lanes overlap when sharing ground is the cheaper: the lane
// costs nothing where nothing blocks it.
func TestTurbineLanesOverlapWhenCheaper(t *testing.T) {
	in := siteInput(t, turbineNode("wind-1"), turbineNode("wind-2"))
	in.Price = SitePrice{Weights: DefaultSiteTypeWeights(), WindW: 2200, ConduitW: 500}
	got, err := EmbedSites(in)
	if err != nil {
		t.Fatal(err)
	}
	a, oka := got.Site("wind-1")
	b, okb := got.Site("wind-2")
	if !oka || !okb {
		t.Fatalf("placements %+v", got)
	}
	// Flush side by side the lanes touch; one cell closer they would overlap.
	if !RectRegion(pad(a.Lanes[0].Region.Bounds(), 1)).Overlaps(b.Lanes[0].Region) {
		t.Errorf("lanes %+v and %+v are apart", a.Lanes[0].Region.Bounds(), b.Lanes[0].Region.Bounds())
	}
	if a.Lanes[0].Blocked != 0 || b.Lanes[0].Blocked != 0 {
		t.Errorf("blocked %d, %d", a.Lanes[0].Blocked, b.Lanes[0].Blocked)
	}
}

// With the lane worth more than the conduit, a turbine stands where its lane is
// clear even when a nearer site would lose output: trees in the wind path (a
// thing that blocks wind) push it off the nearest ground.
func TestTurbineSeparatesWhenOutputLossIsHigher(t *testing.T) {
	survey := zoningSurvey(140, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	clear := func(price SitePrice, trees bool) (SitePlacement, bool) {
		s := survey
		s.Cells = append([]SurveyCell(nil), survey.Cells...)
		in := embedInput(t, s, 3)
		emb, err := Embed(in)
		if err != nil {
			t.Fatal(err)
		}
		if trees {
			// A wall of trees ringing the rooms would block any turbine near them.
			for i := range s.Cells {
				c := &s.Cells[i]
				for _, b := range emb.Blocks {
					if collides(Rectangle{X: c.Cell.X, Z: c.Cell.Z, Width: 1, Height: 1}, pad(b.Interior, 6)) && !collides(Rectangle{X: c.Cell.X, Z: c.Cell.Z, Width: 1, Height: 1}, pad(b.Interior, 3)) {
						c.Tree = true
					}
				}
			}
			in.Survey = s
		}
		sin := SiteEmbedInput{Embed: in, Embedding: emb, Nodes: []SiteNode{turbineNode("wind-1")}, Price: price}
		got, err := EmbedSites(sin)
		if err != nil {
			t.Fatal(err)
		}
		return got.Site("wind-1")
	}
	dear := SitePrice{Weights: DefaultSiteTypeWeights(), WindW: 2200, ConduitW: 6.5, WallW: 32}
	far, _ := clear(dear, true)
	cheap := dear
	cheap.WindW, cheap.ConduitW = 1, 5000
	near, _ := clear(cheap, true)
	if far.Lanes[0].Blocked != 0 {
		t.Errorf("a dear lane stands in the trees: %d blocked", far.Lanes[0].Blocked)
	}
	if near.Lanes[0].Blocked == 0 || near.Origin == far.Origin {
		t.Errorf("a cheap lane and a dear conduit keep the far site: %+v vs %+v", near.Origin, far.Origin)
	}
}

// A conduit run is priced by its cells: a dearer conduit brings the site nearer.
func TestSiteDistanceFollowsConduitPrice(t *testing.T) {
	dist := func(conduit float64) int32 {
		in := siteInput(t, rectNode("solar", 4, 4))
		in.Price = SitePrice{Weights: DefaultSiteTypeWeights(), ConduitW: conduit}
		got, err := EmbedSites(in)
		if err != nil {
			t.Fatal(err)
		}
		p, _ := got.Site("solar")
		best := int32(1 << 20)
		for _, b := range in.Embedding.Blocks {
			best = min(best, rectGap(p.Footprint.Bounds(), b.Interior))
		}
		return best
	}
	if near, far := dist(500), dist(0); near > far {
		t.Errorf("dear conduit sites %d cells away, free conduit %d", near, far)
	}
}

func rectGap(a, b Rectangle) int32 {
	dx := max(b.X-(a.X+a.Width), a.X-(b.X+b.Width), 0)
	dz := max(b.Z-(a.Z+a.Height), a.Z-(b.Z+b.Height), 0)
	return dx + dz
}

func TestTreesBlockTheWindPath(t *testing.T) {
	s := zoningSurvey(140, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Tree: x == 70 && z == 70} })
	g := newWindGround(SiteEmbedInput{Embed: EmbedInput{Survey: s}})
	lane := RectRegion(Rectangle{X: 68, Z: 68, Width: 5, Height: 5})
	if n := g.blocked(lane, Region{}); n != 1 {
		t.Errorf("blocked %d, want the one tree", n)
	}
	_ = domain.Cell{}
}

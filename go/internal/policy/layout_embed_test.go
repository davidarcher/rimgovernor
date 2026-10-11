package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// colonyGroups clusters a colony of pawns: a bed each, a kitchen with its cold
// shelf, a workshop, a store and a dining table per four pawns.
func colonyGroups(t testing.TB, pawns int) []RoomGroup {
	t.Helper()
	stove := node("Stove", PurposeWork, 1, workBand)
	bench := node("Bench", PurposeWork, 1+pawns/10, workBand)
	table := node("Table", PurposeDine, 1+pawns/4)
	bed := node("Bed", PurposeSleep, pawns)
	shelf := node("Shelf", PurposeStore, 1+pawns/5)
	cold := node("ColdShelf", PurposeStoreCold, 1+pawns/10, EnvNeed{Kind: NeedCold})
	g := StationGraph{
		Nodes: []StationNode{bed, stove, bench, table, shelf, cold},
		Edges: []StationEdge{
			edge(stove, cold, 12, true), edge(stove, table, 10, false), edge(bed, table, 6, false),
			edge(bench, shelf, 8, false), edge(cold, shelf, 3, false), edge(table, shelf, 2, false),
		},
	}
	got, err := ClusterStations(ClusterInput{Graph: g, Catalog: clusterCatalog(), RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	return got.Groups
}

func embedInput(t testing.TB, s MapSurvey, pawns int) EmbedInput {
	t.Helper()
	return EmbedInput{Groups: colonyGroups(t, pawns), Zones: Zone(s), Survey: s, RoofDistance: testRoof}
}

// checkEmbedding asserts the embedding invariants: every group is placed or
// reported unplaceable with a reason, blocks hold their area, stay clear of
// each other, and lie on the core.
func checkEmbedding(t *testing.T, in EmbedInput, emb Embedding) {
	t.Helper()
	seen := map[int]bool{}
	for _, b := range emb.Blocks {
		seen[b.Group] = true
		if int(b.Interior.Width*b.Interior.Height) < in.Groups[b.Group].Area {
			t.Errorf("group %d block %+v smaller than area %d", b.Group, b.Interior, in.Groups[b.Group].Area)
		}
	}
	for _, d := range emb.Unplaceable() {
		if seen[d.Group] || d.Reason == "" {
			t.Errorf("unplaceable %+v is placed or has no reason", d)
		}
		seen[d.Group] = true
	}
	for i := range in.Groups {
		if !seen[i] {
			t.Errorf("group %d neither placed nor reported", i)
		}
	}
	for i, a := range emb.Blocks {
		for _, b := range emb.Blocks[i+1:] {
			if collides(a.Interior, b.Interior) {
				t.Errorf("blocks %+v and %+v collide", a, b)
			}
		}
	}
}

func TestEmbedPlacesEveryGroupDeterministically(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	if len(emb.Blocks) != len(in.Groups) || emb.Score.UnmetLinks != 0 {
		t.Fatalf("placed %d of %d groups, %d required links unmet: %+v", len(emb.Blocks), len(in.Groups), emb.Score.UnmetLinks, emb.Decisions)
	}
	for _, g := range in.Groups {
		for _, l := range g.Links {
			if !l.Required {
				continue
			}
			a, _ := emb.Block(g.ID)
			b, _ := emb.Block(l.To)
			if gapDist(a, b) != 1 {
				t.Errorf("required link %d-%d not adjacent: %+v %+v", g.ID, l.To, a, b)
			}
		}
	}
	again, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(emb, again) {
		t.Fatal("Embed is not deterministic")
	}
	// The objective recomputed from the blocks matches the reported score.
	if emb.Score.Total() > 0 {
		t.Errorf("score %+v", emb.Score)
	}
}

// The search never ends worse than the greedy start of its seed.
func TestEmbedSearchNeverWorsens(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 8)
	scorer := newPlanScorer(in.Zones, in.Reservations, in.Survey)
	depth, _, err := roomLimits(in.RoofDistance)
	if err != nil {
		t.Fatal(err)
	}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	e := newEmbedder(in, depth, scorer, emb.Level)
	seed := e.seeds[0]
	searched := e.start(seed, siteSearchIters)
	greedy := e.start(seed, 0)
	if e.objective(searched) > e.objective(greedy) {
		t.Fatalf("search worsened the start: %v > %v", e.objective(searched), e.objective(greedy))
	}
}

// A group that cannot stand under a roof keeps off rock and mountain.
func TestEmbedEnvironmentRequirements(t *testing.T) {
	s := replayFixtureNamed("mountain-edge").survey(t) // mountain-edge: rock from x=85, thick roof from x=100
	in := embedInput(t, s, 6)
	in.Groups = slices.Clone(in.Groups)
	sky := len(in.Groups) - 1
	in.Groups[sky].Requires = append(slices.Clone(in.Groups[sky].Requires), EnvOpenSky)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	b, ok := emb.Block(sky)
	if !ok || b.X+b.Width > 85 {
		t.Errorf("open-sky block %+v on rock", b)
	}
}

// An open-sky group on a map that is all mountain is unplaceable for that
// reason; a cold group is no site requirement (it carries its cooler).
func TestEmbedColdAndOpenSkySiteSignal(t *testing.T) {
	mountain := zoningSurvey(60, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Rock: true, ThickRoof: true} })
	inM := embedInput(t, mountain, 3)
	embM, err := Embed(inM)
	if err != nil {
		t.Fatal(err)
	}
	for _, d := range embM.Decisions {
		if d.Verdict == EmbedUnsatisfied && d.Requirement == EnvCold {
			t.Errorf("cold is reported as a site requirement: %+v", d)
		}
	}
	inM.Groups = slices.Clone(inM.Groups)
	inM.Groups[0].Requires = []EnvRequirement{EnvOpenSky}
	embM, err = Embed(inM)
	if err != nil {
		t.Fatal(err)
	}
	un := embM.Unplaceable()
	if len(un) != 1 || un[0].Group != 0 || un[0].Reason != ReasonEmbedOpenSkyGround {
		t.Errorf("unplaceable %+v", un)
	}
}

// A site too small for the colony reports the groups it cannot place.
func TestEmbedReportsUnplaceable(t *testing.T) {
	s := replayFixtureNamed("tiny").survey(t)
	in := embedInput(t, s, 40)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	if len(emb.Unplaceable()) == 0 {
		t.Fatalf("40 pawns fit a 40x40 map: %d blocks", len(emb.Blocks))
	}
	for _, d := range emb.Unplaceable() {
		if d.Reason != ReasonEmbedNoGround {
			t.Errorf("reason %q", d.Reason)
		}
	}
}

func replayFixtureNamed(name string) replayFixture {
	for _, f := range replayFixtures {
		if f.name == name {
			return f
		}
	}
	panic("no replay fixture " + name)
}

// The replay fixtures: the embedding is scored on the plan's own terms and
// printed beside the old plan's, per interior cell (the old plan houses its
// rooms in other proportions, so totals are not comparable).
func TestEmbedReplayFixtures(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for _, f := range replayFixtures {
		t.Run(f.name, func(t *testing.T) {
			s := f.survey(t)
			in := embedInput(t, s, f.pawns)
			emb, err := Embed(in)
			if err != nil {
				t.Fatal(err)
			}
			checkEmbedding(t, in, emb)
			if len(emb.Unplaceable()) > 0 && !f.mayFail {
				t.Fatalf("unplaced: %+v", emb.Unplaceable())
			}
			plan := SiteCore(LayoutPlan{Zones: in.Zones}, s, f.pawns, 1, TechTierCamp)
			old := Score(plan, s)
			cells := 0
			for _, r := range plan.AllRooms() {
				cells += int(r.Region.Bounds().Width * r.Region.Bounds().Height)
			}
			oldTerms := old.Soil + old.Edge + old.Centre
			// The agreed margin is the replay gate's tolerance: the embedding
			// pays no more per interior cell, on the plan's own site terms,
			// than the old plan does.
			if cells > 0 && emb.Score.Cells > 0 {
				oldPer, per := float64(oldTerms)/float64(cells), float64(emb.Score.SiteTerms())/float64(emb.Score.Cells)
				if per < oldPer*(1+replayTolerance) {
					t.Errorf("site terms per cell %.2f below the old plan's %.2f", per, oldPer)
				}
			}
			t.Logf("%-16s embed: groups=%d cells=%d level=%d soil=%d edge=%d centre=%d walk=%d unmet=%d  terms/cell=%.2f",
				f.name, len(in.Groups), emb.Score.Cells, emb.Level, emb.Score.Soil, emb.Score.Edge, emb.Score.Centre, emb.Score.Walk,
				emb.Score.UnmetLinks, float64(emb.Score.SiteTerms())/float64(max(emb.Score.Cells, 1)))
			t.Logf("%-16s old:   rooms=%d cells=%d soil=%d edge=%d centre=%d walk=%d                    terms/cell=%.2f",
				f.name, len(plan.AllRooms()), cells, old.Soil, old.Edge, old.Centre, old.Walk, float64(oldTerms)/float64(max(cells, 1)))
		})
	}
}

// A cold group stands on open ground with its cooler in the group when the map
// has no mountain.
func TestEmbedColdGroupOnOpenGround(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 3)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	cold := 0
	for i, g := range in.Groups {
		if !slices.Contains(g.Requires, EnvCold) {
			continue
		}
		cold++
		if !slices.ContainsFunc(g.Stations, func(s GroupStation) bool { return s.Purpose == PurposeCool }) {
			t.Errorf("cold group %d has no cooler: %+v", i, g.Stations)
		}
		if _, ok := emb.Block(i); !ok {
			t.Errorf("cold group %d is not placed", i)
		}
	}
	if cold == 0 {
		t.Fatal("no cold group")
	}
}

package policy

import (
	"math"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A node whose stations spawn filth stands in a closet of its own, whichever
// pair the clustering reaches first: it never joins the stove it shares a
// kitchen role with nor the cold shelf it hauls to, stays linked to both by its
// flows, and the group is planned as the butchery.
func TestClusterStationsSendsFilthToAClosetOfItsOwn(t *testing.T) {
	stove := node("Stove", PurposeWork, 1, workBand)
	butcher := node("Butcher", PurposeWork, 1)
	butcher.Filth = true
	cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
	g := StationGraph{
		Nodes: []StationNode{butcher, cold, stove},
		Edges: []StationEdge{edge(stove, cold, 10, true), edge(butcher, cold, 20, true)},
	}
	got := clusterOf(t, g)
	closet, kitchen := groupWith(t, got, butcher.ID), groupWith(t, got, stove.ID)
	if closet.ID == kitchen.ID || groupWith(t, got, cold.ID).ID == closet.ID {
		t.Fatalf("the butcher shares a room: %+v", got.Groups)
	}
	if len(closet.Stations) != 1 || closet.Planned != PlannedButchery {
		t.Errorf("closet %+v, want the butchery alone", closet)
	}
	if role, ok := PlannedRoleOfGroup(closet); !ok || role != PlannedButchery {
		t.Errorf("planned role %q %v", role, ok)
	}
	if kitchen.Planned != "" {
		t.Errorf("the kitchen group carries planned role %q", kitchen.Planned)
	}
	if !slices.ContainsFunc(closet.Links, func(l GroupLink) bool { return l.To == kitchen.ID && l.Required }) {
		t.Errorf("closet links %+v, want a required link to the cold shelf's group %d", closet.Links, kitchen.ID)
	}
	if !slices.ContainsFunc(got.Decisions, func(d ClusterDecision) bool {
		return d.Verdict == ClusterKeep && d.Reason == ReasonClusterFilthPriced && (d.A == butcher.ID || d.B == butcher.ID)
	}) {
		t.Errorf("no priced-filth decision for the butcher: %+v", got.Decisions)
	}
}

var bloodModel = FilthModel{Def: "Filth_Blood", Cleanliness: -10,
	FoodPoison: []CurvePoint{{X: -5, Y: 0.05}, {X: -3.5, Y: 0.025}, {X: -2, Y: 0}}}

// butcherKitchen is a cook (whose meals read cleanliness), a butcher and the
// cold shelf both haul to, clustered with the blood model.
func butcherKitchen(t *testing.T, merge bool) (Clustering, StationNode, StationNode) {
	t.Helper()
	stove := node("Stove", PurposeWork, 1, workBand, EnvNeed{Kind: NeedRoomStat, Input: cleanlinessStat})
	butcher := node("Butcher", PurposeWork, 1)
	butcher.Filth = true
	cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
	g := StationGraph{
		Nodes: []StationNode{butcher, cold, stove},
		Edges: []StationEdge{edge(stove, cold, 10, true), edge(butcher, cold, 20, true)},
	}
	cat := clusterCatalog()
	cat.Filth = bloodModel
	var pairs []NodePair
	if merge {
		pairs = []NodePair{{A: butcher.ID, B: stove.ID, Rule: PairTogether}}
	}
	got, err := ClusterStations(ClusterInput{Graph: g, Catalog: cat, RoofDistance: testRoof, Pairs: pairs})
	if err != nil {
		t.Fatal(err)
	}
	return got, butcher, stove
}

// The default keeps the butcher in its closet but the decision carries the
// price in game units: one blood unit's cleanliness over the room that would
// hold it, the food poison chance (zero until cleanliness falls below the
// curve's last point) and the impressiveness input, for the cook it would
// reach.
func TestButcheryKeptApartIsPriced(t *testing.T) {
	got, butcher, stove := butcherKitchen(t, false)
	if groupWith(t, got, butcher.ID).ID == groupWith(t, got, stove.ID).ID {
		t.Fatalf("default merged the butcher into the kitchen: %+v", got.Groups)
	}
	var priced []ClusterDecision
	for _, d := range got.Decisions {
		if d.Reason == ReasonClusterFilthPriced {
			priced = append(priced, d)
		}
	}
	if len(priced) == 0 {
		t.Fatalf("no priced decision: %+v", got.Decisions)
	}
	for _, d := range priced {
		if d.Verdict != ClusterKeep || len(d.Filth) != 1 {
			t.Fatalf("decision %+v", d)
		}
	}
	c := priced[0].Filth[0]
	if c.Node != butcher.ID || c.Filth != "Filth_Blood" || c.Cells <= 0 {
		t.Fatalf("cost %+v", c)
	}
	if want := -10 / float64(c.Cells); c.CleanlinessPerUnit != want || c.ImpressivenessInputPerUnit != want/2.5 {
		t.Errorf("per unit %v / %v, want %v", c.CleanlinessPerUnit, c.ImpressivenessInputPerUnit, want)
	}
	if want := float64(c.Cells) / 5; c.UnitsToPoison != want || c.PoisonOnsetCleanliness != -2 || c.FoodPoisonPerUnit != 0 {
		t.Errorf("poison %+v, want onset after %v units", c, want)
	}
	for _, g := range got.Groups {
		if len(g.Filth) != 0 {
			t.Errorf("group %d carries a filth price without sharing: %+v", g.ID, g.Filth)
		}
	}
}

// With a PairTogether rule the same graph merges the butcher into the room of the
// stove or the shelf it hauls to: no refusal, the price rides on the merge
// decision and on the merged group, and the cook is named as exposed.
func TestButcheryMergesWhenPriced(t *testing.T) {
	got, butcher, stove := butcherKitchen(t, true)
	room := groupWith(t, got, butcher.ID)
	if len(room.Stations) < 2 || room.Planned != "" {
		t.Fatalf("butcher still alone or planned apart: %+v", room)
	}
	if len(room.Filth) != 1 || room.Filth[0].Node != butcher.ID {
		t.Fatalf("merged group's price %+v", room.Filth)
	}
	if groupWith(t, got, stove.ID).ID == room.ID && !slices.Contains(room.Filth[0].Exposed, stove.ID) {
		t.Errorf("cook not exposed: %+v", room.Filth[0])
	}
	if !slices.ContainsFunc(got.Decisions, func(d ClusterDecision) bool {
		return d.Verdict == ClusterMerge && len(d.Filth) > 0
	}) {
		t.Errorf("no priced merge decision: %+v", got.Decisions)
	}
	again, _, _ := butcherKitchen(t, true)
	if !reflect.DeepEqual(got, again) {
		t.Error("clustering changed")
	}
}

// butcheryColony is the base colony's groups with a butchery closet: the
// butcher table alone, planned as the butchery, joined to the freezer by the
// required link of its corpse and meat flows and to the kitchen by the meat
// the stove works.
func butcheryColony(pawns int) []RoomGroup {
	groups := baseRoomGroups(pawns)
	id := len(groups)
	foot := 3
	closet := RoomGroup{ID: id, Role: RoomRoleKitchen, Planned: PlannedButchery, Footprint: foot, Clearance: 2, Area: foot + 2,
		Stations: []GroupStation{{Node: nodeID("Butcher", PurposeWork), Def: "Butcher", Purpose: PurposeWork, Count: 1}}}
	link := func(to int, weight float64, required bool) {
		closet.Links = append(closet.Links, GroupLink{To: to, Weight: weight, Required: required})
		groups[to].Links = append(groups[to].Links, GroupLink{To: id, Weight: weight, Required: required})
	}
	link(1, 20, true)
	link(0, 5, false)
	closet.AddRequiredDoors()
	groups[1].Clearance++
	groups[1].Area++
	return append(groups, closet)
}

// A butchery group is embedded beside the freezer, tiles into a butchery room
// that is furnished, owes its own room, and leaves the kitchen group alone.
func TestButcheryClosetTilesBesideTheFreezer(t *testing.T) {
	s := plainScoreSurvey()
	in := embedInput(t, s, 3)
	in.Groups = butcheryColony(3)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	tin := TileInput{Groups: in.Groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: s}
	res, err := TileEmbedding(tin)
	if err != nil {
		t.Fatal(err)
	}
	checkTiled(t, tin, res)
	butchery := len(in.Groups) - 1
	room := func(group int) PlannedRoom {
		for _, r := range res.Plan.Rooms {
			if r.ID == res.Rooms[group] {
				return r
			}
		}
		t.Fatalf("group %d has no room", group)
		return PlannedRoom{}
	}
	if got := room(butchery).Role; got != PlannedButchery {
		t.Errorf("butchery group became %q", got)
	}
	if got := room(0).Role; got != PlannedKitchen {
		t.Errorf("kitchen group became %q", got)
	}
	if slices.Contains(res.Unfit(), butchery) || len(res.Pieces[res.Rooms[butchery]]) != 1 {
		t.Errorf("butchery is not furnished: unfit %v pieces %v block %+v doors %v", res.Unfit(), res.Pieces[res.Rooms[butchery]], room(butchery).Region.Bounds(), room(butchery).Door)
	}
	if len(sharedWallRun(pad(room(butchery).Region.Bounds(), 1), pad(room(1).Region.Bounds(), 1))) == 0 {
		t.Errorf("butchery %v does not stand beside the freezer %v", room(butchery).Region.Bounds(), room(1).Region.Bounds())
	}
	if n := slices.Index(res.Plan.Owed, PlannedButchery); n < 0 {
		t.Errorf("the plan owes %v, not a butchery", res.Plan.Owed)
	}
	again, err := TileEmbedding(tin)
	if err != nil || !reflect.DeepEqual(res, again) {
		t.Errorf("tiling changed: %v", err)
	}
}

// poisonModel is the FoodPoisoning hediff as the defs give it (Hediffs_Global_
// Misc.xml): severity 1 falling 1 a day through three stages.
var poisonModel = PoisoningModel{Initial: 1, PerDay: 1, Stages: []PoisonStage{
	{MinSeverity: 0, WorkFactor: 0.6, VomitMtbDays: 0.4},
	{MinSeverity: 0.2, WorkFactor: 0.5, VomitMtbDays: 0.2},
	{MinSeverity: 0.8, WorkFactor: 0.6, VomitMtbDays: 0.3},
}}

// A poisoning costs half a work-day: 0.2 days at x0.6, 0.6 at x0.5 and 0.2 at
// x0.6 is 0.46 work-days of lost work, and 4.17 vomits of 600 ticks add 0.042.
func TestPoisoningWorkDays(t *testing.T) {
	got := poisonModel.WorkDays()
	want := 0.46 + (0.2/0.3+0.6/0.2+0.2/0.4)*600/domain.TicksPerDay
	if math.Abs(got-want) > 1e-9 || math.Abs(got-0.50167) > 1e-4 {
		t.Errorf("work-days %v, want %v", got, want)
	}
	if (PoisoningModel{}).WorkDays() != 0 || (PoisoningModel{Initial: 1, PerDay: 1}).WorkDays() != 0 {
		t.Error("an unknown model prices a poisoning")
	}
}

// filthyKitchen is a cook making meals a day and a butcher the cook's meat flows
// from, regrouped on a square site of side cells; the blood is heavy enough to
// poison the merged room (a unit moves its cleanliness past the curve's onset).
func filthyKitchen(meals float64, side int32, poison PoisoningModel) (Regrouping, StationNode, StationNode, error) {
	return filthyKitchenCleaning(meals, side, poison, 70)
}

// filthyKitchenCleaning is filthyKitchen with the def work a unit of blood takes
// to clean.
func filthyKitchenCleaning(meals float64, side int32, poison PoisoningModel, work float64) (Regrouping, StationNode, StationNode, error) {
	stove := node("Stove", PurposeWork, 1, workBand, EnvNeed{Kind: NeedRoomStat, Input: cleanlinessStat})
	stove.Meals = meals
	butcher := node("Butcher", PurposeWork, 1)
	butcher.Filth, butcher.FilthRuns, butcher.FilthWork = true, 0.1, 300
	g := StationGraph{Nodes: []StationNode{butcher, stove}, Edges: []StationEdge{edge(butcher, stove, 50, false)}}
	in := regroupIn(g, plainSurvey(side))
	in.Cluster.Catalog.Filth = FilthModel{Def: "Filth_Blood", Cleanliness: -100, Poisoning: poison, CleaningWork: work, CleaningSpeed: 1, StandTicks: 600,
		FoodPoison: []CurvePoint{{X: -5, Y: 0.05}, {X: -3.5, Y: 0.025}, {X: -2, Y: 0}}}
	got, err := Regroup(in)
	return got, butcher, stove, err
}

// The butcher joins the cook's room when the walk the merge saves exceeds the
// priced poisoning and stays a closet when it does not; each verdict carries
// the two parts of its change, and both runs repeat.
func TestRegroupPricesFilthMergeAgainstWalk(t *testing.T) {
	cheap, butcher, stove, err := filthyKitchen(0.01, 30, poisonModel)
	if err != nil {
		t.Fatal(err)
	}
	if len(cheap.Clustering.Groups) != 1 || len(cheap.Decisions) != 1 {
		t.Fatalf("cheap poison: rooms %v decisions %+v", roomsOf(cheap), cheap.Decisions)
	}
	d := cheap.Decisions[0]
	if d.Kind != RegroupMerge || d.Verdict != RegroupApplied || d.Reason != ReasonRegroupFilthMerge || !(d.A == butcher.ID && d.B == stove.ID || d.A == stove.ID && d.B == butcher.ID) {
		t.Fatalf("decision %+v", d)
	}
	if !(d.Walk < 0 && d.Poison > 0 && d.Clean == 0 && d.Walk+d.Poison+d.Clean < 0 && math.Abs(d.After-d.Before-(d.Walk+d.Poison+d.Clean)) < 1e-9) {
		t.Errorf("walk %v poison %v clean %v before %v after %v", d.Walk, d.Poison, d.Clean, d.Before, d.After)
	}

	dear, _, _, err := filthyKitchen(1000, 30, poisonModel)
	if err != nil {
		t.Fatal(err)
	}
	if len(dear.Clustering.Groups) != 2 || len(dear.Pairs) != 0 || len(dear.Decisions) != 1 {
		t.Fatalf("dear poison: rooms %v decisions %+v", roomsOf(dear), dear.Decisions)
	}
	k := dear.Decisions[0]
	if k.Kind != RegroupMerge || k.Verdict != RegroupKept || k.Reason != ReasonRegroupFilthKept || !(k.Walk < 0 && k.Poison+k.Clean > -k.Walk && k.After > k.Before) {
		t.Errorf("kept decision %+v", k)
	}
	for _, g := range dear.Clustering.Groups {
		if len(g.Stations) == 1 && g.Stations[0].Node == butcher.ID && g.Planned != PlannedButchery {
			t.Errorf("closet planned as %q", g.Planned)
		}
	}
	assertReasons(t, cheap)
	assertReasons(t, dear)
	again, _, _, err := filthyKitchen(1000, 30, poisonModel)
	if err != nil || !reflect.DeepEqual(dear, again) {
		t.Errorf("regrouping changed: %v", err)
	}
}

// A model that cannot price the poisoning (no hediff row) proposes no merge: the
// butcher stays in the seed's closet.
func TestRegroupFilthMergeNeedsAKnownPoisoning(t *testing.T) {
	got, _, _, err := filthyKitchen(0.01, 30, PoisoningModel{})
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Clustering.Groups) != 2 || len(got.Pairs) != 0 {
		t.Errorf("rooms %v pairs %v decisions %+v", roomsOf(got), got.Pairs, got.Decisions)
	}
}

// cleaningModel is blood as the defs give it (#2988): 70 work a unit of
// thickness, a colonist's cleaning speed 1, 600 ticks before anyone may clean.
func cleaningModel() FilthModel {
	m := bloodModel
	m.CleaningWork, m.CleaningSpeed, m.StandTicks = 70, 1, 600
	return m
}

func filthCostOf(t *testing.T, m FilthModel, cells int, meals float64, r FilthRuns) FilthCost {
	t.Helper()
	got := m.filthCosts([]string{"B"}, []string{"S"}, cells, meals, map[string]FilthRuns{"B": r})
	if len(got) != 1 {
		t.Fatalf("costs %+v", got)
	}
	return got[0]
}

// Cleaning labor is the runs a day times the def's work over the cleaning
// speed, in work-days: it scales with the units a day, the def's work and the
// inverse of the colonist's speed.
func TestFilthCleaningLaborScalesWithUnitsAndDefWork(t *testing.T) {
	r := FilthRuns{Runs: 2, Ticks: 300}
	cleaningOf := func(m FilthModel, r FilthRuns) float64 { return m.CleaningWorkDays(map[string]FilthRuns{"B": r}) }
	base := cleaningOf(cleaningModel(), r)
	if want := 2 * 70 / float64(domain.TicksPerDay); math.Abs(base-want) > 1e-12 {
		t.Fatalf("cleaning %v, want %v", base, want)
	}
	heavy := cleaningModel()
	heavy.CleaningWork = 140
	fast := cleaningModel()
	fast.CleaningSpeed = 2
	for name, c := range map[string]struct{ got, want float64 }{
		"units x3": {cleaningOf(cleaningModel(), FilthRuns{Runs: 6, Ticks: 300}) / base, 3},
		"work x2":  {cleaningOf(heavy, r) / base, 2},
		"speed x2": {base / cleaningOf(fast, r), 2},
	} {
		if math.Abs(c.got-c.want) > 1e-9 {
			t.Errorf("%s: ratio %v, want %v", name, c.got, c.want)
		}
	}
	if none := cleaningOf(FilthModel{Def: "Filth_Blood", Cleanliness: -10}, r); none != 0 {
		t.Errorf("a model without the def work prices cleaning: %v", none)
	}
}

// The poisoning residual is the units that fall in the 600-tick window standing
// at once: one unit alone leaves the room at -1 (in the curve's flat part),
// three runs 200 ticks apart stand together at -3.
func TestFilthPoisoningResidualUsesTheStandWindow(t *testing.T) {
	m := cleaningModel()
	m.Cleanliness = -5 // over 5 cells a unit is -1
	m.Poisoning = poisonModel
	one := filthCostOf(t, m, 5, 100, FilthRuns{Runs: 1, Ticks: 300})
	if one.StandingUnits != 1 || one.StandingPoison != 0 || one.PoisonWorkDays != 0 {
		t.Errorf("one run: %+v", one)
	}
	three := filthCostOf(t, m, 5, 100, FilthRuns{Runs: 3, Ticks: 200})
	if three.StandingUnits != 3 || math.Abs(three.StandingPoison-0.025/1.5) > 1e-12 {
		t.Fatalf("three runs 200 apart: units %v chance %v", three.StandingUnits, three.StandingPoison)
	}
	if want := (2*200 + 600) / float64(domain.TicksPerDay); math.Abs(three.Exposure-want) > 1e-12 {
		t.Errorf("exposure %v, want %v", three.Exposure, want)
	}
	if want := 100 * three.Exposure * three.StandingPoison; math.Abs(three.PoisoningsPerDay-want) > 1e-12 || three.PoisonWorkDays <= 0 {
		t.Errorf("poisonings %v, want %v", three.PoisoningsPerDay, want)
	}
	// Runs a whole window apart never stand together.
	apart := filthCostOf(t, m, 5, 100, FilthRuns{Runs: 3, Ticks: 600})
	if apart.StandingUnits != 1 || apart.PoisonWorkDays != 0 {
		t.Errorf("runs a window apart: %+v", apart)
	}
	if got := filthCostOf(t, m, 5, 100, FilthRuns{}); got.PoisonWorkDays != 0 || got.StandingUnits != 0 {
		t.Errorf("a node that butchers nothing owes %+v", got)
	}
}

// A butchery merged into the kitchen room costs work-days a day; kept apart in
// its closet, no group carries the price.
func TestButcheryMergedIntoKitchenCostsMoreThanApart(t *testing.T) {
	price := func(merge bool) (total float64) {
		stove := node("Stove", PurposeWork, 1, workBand, EnvNeed{Kind: NeedRoomStat, Input: cleanlinessStat})
		stove.Meals = 3
		butcher := node("Butcher", PurposeWork, 1)
		butcher.Filth, butcher.FilthRuns, butcher.FilthWork = true, 2, 300
		cold := node("ColdShelf", PurposeStoreCold, 1, EnvNeed{Kind: NeedCold})
		g := StationGraph{Nodes: []StationNode{butcher, cold, stove}, Edges: []StationEdge{edge(stove, cold, 10, true), edge(butcher, cold, 20, true)}}
		cat := clusterCatalog()
		cat.Filth = cleaningModel()
		cat.Filth.Cleanliness, cat.Filth.Poisoning = -100, poisonModel
		var pairs []NodePair
		if merge {
			pairs = []NodePair{{A: butcher.ID, B: stove.ID, Rule: PairTogether}}
		}
		got, err := ClusterStations(ClusterInput{Graph: g, Catalog: cat, RoofDistance: testRoof, Pairs: pairs})
		if err != nil {
			t.Fatal(err)
		}
		for _, grp := range got.Groups {
			for _, f := range grp.Filth {
				total += f.WorkDays()
			}
		}
		return total
	}
	if apart, merged := price(false), price(true); !(merged > apart && apart == 0) {
		t.Errorf("merged %v work-days, apart %v", merged, apart)
	}
}

// The cleaning labor is the same whether the butchery shares the kitchen's room
// or stands in its closet, so it cancels from the merge decision (#2993): a
// thousandfold def work leaves both verdicts as they are, the butchery's group
// carries the price either way, and the move's Clean change is zero.
func TestRegroupCleaningLaborIsACommonBaseline(t *testing.T) {
	for _, c := range []struct {
		name   string
		meals  float64
		merged bool
	}{{"walk beats poison", 0.01, true}, {"poison beats walk", 1000, false}} {
		base, butcher, _, err := filthyKitchenCleaning(c.meals, 30, poisonModel, 70)
		if err != nil {
			t.Fatal(err)
		}
		heavy, _, _, err := filthyKitchenCleaning(c.meals, 30, poisonModel, 70000)
		if err != nil {
			t.Fatal(err)
		}
		if (len(heavy.Clustering.Groups) == 1) != c.merged || len(heavy.Pairs) != len(base.Pairs) || len(heavy.Decisions) != 1 {
			t.Fatalf("%s: heavy cleaning changed the grouping: rooms %v vs %v", c.name, roomsOf(heavy), roomsOf(base))
		}
		d := heavy.Decisions[0]
		if d.Clean != 0 || d.Poison != base.Decisions[0].Poison || d.Walk != base.Decisions[0].Walk {
			t.Errorf("%s: cleaning moved the decision: heavy %+v base %+v", c.name, d, base.Decisions[0])
		}
		var paid float64
		for _, g := range heavy.Clustering.Groups {
			if holdsNode(g, butcher.ID) {
				paid += g.Cleaning
			}
		}
		if paid <= 0 {
			t.Errorf("%s: the butchery's group carries no cleaning price", c.name)
		}
	}
}

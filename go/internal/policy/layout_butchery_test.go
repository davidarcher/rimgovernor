package policy

import (
	"reflect"
	"slices"
	"testing"
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

// TestPlanCorePlansButcheryApartFromKitchen: the butchery is grown on demand and
// that does not crowd the kitchen.
func TestPlanCorePlansButcheryApartFromKitchen(t *testing.T) {
	p := corePlan(coreTestZones(), 0, TechTierCamp)
	p, grown, err := growDemandRooms(p, MapSurvey{}, nil, []PlannedRole{PlannedButchery})
	if err != nil || !grown {
		t.Fatalf("butchery grown=%v err=%v", grown, err)
	}
	var butchery, kitchen *PlannedRoom
	for i, r := range p.Rooms {
		switch r.Role {
		case PlannedButchery:
			butchery = &p.Rooms[i]
		case PlannedKitchen:
			kitchen = &p.Rooms[i]
		}
	}
	if butchery == nil || kitchen == nil {
		t.Fatalf("butchery %v kitchen %v", butchery, kitchen)
	}
	if overlapsRooms(*butchery, []PlannedRoom{*kitchen}) {
		t.Fatal("butchery crowds the kitchen")
	}
	t.Logf("butchery %+v link %v", butchery.Region.Bounds(), butchery.Link)
}

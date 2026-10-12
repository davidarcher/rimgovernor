package policy

import "testing"

func joyGraphInput(colonists int, sources ...JoySource) StationGraphInput {
	in := graphTestInput()
	in.Population.Colonists = colonists
	in.Catalog.Joy = JoyModel{Sources: sources}
	return in
}

// The demand is the awake fall of the need over the mean session: 0.0015 a
// 150-tick interval, 400 intervals a day, 16 awake hours of 24, against a
// 4000-tick session of rate 1 (0.36/2500 a tick = 0.576). A quarter of the
// chance is outdoors, so a quarter of the sessions are outings.
func TestRecreationDemandSizesSessionsFromTheNeedAndTheGivers(t *testing.T) {
	m := JoyModel{Sources: []JoySource{
		{Giver: "Walk", Weight: 1, Session: 4000, Outdoor: true},
		{Giver: "Relax", Weight: 3, Session: 4000},
	}}
	node, ok := m.RecreationDemand().Value()
	if !ok {
		t.Fatal("unknown demand")
	}
	if !near(node.NeedPerDay, 0.4) || !near(node.Session, 0.576) || !near(node.Sessions, 0.4/0.576) {
		t.Errorf("need %v session %v sessions %v", node.NeedPerDay, node.Session, node.Sessions)
	}
	if !near(node.OutdoorShare, 0.25) || !near(node.Outdoor, node.Sessions/4) || !near(node.Outdoor+node.Indoor, node.Sessions) {
		t.Errorf("split %+v", node)
	}
	// Richer sessions mean fewer of them; the chance-weighted mean is what counts.
	rich := JoyModel{Sources: []JoySource{{Giver: "Walk", Weight: 1, Session: 8000, Outdoor: true}, {Giver: "Relax", Weight: 1, Session: 4000}}}
	r, _ := rich.RecreationDemand().Value()
	if !near(r.Session, 0.864) || !near(r.OutdoorShare, 0.5) {
		t.Errorf("rich %+v", r)
	}
}

// A model with no giver that gives joy, or only unread numbers, is unknown and
// adds no node and no flow.
func TestRecreationDemandIsUnknownWithoutSources(t *testing.T) {
	for name, m := range map[string]JoyModel{
		"empty":      {},
		"no chance":  {Sources: []JoySource{{Giver: "Walk", Weight: 0, Session: 4000, Outdoor: true}}},
		"no session": {Sources: []JoySource{{Giver: "Walk", Weight: 1, Session: 0, Outdoor: true}}},
	} {
		if _, ok := m.RecreationDemand().Value(); ok {
			t.Errorf("%s: demand is known", name)
		}
	}
	base, err := BuildStationGraph(joyGraphInput(2))
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := base.Recreation.Value(); ok || base.Weight("Bed/sleep", OutsideNode) != 2 {
		t.Errorf("unknown joy changed the graph: %v", base.Weight("Bed/sleep", OutsideNode))
	}
}

// Each healthy sleeper's outdoor sessions are trips from the bed to the outside
// node, added to the day cycle's one a day, and scale with the colonists.
func TestJoyOutingsAreTripsToTheOutsideNode(t *testing.T) {
	walk := JoySource{Giver: "Walk", Weight: 1, Session: 4000, Outdoor: true}
	relax := JoySource{Giver: "Relax", Weight: 3, Session: 4000}
	g, err := BuildStationGraph(joyGraphInput(2, walk, relax))
	if err != nil {
		t.Fatal(err)
	}
	node, ok := g.Recreation.Value()
	if !ok {
		t.Fatal("no recreation node")
	}
	if want := 2 + 2*node.Outdoor; !near(g.Weight("Bed/sleep", OutsideNode), want) {
		t.Errorf("bed to outside %v, want the day cycle's 2 plus 2 colonists' outings %v", g.Weight("Bed/sleep", OutsideNode), want)
	}
	g4, _ := BuildStationGraph(joyGraphInput(4, walk, relax))
	if !near(g4.Weight("Bed/sleep", OutsideNode)-4, 2*(g.Weight("Bed/sleep", OutsideNode)-2)) {
		t.Error("twice the colonists is not twice the outings")
	}
	indoors, _ := BuildStationGraph(joyGraphInput(2, relax))
	if !near(indoors.Weight("Bed/sleep", OutsideNode), 2) {
		t.Errorf("a colony with only indoor givers walks out %v, want the day cycle's 2", indoors.Weight("Bed/sleep", OutsideNode))
	}
}

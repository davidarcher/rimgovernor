package bridge

import (
	"testing"
)

// TestJoyModelFromRecordedCatalog reads the building-less joy givers of the
// recorded game (#2997): the outdoor ones are the def-flagged ones and the walk,
// and the resulting demand is a few sessions a day at most.
func TestJoyModelFromRecordedCatalog(t *testing.T) {
	m, err := fullCatalog(t).joyModel()
	if err != nil {
		t.Fatal(err)
	}
	outdoor := map[string]bool{}
	for _, s := range m.Sources {
		if s.Weight <= 0 || s.Session <= 0 {
			t.Errorf("source %+v holds an unread number", s)
		}
		outdoor[s.Giver] = s.Outdoor
	}
	for _, giver := range []string{"GoForWalk", "Skygaze", "BuildSnowman"} {
		if !outdoor[giver] {
			t.Errorf("%s is not outdoors: %+v", giver, m.Sources)
		}
	}
	for _, giver := range []string{"SocialRelax", "Meditate"} {
		if out, found := outdoor[giver]; !found || out {
			t.Errorf("%s outdoors %v, found %v", giver, out, found)
		}
	}
	if _, built := outdoor["Play_Chess"]; built {
		t.Error("a building-backed giver is in the building-less pool")
	}
	node, ok := m.RecreationDemand().Value()
	if !ok || node.Sessions <= 0.1 || node.Sessions > 3 || node.OutdoorShare <= 0 || node.OutdoorShare >= 1 {
		t.Errorf("demand = %+v", node)
	}
	t.Logf("joy: %+v", node)
}

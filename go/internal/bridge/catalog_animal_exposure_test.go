package bridge

import "testing"

func TestAnimalRaceComfort(t *testing.T) {
	reply := racesReply(t)
	setStats(reply.ThingDefs[0], StatComfyTemperatureMin, -30, StatComfyTemperatureMax, 45)
	catalog, err := DecodeDefinitionCatalog(reply, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	wolf, _ := races.Race("Wolf")
	if got, known := wolf.Comfort.Value(); !known || got.Min != -30 || got.Max != 45 {
		t.Fatalf("wolf comfort %+v %v", got, known)
	}
	beaver, _ := races.Race("Alphabeaver")
	// The game shows the comfort stats of every animal; one that sets none reads
	// the stats' own default base values.
	if got, known := beaver.Comfort.Value(); !known || got.Min != 0 || got.Max != 40 {
		t.Fatalf("beaver comfort %+v %v", got, known)
	}

	reply = racesReply(t)
	setStats(reply.ThingDefs[0], StatComfyTemperatureMin, 50, StatComfyTemperatureMax, 45)
	if catalog, err = DecodeDefinitionCatalog(reply, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, err = catalog.AnimalRaces(); err == nil {
		t.Fatal("an inverted range was accepted")
	}
}

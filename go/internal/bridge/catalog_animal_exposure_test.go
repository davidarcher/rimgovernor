package bridge

import "testing"

func TestAnimalRaceComfort(t *testing.T) {
	reply := racesReply()
	reply.StatValues.Stats = append(reply.StatValues.Stats, StatComfyTemperatureMin, StatComfyTemperatureMax)
	reply.StatValues.Rows[0].Stat = append(reply.StatValues.Rows[0].Stat, 4, 5)
	reply.StatValues.Rows[0].Value = append(reply.StatValues.Rows[0].Value, -30, 45)
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
	if _, known := beaver.Comfort.Value(); known {
		t.Fatal("a race without the stats got a range")
	}

	reply = racesReply()
	reply.StatValues.Stats = append(reply.StatValues.Stats, StatComfyTemperatureMin, StatComfyTemperatureMax)
	reply.StatValues.Rows[0].Stat = append(reply.StatValues.Rows[0].Stat, 4, 5)
	reply.StatValues.Rows[0].Value = append(reply.StatValues.Rows[0].Value, 50, 45)
	if catalog, err = DecodeDefinitionCatalog(reply, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, err = catalog.AnimalRaces(); err == nil {
		t.Fatal("an inverted range was accepted")
	}
}

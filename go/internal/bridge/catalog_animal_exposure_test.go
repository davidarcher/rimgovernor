package bridge

import "testing"

func TestAnimalComfort(t *testing.T) {
	reply := racesReply()
	reply.StatValues.Stats = append(reply.StatValues.Stats, StatComfyTemperatureMin, StatComfyTemperatureMax)
	reply.StatValues.Rows[0].Stat = append(reply.StatValues.Rows[0].Stat, 4, 5)
	reply.StatValues.Rows[0].Value = append(reply.StatValues.Rows[0].Value, -30, 45)
	catalog, err := DecodeDefinitionCatalog(reply, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	got, err := catalog.AnimalComfort("Wolf")
	if err != nil || got.Min != -30 || got.Max != 45 {
		t.Fatalf("wolf comfort %+v, %v", got, err)
	}
	if _, err := catalog.AnimalComfort("Alphabeaver"); err == nil {
		t.Fatal("a race without the stat was accepted")
	}
	if _, err := catalog.AnimalComfort("Unlisted"); err == nil {
		t.Fatal("a race without a row was accepted")
	}
	var none *DefinitionCatalog
	if _, err := none.AnimalComfort("Wolf"); err == nil {
		t.Fatal("no catalog was accepted")
	}
}

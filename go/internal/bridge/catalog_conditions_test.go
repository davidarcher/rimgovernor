package bridge

import "testing"

// TestWeatherAndConditionRows (#1723): the weather accuracy and the power
// outage come from the def rows, and a def with no row is an error.
func TestWeatherAndConditionRows(t *testing.T) {
	catalog := FixtureCatalog("load")
	if got, err := catalog.WeatherAccuracy("FoggyRain"); err != nil || got != 0.5 {
		t.Fatalf("FoggyRain accuracy = %v, %v", got, err)
	}
	if got, err := catalog.WeatherAccuracy("Clear"); err != nil || got != 1 {
		t.Fatalf("Clear accuracy = %v, %v", got, err)
	}
	if _, err := catalog.WeatherAccuracy("ModdedWeather"); err == nil {
		t.Fatal("a weather with no row was accepted")
	}
	if got, err := catalog.DisablesElectricity("SolarFlare"); err != nil || !got {
		t.Fatalf("SolarFlare disables electricity = %v, %v", got, err)
	}
	if got, err := catalog.DisablesElectricity("ColdSnap"); err != nil || got {
		t.Fatalf("ColdSnap disables electricity = %v, %v", got, err)
	}
	if _, err := catalog.DisablesElectricity("ModdedStorm"); err == nil {
		t.Fatal("a condition with no row was accepted")
	}
	var none *DefinitionCatalog
	if _, err := none.WeatherAccuracy("Rain"); err == nil {
		t.Fatal("no catalog was accepted")
	}
}

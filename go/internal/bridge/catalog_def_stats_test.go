package bridge

import "testing"

// Each rule that replaced a native copy (#2653) is held to the value native
// computed for the vanilla defs.
func TestDefStatRulesMatchTheNativeValues(t *testing.T) {
	catalog := fullCatalog(t)

	for def, want := range map[string]int64{"WoodLog": 75, "Steel": 75, "Silver": 500, "MealSimple": 10} {
		if got, err := catalog.StackLimit(def); err != nil || got != want {
			t.Errorf("StackLimit(%s) = %d, %v; native read %d", def, got, err, want)
		}
	}

	for def, want := range map[string]bool{"Turret_Mortar": true, "Turret_MiniTurret": false, "Hive": false, "Wall": false} {
		if got, err := catalog.IsMortar(def); err != nil || got != want {
			t.Errorf("IsMortar(%s) = %v, %v; native read %v", def, got, err, want)
		}
	}

	for _, c := range []struct {
		def     string
		medical bool
		want    int
	}{
		{"Cooler", false, 0}, {"Heater", false, 0}, {"SolarGenerator", false, 0}, {"WoodFiredGenerator", false, 0},
		{"Bed", true, 0}, {"Bed", false, 1}, {"TableButcher", false, 1}, {"Wall", false, 1}, {"StandingLamp", false, 2},
	} {
		if got, err := catalog.RepairPriority(c.def, c.medical); err != nil || got != c.want {
			t.Errorf("RepairPriority(%s, medical %v) = %d, %v; native read %d", c.def, c.medical, got, err, c.want)
		}
	}

	if radius, ok, err := catalog.GlowRadius("StandingLamp"); err != nil || !ok || radius != 12 {
		t.Errorf("GlowRadius(StandingLamp) = %v, %v, %v; native read 12", radius, ok, err)
	}
	if _, ok, err := catalog.GlowRadius("Wall"); err != nil || ok {
		t.Errorf("a wall has no glower: %v, %v", ok, err)
	}

	if human, err := catalog.BedHumanlike("Bed"); err != nil || !human {
		t.Errorf("BedHumanlike(Bed) = %v, %v", human, err)
	}
	if rest, err := catalog.PlannerStatValue("Bed", "", StatBedRestEffectiveness); err != nil || rest <= 0 {
		t.Errorf("a bed rests: %v, %v", rest, err)
	}
	if flammable, err := catalog.PlannerStatValue("Wall", "BlocksGranite", StatFlammability); err != nil || flammable != 0 {
		t.Errorf("granite wall flammability = %v, %v; native read 0", flammable, err)
	}
	if flammable, err := catalog.PlannerStatValue("Wall", "WoodLog", StatFlammability); err != nil || flammable <= 0 {
		t.Errorf("a wooden wall burns: %v, %v", flammable, err)
	}

	if food, known, err := catalog.TradeFood("MealSimple"); err != nil || !known || float32(food.Nutrition) != 0.9 {
		t.Errorf("TradeFood(MealSimple) nutrition = %v, %v, %v; native read 0.9", food.Nutrition, known, err)
	}

	materials, err := catalog.StuffMaterials("Wall", "Stony")
	if err != nil || len(materials) == 0 {
		t.Fatalf("a wall takes stony stuffs: %v, %v", materials, err)
	}
	for _, m := range materials {
		if len(m.Costs) != 1 || m.Costs[0].Units != 5 {
			t.Errorf("stony wall material %s costs %v; native read 5 units", m.Stuff, m.Costs)
		}
	}
}

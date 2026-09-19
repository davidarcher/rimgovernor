package main

import "testing"

// A resource target dispatches production bills without the cooking bill
// family, so it needs the bill executor too (#4 M2: the admitted club bill
// looped on "missing or unsupported building action" under
// sleeping,...,resource,workshop,gear).
func TestBillExecutorRequiredByResourceTargets(t *testing.T) {
	t.Parallel()
	var none serveConfig
	if billExecutorRequired(none) {
		t.Fatal("bill executor required with nothing composed")
	}
	var bills serveConfig
	bills.routineBillPlans = true
	if !billExecutorRequired(bills) {
		t.Fatal("bill family does not require the bill executor")
	}
	var targets serveConfig
	targets.routineResourcePlans = true
	if err := targets.routineResourceTargets.Set("MeleeWeapon_Club:3"); err != nil {
		t.Fatal(err)
	}
	if !billExecutorRequired(targets) {
		t.Fatal("resource target does not require the bill executor")
	}
	var gear serveConfig
	gear.routineGearPlans = true
	if !billExecutorRequired(gear) {
		t.Fatal("gear family does not require the bill executor")
	}
	var stone serveConfig
	stone.routineResourcePlans, stone.routineStoneBlockTarget = true, 60
	if !billExecutorRequired(stone) {
		t.Fatal("stone block target does not require the bill executor")
	}
}

// The resource and animal-feed families place stockpile zones on their
// storage fallbacks, so each needs the zone executor on its own (#311: the
// feed zone looped on "missing or unsupported building action" under
// animal-feed,resource,bill,work).
func TestZoneExecutorRequiredByStorageFallbacks(t *testing.T) {
	t.Parallel()
	var none serveConfig
	if zoneExecutorRequired(none) {
		t.Fatal("zone executor required with nothing composed")
	}
	for name, set := range map[string]func(*serveConfig){
		"field":           func(c *serveConfig) { c.routineFieldPlans = true },
		"food-storage":    func(c *serveConfig) { c.routineFoodStoragePlans = true },
		"secure-supplies": func(c *serveConfig) { c.routineSecureSuppliesPlans = true },
		"resource":        func(c *serveConfig) { c.routineResourcePlans = true },
		"animal-feed":     func(c *serveConfig) { c.routineAnimalFeedPlans = true },
	} {
		var c serveConfig
		set(&c)
		if !zoneExecutorRequired(c) {
			t.Fatalf("%s family does not require the zone executor", name)
		}
	}
}

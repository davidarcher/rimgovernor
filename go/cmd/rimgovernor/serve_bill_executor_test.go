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

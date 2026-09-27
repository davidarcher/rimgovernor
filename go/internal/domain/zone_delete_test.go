package domain

import "testing"

func TestZoneDeleteIdentity(t *testing.T) {
	if _, err := NewZoneDelete(""); err == nil {
		t.Fatal("expected empty zone to be rejected")
	}
	c, err := NewZoneDelete("Zone_7")
	if err != nil || c.Zone() != "Zone_7" {
		t.Fatal(c, err)
	}
	action, err := NewZoneDeleteAction("a", c)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := action.ZoneDelete(); !ok || got != c || action.Kind() != ZoneDeleteAction {
		t.Fatal("zone delete action does not carry its value")
	}
	if _, err := NewZoneDeleteAction("a", ZoneDelete{}); err == nil {
		t.Fatal("expected an empty zone to be rejected")
	}
	plan, err := NewPlan("p", 1, []Action{action})
	if err != nil || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
}

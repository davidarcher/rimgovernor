package solo

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func pawn(id string, dead bool, hypothermia float64) map[string]any {
	return map[string]any{
		"pawn": map[string]any{"id": id}, "dead": dead,
		"health": map[string]any{"hediffs": []any{map[string]any{"definition": map[string]any{"defName": "Hypothermia"}, "severity": hypothermia}}},
	}
}

func TestCasesAreRegisteredAndLint(t *testing.T) {
	for _, name := range []string{"solo/sea-ice-stock", "solo/sea-ice-rhadamant"} {
		c, ok := cases.Lookup(name)
		if !ok {
			t.Fatalf("%s is not registered", name)
		}
		if err := c.Lint(); err != nil {
			t.Errorf("%s: %v", name, err)
		}
	}
}

func TestAuditColonists(t *testing.T) {
	for _, tc := range []struct {
		name string
		rows []map[string]any
		want string
	}{
		{"alive and warm", []map[string]any{pawn("a", false, 0.1)}, ""},
		{"at the cap", []map[string]any{pawn("a", false, hypothermiaCap)}, ""},
		{"past the cap", []map[string]any{pawn("a", false, 0.4)}, "Hypothermia"},
		{"dead", []map[string]any{pawn("a", true, 0)}, "dead or gone"},
		{"missing", nil, "dead or gone"},
	} {
		err := AuditColonists(tc.rows, []string{"a"}, map[string]any{})
		if tc.want == "" && err != nil || tc.want != "" && (err == nil || !strings.Contains(err.Error(), tc.want)) {
			t.Errorf("%s: err = %v, want %q", tc.name, err, tc.want)
		}
	}
}

func TestAuditSleepingBand(t *testing.T) {
	p := policy.DefaultRoundsPolicy()
	if err := auditSleepingBand(15, true, 22, true, p); err != nil {
		t.Errorf("in band: %v", err)
	}
	if auditSleepingBand(5, true, 22, true, p) == nil {
		t.Error("a cold shelter passed")
	}
	if auditSleepingBand(15, true, 40, true, p) == nil {
		t.Error("a hot shelter passed")
	}
	if auditSleepingBand(0, false, 22, true, p) == nil {
		t.Error("an unknown temperature passed")
	}
}

func TestAuditPower(t *testing.T) {
	if err := auditPower(true, true, 100, true, false, true); err != nil {
		t.Errorf("powered: %v", err)
	}
	for name, err := range map[string]error{
		"no consumer": auditPower(false, true, 0, true, false, true),
		"unpowered":   auditPower(true, true, 100, true, true, true),
		"deficit":     auditPower(true, true, -5, true, false, true),
		"unknown":     auditPower(true, true, 0, false, false, true),
	} {
		if err == nil {
			t.Errorf("%s passed", name)
		}
	}
}

func TestAuditCensus(t *testing.T) {
	zero, two := 0.0, 2.0
	reached := &sustainedfood.TickWindow{Reached: true}
	if err := auditCensus(sustainedfood.ColonyOutcome{MinFoodRunwayDays: &two}, reached, 10); err != nil {
		t.Errorf("healthy segment: %v", err)
	}
	for name, err := range map[string]error{
		"short window": auditCensus(sustainedfood.ColonyOutcome{}, &sustainedfood.TickWindow{}, 10),
		"lost":         auditCensus(sustainedfood.ColonyOutcome{ColonistsLost: 1}, reached, 10),
		"empty larder": auditCensus(sustainedfood.ColonyOutcome{MinFoodRunwayDays: &zero}, reached, 10),
	} {
		if err == nil {
			t.Errorf("%s passed", name)
		}
	}
}

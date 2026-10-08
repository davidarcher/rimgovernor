package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A shortage alone makes a resource short: a derived steel need with no steel
// in stock puts steel-yielding ruins in tier 2.
func TestRecoveryShortFromDerivedSteelShortage(t *testing.T) {
	p := RoundsPolicy{}
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 0}}), ResourceNeeds: map[Resource]int64{"Steel": 200}}
	short, err := recoveryShortResources(p, f)
	if err != nil || !short["Steel"] {
		t.Fatal(short, err)
	}
}

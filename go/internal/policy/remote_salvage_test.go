package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A shortage alone makes a resource short: the default steel floor (#875)
// with no steel in stock puts steel-yielding ruins in tier 2 without an
// operator target.
func TestRecoveryShortFromDefaultSteelShortage(t *testing.T) {
	p := RoundsPolicy{ResourceTargets: DefaultResourceTargets()}
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 0}})}
	short, err := recoveryShortResources(p, f)
	if err != nil || !short["Steel"] {
		t.Fatal(short, err)
	}
}

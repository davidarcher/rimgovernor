package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The ritual starts only for an accepted ceremony whose bestower is known to
// wait and whose ritual is known not to have started.
func TestCeremonyStartEmitCondition(t *testing.T) {
	c, ok := CeremonyStart(pendingCeremony(nil))
	if !ok || c.Pawn != "Alice" || c.Bestower != "Envoy" {
		t.Fatalf("%+v %v", c, ok)
	}
	if c, ok := CeremonyStartOf(domain.Known(pendingCeremony(nil))); !ok || c.Pawn != "Alice" {
		t.Fatalf("%+v %v", c, ok)
	}
	for name, edit := range map[string]func(*BestowingCeremony){
		"offered":          func(c *BestowingCeremony) { c.Accepted = domain.Known(false) },
		"accept unread":    func(c *BestowingCeremony) { c.Accepted = domain.Unknown[bool]() },
		"bestower walking": func(c *BestowingCeremony) { c.BestowerWaiting = domain.Known(false) },
		"waiting unread":   func(c *BestowingCeremony) { c.BestowerWaiting = domain.Unknown[bool]() },
		"started":          func(c *BestowingCeremony) { c.Started = domain.Known(true) },
		"started unread":   func(c *BestowingCeremony) { c.Started = domain.Unknown[bool]() },
	} {
		if c, ok := CeremonyStart(pendingCeremony(edit)); ok {
			t.Errorf("%s: %+v", name, c)
		}
	}
	if _, ok := CeremonyStartOf(domain.Unknown[RoyaltyFacts]()); ok {
		t.Fatal("unknown royalty starts nothing")
	}
	// Two waiting ceremonies: the lowest colonist first.
	f := pendingCeremony(nil)
	second := f.Ceremonies[0]
	second.Pawn, second.Quest = "Aaron", "Quest_5"
	f.Ceremonies = append(f.Ceremonies, second)
	if c, ok := CeremonyStart(f); !ok || c.Pawn != "Aaron" {
		t.Fatalf("%+v %v", c, ok)
	}
}

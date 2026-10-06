package domain

import "testing"

// The incinerator takes everything but what the native burnable rule refuses:
// rotten colonist and slave corpses, serviceable gear and the like.
func TestIncineratorFilterRefusesTheNotBurnableSpecial(t *testing.T) {
	f := IncineratorFilter()
	if f.Base() != BaseEverything {
		t.Fatal(f.Base())
	}
	if len(f.Allow()) != 0 || len(f.Disallow()) != 1 || f.Disallow()[0] != SpecialFilter(NotBurnableFilterDef) {
		t.Fatal(f.Allow(), f.Disallow())
	}
	if _, _, ok := f.HitPoints(); ok {
		t.Fatal("the burnable rule, not a hit-point bound, judges gear")
	}
}

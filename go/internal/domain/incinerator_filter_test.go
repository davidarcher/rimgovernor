package domain

import "testing"

// The incinerator burns rotten stranger corpses (#1822); fresh ones wait for
// the butcher (#1811) and colonists and slaves keep the tomb.
func TestIncineratorFilterTakesRottenStrangerCorpsesOnly(t *testing.T) {
	f := IncineratorFilter()
	has := func(list []FilterSelector, want FilterSelector) bool {
		for _, s := range list {
			if s == want {
				return true
			}
		}
		return false
	}
	if !has(f.Allow(), CategoryDef("CorpsesHumanlike")) {
		t.Fatal("humanlike corpses not allowed")
	}
	for _, special := range []string{"AllowFresh", "AllowCorpsesColonist", "AllowCorpsesSlave"} {
		if !has(f.Disallow(), SpecialFilter(special)) {
			t.Errorf("%s not disallowed", special)
		}
	}
}

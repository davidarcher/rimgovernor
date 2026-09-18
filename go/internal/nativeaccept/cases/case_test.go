package cases

import (
	"context"
	"testing"
)

func noop(context.Context, Session) error { return nil }

func TestRegisterRejectsDuplicateNames(t *testing.T) {
	reset()
	defer reset()
	Register(Case{Name: "a/one", Start: DebugStart{}, Run: noop})
	defer func() {
		if recover() == nil {
			t.Fatalf("second Register of a/one did not panic")
		}
	}()
	Register(Case{Name: "a/one", Start: Save{Name: "x"}, Run: noop})
}

func TestRegisterRejectsIncompleteCases(t *testing.T) {
	reset()
	defer reset()
	for _, c := range []Case{
		{Start: DebugStart{}, Run: noop},
		{Name: "a/nostart", Run: noop},
		{Name: "a/norun", Start: DebugStart{}},
	} {
		if err := c.Validate(); err == nil {
			t.Errorf("Validate(%+v) = nil", c)
		}
	}
}

func TestAllSortsAndLookupFinds(t *testing.T) {
	reset()
	defer reset()
	Register(Case{Name: "b/two", Start: DebugStart{}, Run: noop})
	Register(Case{Name: "a/one", Start: Fixture{Op: "test/x"}, Run: noop})
	all := All()
	if len(all) != 2 || all[0].Name != "a/one" || all[1].Name != "b/two" {
		t.Fatalf("All() = %v", all)
	}
	if c, ok := Lookup("b/two"); !ok || c.Name != "b/two" {
		t.Fatalf("Lookup(b/two) = %+v, %v", c, ok)
	}
	if _, ok := Lookup("c/none"); ok {
		t.Fatalf("Lookup(c/none) found a case")
	}
}

package domain

import (
	"context"
	"strings"
	"testing"
)

// A read is stale only for another world or native generation, or an
// invalidated view; its tick never makes it stale.
func TestReadValidityStale(t *testing.T) {
	t.Parallel()
	scope := ReadScope{Colony: "c", Map: 1, Load: "l", Native: 4}
	step := ReadValidity{Scope: scope, Tick: 10000}
	other := scope
	other.Native++
	reloaded := scope
	reloaded.Load = "l2"
	cases := []struct {
		name  string
		v     ReadValidity
		have  ReadScope
		stale string
	}{
		{"same scope", step, scope, ""},
		{"generation change", step, other, "native generation 5, step is 4"},
		{"reload", step, reloaded, "scope c/1/l2, step is c/1/l"},
		{"invalidated view", ReadValidity{Scope: scope, Tick: 10000, Invalid: "event gap"}, scope, "view invalidated: event gap"},
		{"unknown validity checks nothing", ReadValidity{}, other, ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got := c.v.Stale(c.have)
			if c.stale == "" && got != "" || c.stale != "" && !strings.Contains(got, c.stale) {
				t.Fatalf("stale %q, want %q", got, c.stale)
			}
		})
	}
}

func TestReadValidityContextHelpers(t *testing.T) {
	scope := ReadScope{Colony: "c", Map: 1, Load: "l", Native: 1}
	ctx := WithReadValidity(context.Background(), ReadValidity{Scope: scope, Tick: 100})
	if got, ok := ReadValidityFrom(ctx); !ok || got.Tick != 100 {
		t.Fatal(got, ok)
	}
	if _, ok := ReadValidityFrom(context.Background()); ok {
		t.Fatal("bare context carries no validity")
	}
}

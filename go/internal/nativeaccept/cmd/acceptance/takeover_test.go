package main

import (
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"testing"
)

func TestTakeoverCasesRegisteredForNightly(t *testing.T) {
	for _, name := range []string{"draft"} {
		c, ok := cases.Lookup("takeover/" + name)
		if !ok {
			t.Fatalf("missing takeover/%s", name)
		}
		if err := c.Lint(); err != nil {
			t.Fatal(err)
		}
		if c.Matrix {
			t.Fatalf("%s is a matrix case", c.Name)
		}
	}
}

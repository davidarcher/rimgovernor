package cases

import (
	"testing"
	"time"
)

func TestExpansionFixtureRequiresPrivateProcess(t *testing.T) {
	c := Case{Name: "test/odyssey", Start: DebugStart{}, Expansions: []string{"ludeon.rimworld.odyssey"}, Run: noop, Budget: time.Minute}
	if err := c.Validate(); err == nil {
		t.Fatal("DLC fixture can leak a kept process into Core cases")
	}
	c.NoKeep = true
	if err := c.Validate(); err != nil {
		t.Fatal(err)
	}
	c.Expansions = []string{"ludeon.rimworld"}
	if err := c.Validate(); err == nil {
		t.Fatal("Core is not an expansion")
	}
}

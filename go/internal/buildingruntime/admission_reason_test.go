package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestAcquisitionReasonNamesRunwayPestOrStock(t *testing.T) {
	rows := []policy.AcquisitionSource{{ID: "a", Resource: "WoodLog", Definition: "Megascarab"}}
	for _, c := range []struct {
		food, pest bool
		runway     domain.Fact[float64]
		rows       []policy.AcquisitionSource
		want       string
	}{
		{true, false, domain.Known(3.24), rows, "food runway 3.2d"},
		{true, false, domain.Unknown[float64](), rows, ""},
		{false, true, domain.Unknown[float64](), rows, "pest Megascarab"},
		{false, false, domain.Unknown[float64](), rows, "WoodLog low"},
		{true, false, domain.Known(1.0), nil, ""},
	} {
		if got := acquisitionReason(c.food, c.pest, c.runway, c.rows); got != c.want {
			t.Errorf("food=%v pest=%v = %q, want %q", c.food, c.pest, got, c.want)
		}
	}
}

func TestBedroomShellReasonCountsUnhoused(t *testing.T) {
	if got := bedroomShellReason(policy.BedroomStep{Kind: policy.BedroomReconcile, Unhoused: 2}); got != "room for 2 unhoused" {
		t.Fatal(got)
	}
	if got := bedroomShellReason(policy.BedroomStep{Kind: policy.BedroomReconcile}); got != "" {
		t.Fatal(got)
	}
}

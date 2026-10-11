package bridge

import (
	"math"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The body block rides the work detail, and only finite, non-negative stats pass.
func TestValidateSettingsBodyStats(t *testing.T) {
	f := func(v float64) *float64 { return &v }
	body := &o.PawnBodyStats{ComfortableMinC: f(-12), ComfortableMaxC: f(30), MoveSpeed: f(4.6), Hypothermia: f(0.1), Moving: f(1)}
	if err := validateSettings(&o.PawnSettings{Body: body}, true, false, false); err != nil {
		t.Fatal(err)
	}
	if err := validateSettings(&o.PawnSettings{Body: body}, false, true, true); err == nil {
		t.Fatal("body accepted without work detail")
	}
	for name, bad := range map[string]*o.PawnBodyStats{
		"nan temperature":   {ComfortableMinC: f(math.NaN())},
		"inf temperature":   {CellTemperatureC: f(math.Inf(-1))},
		"negative speed":    {MoveSpeed: f(-1)},
		"negative severity": {Hypothermia: f(-0.1)},
		"nan capacity":      {Moving: f(math.NaN())},
	} {
		if err := validateSettings(&o.PawnSettings{Body: bad}, true, false, false); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

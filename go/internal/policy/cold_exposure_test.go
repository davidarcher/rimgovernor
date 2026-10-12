package policy

import (
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// seaIce is a sea-ice map's twelfth means (C): a cold year whose summer stays
// under the full-rot temperature even with the diurnal swing added.
var seaIce = []float64{-38, -36, -30, -22, -14, -6, -4, -8, -16, -24, -32, -37}

// shortSummer peaks at 4 C: its mean alone is under rot, the +7 C afternoon
// swing on top is not (10.995 C with one vent).
var shortSummer = []float64{-20, -18, -12, -4, 0, 4, 4, 0, -4, -10, -16, -20}

func TestColdExposureSnapshot(t *testing.T) {
	const rot = 10.0
	cases := []struct {
		name  string
		temps []float64
		cells int
		peak  float64
		want  ColdExposure
	}{
		{"sea ice small room", seaIce, 12, 2.852, ColdExposure{Satisfied: true, Vents: 1, Cells: 12, ThresholdC: rot}},
		{"sea ice big room", seaIce, 120, 2.275, ColdExposure{Satisfied: true, Vents: 1, Cells: 120, ThresholdC: rot}},
		{"the swing alone breaks rot", shortSummer, 12, 10.995, ColdExposure{Cells: 12, ThresholdC: rot}},
		{"temperate year is not exposed", []float64{2, 4, 8, 12, 16, 19, 21, 20, 16, 11, 6, 3}, 12, 27.959, ColdExposure{Cells: 12, ThresholdC: rot}},
	}
	for _, c := range cases {
		got := ColdExposureFor(c.temps, c.cells, ExposureMaxVents, rot)
		if math.Abs(got.PeakC-c.peak) > 0.001 {
			t.Errorf("%s: peak %.3f, want %.3f", c.name, got.PeakC, c.peak)
		}
		got.PeakC = 0
		if got != c.want {
			t.Errorf("%s: got %+v, want %+v", c.name, got, c.want)
		}
	}
}

func TestColdExposurePeakNeverFallsWithVents(t *testing.T) {
	prev := math.Inf(-1)
	for v := 1; v <= ExposureMaxVents; v++ {
		p := exposurePeak(seaIce, 12, v)
		if p < prev-1e-9 {
			t.Errorf("peak fell from %.3f to %.3f at %d vents", prev, p, v)
		}
		prev = p
	}
	if slow, fast := exposurePeak(shortSummer, 120, 1), exposurePeak(shortSummer, 12, ExposureMaxVents); slow >= fast {
		t.Errorf("a big room with one vent peaks at %.3f, not under the small room with eight at %.3f", slow, fast)
	}
}

func TestColdExposureThreshold(t *testing.T) {
	one := exposurePeak(shortSummer, 40, 1)
	if got := ColdExposureFor(shortSummer, 40, ExposureMaxVents, one+0.01); !got.Satisfied || got.Vents != 1 || got.PeakC != one {
		t.Errorf("threshold over one vent's peak: %+v", got)
	}
	if got := ColdExposureFor(shortSummer, 40, ExposureMaxVents, one); got.Satisfied || got.Vents != 0 || got.PeakC != one {
		t.Errorf("threshold at one vent's peak (not below it): %+v", got)
	}
}

func TestColdExposureRefusesBadInput(t *testing.T) {
	for name, got := range map[string]ColdExposure{
		"short curve": ColdExposureFor(seaIce[:11], 12, 4, 10),
		"no cells":    ColdExposureFor(seaIce, 0, 4, 10),
		"no vents":    ColdExposureFor(seaIce, 12, 0, 10),
	} {
		if got.Satisfied {
			t.Errorf("%s satisfied: %+v", name, got)
		}
	}
}

func TestGroupExposureIsBesideColdGroupsOnly(t *testing.T) {
	f := StationFacts{OutdoorCurve: domain.Known(slices.Clone(seaIce)), FullRotRateC: 10}
	cold := RoomGroup{Requires: []EnvRequirement{EnvCold}, Area: 12}
	if got := groupExposure(f, cold); got == nil || !got.Satisfied || got.Cells != 12 || got.Vents != 1 {
		t.Fatalf("cold group: %+v", got)
	}
	if groupExposure(f, RoomGroup{Area: 12}) != nil {
		t.Error("a group that does not require cold got an exposure answer")
	}
	f.OutdoorCurve = domain.Unknown[[]float64]()
	if groupExposure(f, cold) != nil {
		t.Error("exposure without a curve")
	}
	f.OutdoorCurve, f.FullRotRateC = domain.Known(slices.Clone(seaIce)), 0
	if groupExposure(f, cold) != nil {
		t.Error("exposure without a full-rot temperature")
	}
}

func TestAdviseStationsAttachesExposureToColdGroups(t *testing.T) {
	f := stationFactsFixture()
	f.OutdoorCurve, f.FullRotRateC = domain.Known(slices.Clone(seaIce)), 10
	advice, err := AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	for _, g := range advice.Groups {
		if cold := slices.Contains(g.Group.Requires, EnvCold); cold != (g.Exposure != nil) {
			t.Errorf("group %d: requires cold %v, exposure %v", g.Group.ID, cold, g.Exposure)
		}
	}
	f.OutdoorCurve = domain.Unknown[[]float64]()
	if advice, err = AdviseStations(f); err != nil {
		t.Fatal(err)
	}
	for _, g := range advice.Groups {
		if g.Exposure != nil {
			t.Errorf("exposure without a curve: %+v", g)
		}
	}
}

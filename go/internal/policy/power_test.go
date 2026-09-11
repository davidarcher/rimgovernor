package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestPowerCoverageUsesConsumerNetworksAndAvailability(t *testing.T) {
	building := func(base, output float64, net string) PowerBuilding {
		return PowerBuilding{BaseW: domain.Known(base), OutputW: domain.Known(output), Network: domain.Known(net), Connected: domain.Known(true), Powered: domain.Known(true), Forbidden: domain.Known(false), SwitchedOn: domain.Known(true)}
	}
	for _, phase := range []string{"surplus", "isolated", "disconnected", "unpowered", "forbidden", "switched-off", "unknown-output", "unknown-base", "empty", "unavailable"} {
		t.Run(phase, func(t *testing.T) {
			rows := []PowerBuilding{building(1000, 1000, "a"), building(-200, -200, "a")}
			wantRequired, wantDisabled, wantHeadroom, known := true, false, float64(800), true
			switch phase {
			case "isolated":
				rows[1].Network = domain.Known("b")
				wantHeadroom = -200
			case "disconnected":
				rows[1].Connected = domain.Known(false)
				rows[1].Network = domain.Unknown[string]()
				wantHeadroom = -1
			case "unpowered", "forbidden", "switched-off":
				rows[1].Powered = domain.Known(false)
				wantHeadroom = -1
				wantDisabled = phase == "unpowered"
				if phase == "forbidden" {
					rows[1].Forbidden = domain.Known(true)
				}
				if phase == "switched-off" {
					rows[1].SwitchedOn = domain.Known(false)
				}
			case "unknown-output":
				rows[0].OutputW = domain.Unknown[float64]()
				known = false
			case "unknown-base":
				rows[0].BaseW = domain.Unknown[float64]()
				known = false
			case "empty":
				rows = nil
				wantRequired = false
				wantHeadroom = 0
			case "unavailable":
				known = false
			}
			facts := domain.Known(rows)
			if phase == "unavailable" {
				facts = domain.Unknown[[]PowerBuilding]()
			}
			required, headroom, disabled := PowerCoverage(facts)
			if phase == "unknown-base" || phase == "unavailable" {
				if _, ok := required.Value(); ok {
					t.Fatal(required)
				}
				return
			}
			if value, ok := required.Value(); !ok || value != wantRequired {
				t.Fatal(required)
			}
			if value, ok := disabled.Value(); !ok || value != wantDisabled {
				t.Fatal(disabled)
			}
			if value, ok := headroom.Value(); ok != known || ok && value != wantHeadroom {
				t.Fatal(headroom, known, wantHeadroom)
			}
		})
	}
}

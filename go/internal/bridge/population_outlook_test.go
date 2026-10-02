package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestDecodePopulationOutlook(t *testing.T) {
	present, err := decodePopulation(&o.PopulationSnapshot{
		PopulationIntent: proto.Float64(1.5), AdjustedPopulation: proto.Float64(4),
		DeathOnDownedChance: proto.Float64(0), UnrecruitableChance: proto.Float64(0.115),
	}, Pawns{})
	if err != nil {
		t.Fatal(err)
	}
	for name, want := range map[string]struct {
		got  func() (float64, bool)
		want float64
	}{
		"intent":        {present.Outlook.Intent.Value, 1.5},
		"adjusted":      {present.Outlook.AdjustedPopulation.Value, 4},
		"death":         {present.Outlook.DeathOnDownedChance.Value, 0},
		"unrecruitable": {present.Outlook.UnrecruitableChance.Value, 0.115},
	} {
		if got, known := want.got(); !known || got != want.want {
			t.Errorf("%s = %v, %v; want %v known", name, got, known, want.want)
		}
	}

	absent, err := decodePopulation(&o.PopulationSnapshot{}, Pawns{})
	if err != nil {
		t.Fatal(err)
	}
	out := absent.Outlook
	for name, value := range map[string]func() (float64, bool){
		"intent": out.Intent.Value, "adjusted": out.AdjustedPopulation.Value,
		"death": out.DeathOnDownedChance.Value, "unrecruitable": out.UnrecruitableChance.Value,
	} {
		if _, known := value(); known {
			t.Errorf("absent %s decoded as known", name)
		}
	}
}

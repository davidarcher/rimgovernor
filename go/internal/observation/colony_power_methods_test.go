package observation

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

// The captured initial proposal and final native consumer state bracket real
// Go service construction. Fixtures alone cannot satisfy this acceptance gate.
func TestNativePowerMethodsReplay(t *testing.T) {
	directory := os.Getenv("RIMGOVERNOR_NATIVE_POWER_METHODS_CAPTURE")
	if directory == "" {
		t.Skip("requires completed native power method acceptance")
	}
	var evidence struct {
		Mode string `json:"mode"`
	}
	data, err := os.ReadFile(filepath.Join(directory, "power-methods.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = json.Unmarshal(data, &evidence); err != nil {
		t.Fatal(err)
	}
	for _, phase := range []string{"initial", "outcome"} {
		data, err := os.ReadFile(filepath.Join(directory, "power-"+phase+".json"))
		if err != nil {
			t.Fatal(err)
		}
		reply := &o.ColonyFactsReply{}
		if err = protojson.Unmarshal(data, reply); err != nil {
			t.Fatal(err)
		}
		identity, err := contextIdentity(reply.GetObserved().Context)
		if err != nil {
			t.Fatal(err)
		}
		facts, err := DecodeColony(reply, identity)
		if err != nil {
			t.Fatal(err)
		}
		proposal, err := policy.SelectPowerMethod(facts.PowerPlanning, facts.Bounds, facts.Cells, nil, policy.DefaultPowerPlanning())
		if err != nil {
			t.Fatal(err)
		}
		want := policy.PowerGenerate
		if evidence.Mode == "conduit" {
			want = policy.PowerConnect
		} else if evidence.Mode != "generation" {
			t.Fatal("unknown acceptance mode")
		}
		if phase == "outcome" {
			want = policy.PowerNoMethod
			if facts.Facts.PowerRequired != domain.Known(true) || facts.Facts.DisabledConsumers != domain.Known(false) {
				t.Fatal("consumer did not recover", facts.Facts)
			}
			if value, known := facts.Facts.PowerHeadroom.Value(); !known || value < 0 {
				t.Fatal("native network lacks power", facts.Facts.PowerHeadroom)
			}
		}
		if proposal.Method != want {
			t.Fatal(phase, proposal, want)
		}
	}
}

package observation

import (
	"encoding/json"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"os"
	"path/filepath"
	"testing"
)

func TestNativePowerParity(t *testing.T) {
	dir := os.Getenv("RIMGOVERNOR_NATIVE_POWER_CAPTURE")
	if dir == "" {
		t.Skip("requires native power capture")
	}
	data, err := os.ReadFile(filepath.Join(dir, "work-colony.json"))
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	expected, err := contextIdentity(reply.GetObserved().Context)
	if err != nil {
		t.Fatal(err)
	}
	projection, err := DecodeColony(reply, expected)
	if err != nil {
		t.Fatal(err)
	}
	var reference struct {
		Required, Disabled bool
		Headroom           float64
	}
	data, err = os.ReadFile(filepath.Join(dir, "power-reference.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(data, &reference); err != nil {
		t.Fatal(err)
	}
	facts := projection.Facts
	if value, known := facts.PowerRequired.Value(); !known || value != reference.Required {
		t.Fatal(facts.PowerRequired, reference)
	}
	if value, known := facts.DisabledConsumers.Value(); !known || value != reference.Disabled {
		t.Fatal(facts.DisabledConsumers, reference)
	}
	if value, known := facts.PowerHeadroom.Value(); !known || value != reference.Headroom {
		t.Fatal(facts.PowerHeadroom, reference)
	}
	t.Logf("Native power parity: required=%v headroom=%v disabled=%v", reference.Required, reference.Headroom, reference.Disabled)
}

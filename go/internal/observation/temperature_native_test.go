package observation

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func TestNativeTemperatureMethodsReplay(t *testing.T) {
	directory := os.Getenv("RIMGOVERNOR_NATIVE_TEMPERATURE_CAPTURE")
	if directory == "" {
		t.Skip("requires completed native temperature method acceptance")
	}
	var evidence struct {
		Mode string `json:"mode"`
	}
	data, err := os.ReadFile(filepath.Join(directory, "temperature-methods.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = json.Unmarshal(data, &evidence); err != nil {
		t.Fatal(err)
	}
	if evidence.Mode != "cold" && evidence.Mode != "hot" {
		t.Fatal(evidence)
	}
	for _, phase := range []string{"initial", "outcome"} {
		data, err := os.ReadFile(filepath.Join(directory, "temperature-"+phase+"-colony.json"))
		if err != nil {
			t.Fatal(err)
		}
		colony := &o.ColonyFactsReply{}
		if err = protojson.Unmarshal(data, colony); err != nil {
			t.Fatal(err)
		}
		identity, err := contextIdentity(colony.GetObserved().Context)
		if err != nil {
			t.Fatal(err)
		}
		facts, err := DecodeColony(colony, identity)
		if err != nil {
			t.Fatal(err)
		}
		data, err = os.ReadFile(filepath.Join(directory, "temperature-"+phase+"-rooms.json"))
		if err != nil {
			t.Fatal(err)
		}
		rooms := &o.ListRoomsReply{}
		if err = protojson.Unmarshal(data, rooms); err != nil {
			t.Fatal(err)
		}
		if err = bridge.ValidateTemperatureRooms(rooms.GetObserved(), colony.GetObserved().Context.Identity); err != nil {
			t.Fatal(err)
		}
		if rooms.GetObserved().Context.GetTick() != colony.GetObserved().Context.GetTick() {
			t.Fatal("room read escaped colony bracket")
		}
		planning := temperatureRooms(rooms.GetObserved(), facts.Facts.Sleeping)
		facts.Facts.SleepingMin, facts.Facts.SleepingMax = policy.TemperatureRange(planning)
		latches := policy.RoutineLatches{Cold: evidence.Mode == "cold", Hot: evidence.Mode == "hot"}
		proposal, err := policy.SelectTemperatureMethod(planning, policy.DefaultRoutinePolicy(), latches)
		if err != nil {
			t.Fatal(err)
		}
		want := policy.TemperatureHeat
		if evidence.Mode == "hot" {
			want = policy.TemperatureCool
		}
		if phase == "outcome" {
			want = policy.TemperatureNoMethod
		}
		if proposal.Method != want {
			t.Fatal(phase, proposal, want)
		}
		low, lk := facts.Facts.SleepingMin.Value()
		high, hk := facts.Facts.SleepingMax.Value()
		if !lk || !hk || phase == "outcome" && (low < 16 || high > 28) {
			t.Fatal(phase, low, high, lk, hk)
		}
	}
}

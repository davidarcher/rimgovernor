package main

import (
	"encoding/json"
	"io"
	"net/http"
	"reflect"
	"sync"
	"testing"

	bridgepkg "github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

func f32(v float32) *float32 { return &v }
func u32(v uint32) *uint32   { return &v }

// partsSpec is a solo spec carrying one of every part kind the composer edits.
func partsSpec() NewColonySpec {
	s := DefaultNewColonySpec()
	s.ColonistCount = 1
	s.Seed = "parts"
	s.PlayerFaction, s.ArrivalMethod = "new_arrivals", "drop_pods"
	s.StartingThings = []httpapi.StartingThingDTO{
		{Placement: "start_with", ThingDef: "Silver", Count: u32(800)},
		{Placement: "scattered", ThingDef: "Steel", Count: u32(720)},
	}
	s.ForcedTraits = []httpapi.ForcedTraitDTO{{Trait: "NaturalMood", Degree: new(int32), Chance: f32(1), Context: "player_starter"}}
	*s.ForcedTraits[0].Degree = 2
	s.ForcedHediffs = []httpapi.ForcedHediffDTO{{Hediff: "Asthma", Severity: f32(0.5), Chance: f32(1), Context: "all"}}
	s.PermaGameConditions = []string{"ColdSnap"}
	s.PermaMapConditions = []string{"ColdSnap"}
	s.DisallowedBuildings = []string{"Turret_Mortar"}
	s.DisabledIncidents = []string{"Eclipse"}
	return s
}

func TestScenarioChoicesAreValidParts(t *testing.T) {
	c := scenarioChoices
	d := scenarioPartDefaults
	for _, o := range c.Things {
		s := DefaultNewColonySpec()
		s.StartingThings = []httpapi.StartingThingDTO{{Placement: d.Placement, ThingDef: o.Value, Count: u32(d.Count)}}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("thing %s: %v", o.Value, err)
		}
	}
	for _, o := range c.Placements {
		s := DefaultNewColonySpec()
		s.StartingThings = []httpapi.StartingThingDTO{{Placement: o.Value, ThingDef: "Silver", Count: u32(1)}}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("placement %s: %v", o.Value, err)
		}
	}
	for _, o := range c.Contexts {
		s := DefaultNewColonySpec()
		s.ForcedHediffs = []httpapi.ForcedHediffDTO{{Hediff: "Asthma", Severity: f32(d.Severity), Chance: f32(d.Chance), Context: o.Value}}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("context %s: %v", o.Value, err)
		}
	}
	for _, o := range c.Traits {
		deg := o.Degree
		s := DefaultNewColonySpec()
		s.ForcedTraits = []httpapi.ForcedTraitDTO{{Trait: o.Value, Degree: &deg, Chance: f32(d.Chance), Context: d.Context}}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("trait %s %d: %v", o.Value, o.Degree, err)
		}
	}
	for _, o := range c.Hediffs {
		s := DefaultNewColonySpec()
		s.ForcedHediffs = []httpapi.ForcedHediffDTO{{Hediff: o.Value, Severity: f32(d.Severity), Chance: f32(d.Chance), Context: d.Context}}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("hediff %s: %v", o.Value, err)
		}
	}
	for _, o := range c.Conditions {
		s := DefaultNewColonySpec()
		s.PermaGameConditions = []string{o.Value}
		s.PermaMapConditions = []string{o.Value}
		s.DisabledIncidents = []string{o.Value}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("condition %s: %v", o.Value, err)
		}
	}
	for _, o := range c.Incidents {
		s := DefaultNewColonySpec()
		s.DisabledIncidents = []string{o.Value}
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("incident %s: %v", o.Value, err)
		}
	}
	for _, o := range c.PlayerFactions {
		s := DefaultNewColonySpec()
		s.PlayerFaction = o.Value
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("faction %q: %v", o.Value, err)
		}
	}
	for _, o := range c.ArrivalMethods {
		s := DefaultNewColonySpec()
		s.ArrivalMethod = o.Value
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("arrival %q: %v", o.Value, err)
		}
	}
	if err := ValidateNewColonySpec(partsSpec()); err != nil {
		t.Fatal(err)
	}
}

func TestNewColonyRefusesInvalidPartsBeforeAnyStop(t *testing.T) {
	r, host := colonyFixture(t, func(http.ResponseWriter, *http.Request) { t.Error("served") })
	bad := map[string]func(*NewColonySpec){
		"no thing count":     func(s *NewColonySpec) { s.StartingThings[0].Count = nil },
		"zero thing count":   func(s *NewColonySpec) { s.StartingThings[0].Count = u32(0) },
		"blank thing":        func(s *NewColonySpec) { s.StartingThings[0].ThingDef = "" },
		"bad placement":      func(s *NewColonySpec) { s.StartingThings[0].Placement = "nowhere" },
		"roofed not scatter": func(s *NewColonySpec) { b := true; s.StartingThings[0].AllowRoofed = &b },
		"trait degree":       func(s *NewColonySpec) { *s.ForcedTraits[0].Degree = 99 },
		"trait chance":       func(s *NewColonySpec) { s.ForcedTraits[0].Chance = f32(2) },
		"trait context":      func(s *NewColonySpec) { s.ForcedTraits[0].Context = "everyone" },
		"hediff severity":    func(s *NewColonySpec) { s.ForcedHediffs[0].Severity = f32(-1) },
		"blank condition":    func(s *NewColonySpec) { s.PermaGameConditions = []string{" "} },
		"faction":            func(s *NewColonySpec) { s.PlayerFaction = "empire" },
		"arrival":            func(s *NewColonySpec) { s.ArrivalMethod = "teleport" },
		"too many incidents": func(s *NewColonySpec) { s.DisabledIncidents = make([]string, bridgepkg.NewColonyMaxPartsPerKind+1) },
		"too many colonists": func(s *NewColonySpec) { s.ColonistCount = bridgepkg.NewColonyMaxColonists + 1 },
	}
	for name, mutate := range bad {
		spec := partsSpec()
		mutate(&spec)
		if err := r.Generate(spec); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
	if closes, starts := host.counts(); closes != 0 || starts != 0 || r.View().Progress.State != ColonyIdle {
		t.Fatalf("stopped before validating: %d %d", closes, starts)
	}
}

func TestNewColonySpecWithPartsRoundTrips(t *testing.T) {
	var mu sync.Mutex
	var posted []byte
	r, host := colonyFixture(t, func(w http.ResponseWriter, req *http.Request) {
		if req.Method == http.MethodPost {
			b, _ := io.ReadAll(req.Body)
			mu.Lock()
			posted = b
			mu.Unlock()
		}
		w.Write([]byte(completedBody))
	})
	spec := partsSpec()
	if err := r.Generate(spec); err != nil {
		t.Fatal(err)
	}
	waitColony(t, r, ColonyCompleted)

	// The HTTP call: the server's own DTO and conversion read the body back
	// into the wire spec the launcher validated.
	mu.Lock()
	defer mu.Unlock()
	var req httpapi.NewColonyRequestDTO
	if err := json.Unmarshal(posted, &req); err != nil {
		t.Fatal(err, string(posted))
	}
	got, err := req.Spec.Proto()
	if err != nil {
		t.Fatal(err)
	}
	if err := bridgepkg.ValidateNewColonySpec(got); err != nil {
		t.Fatal(err)
	}
	if got.GetColonistCount() != 1 || got.GetPlayerFaction() != l.ScenarioPlayerFaction_SCENARIO_PLAYER_FACTION_NEW_ARRIVALS ||
		got.GetArrivalMethod() != l.ScenarioArrivalMethod_SCENARIO_ARRIVAL_METHOD_DROP_PODS {
		t.Fatalf("overrides: %v", got)
	}
	if len(got.StartingThings) != 2 || got.StartingThings[1].GetPlacement() != l.StartingThingPlacement_STARTING_THING_PLACEMENT_SCATTERED ||
		got.StartingThings[1].GetThingDef() != "Steel" || got.StartingThings[1].GetCount() != 720 {
		t.Fatalf("things: %v", got.StartingThings)
	}
	if len(got.ForcedTraits) != 1 || got.ForcedTraits[0].GetTrait() != "NaturalMood" || got.ForcedTraits[0].GetDegree() != 2 ||
		len(got.ForcedHediffs) != 1 || got.ForcedHediffs[0].GetSeverity() != 0.5 ||
		!reflect.DeepEqual(got.PermaGameConditions, []string{"ColdSnap"}) || !reflect.DeepEqual(got.PermaMapConditions, []string{"ColdSnap"}) ||
		!reflect.DeepEqual(got.DisallowedBuildings, []string{"Turret_Mortar"}) || !reflect.DeepEqual(got.DisabledIncidents, []string{"Eclipse"}) {
		t.Fatalf("parts: %v", got)
	}

	// The settings file: the spec Generate saved loads back whole.
	host.mu.Lock()
	saved := host.saved[0]
	host.mu.Unlock()
	s := DefaultSettings()
	s.NewColony = &saved
	path := t.TempDir() + "/launcher.json"
	if err := SaveSettings(path, s); err != nil {
		t.Fatal(err)
	}
	loaded, err := LoadSettings(path)
	if err != nil || loaded.NewColony == nil || !reflect.DeepEqual(*loaded.NewColony, saved) {
		t.Fatalf("settings: %+v %v", loaded.NewColony, err)
	}
	if loaded.NewColony.StartingThings[0].ThingDef != "Silver" || *loaded.NewColony.StartingThings[0].Count != 800 {
		t.Fatalf("parts lost: %+v", loaded.NewColony)
	}
}

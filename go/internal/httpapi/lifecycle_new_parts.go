package httpapi

import (
	"fmt"
	"strings"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// NewColonyPartsDTO are the scenario parts of a new colony, layered on the base
// scenario. Numeric fields are pointers so an absent value fails the shared
// bridge validation instead of reading as zero. Enums travel as lower-case names.
type NewColonyPartsDTO struct {
	StartingThings      []StartingThingDTO `json:"startingThings,omitempty"`
	ForcedTraits        []ForcedTraitDTO   `json:"forcedTraits,omitempty"`
	ForcedHediffs       []ForcedHediffDTO  `json:"forcedHediffs,omitempty"`
	Naked               []NakedDTO         `json:"naked,omitempty"`
	SetNeedLevels       []SetNeedLevelDTO  `json:"setNeedLevels,omitempty"`
	PermaGameConditions []string           `json:"permaGameConditions,omitempty"`
	DisallowedBuildings []string           `json:"disallowedBuildings,omitempty"`
	DisabledIncidents   []string           `json:"disabledIncidents,omitempty"`
	PlayerFaction       string             `json:"playerFaction,omitempty"`
	ArrivalMethod       string             `json:"arrivalMethod,omitempty"`
}
type StartingThingDTO struct {
	Placement   string  `json:"placement"`
	ThingDef    string  `json:"thingDef"`
	Stuff       *string `json:"stuff,omitempty"`
	Count       *uint32 `json:"count"`
	Quality     *string `json:"quality,omitempty"`
	AllowRoofed *bool   `json:"allowRoofed,omitempty"`
}
type ForcedTraitDTO struct {
	Trait   string   `json:"trait"`
	Degree  *int32   `json:"degree"`
	Chance  *float32 `json:"chance"`
	Context string   `json:"context"`
}
type ForcedHediffDTO struct {
	Hediff   string   `json:"hediff"`
	Severity *float32 `json:"severity"`
	Chance   *float32 `json:"chance"`
	Context  string   `json:"context"`
}
type NakedDTO struct {
	Chance  *float32 `json:"chance"`
	Context string   `json:"context"`
}
type SetNeedLevelDTO struct {
	Need    string   `json:"need"`
	Level   *float32 `json:"level"`
	Chance  *float32 `json:"chance"`
	Context string   `json:"context"`
}

// enumByName maps a lower-case wire name to its enum value.
func enumByName[E ~int32](name string, prefix string, names map[int32]string) (E, error) {
	want := prefix + strings.ToUpper(name)
	for value, n := range names {
		if n == want && value != 0 {
			return E(value), nil
		}
	}
	return 0, fmt.Errorf("unknown value %q", name)
}

func pawnContext(name string) (*l.ScenarioPawnContext, error) {
	v, err := enumByName[l.ScenarioPawnContext](name, "SCENARIO_PAWN_CONTEXT_", l.ScenarioPawnContext_name)
	return &v, err
}

// applyNewColonyParts copies the parts onto the spec; a malformed enum name is an error.
func applyNewColonyParts(spec *l.NewColonySpec, parts NewColonyPartsDTO) error {
	for _, t := range parts.StartingThings {
		placement, err := enumByName[l.StartingThingPlacement](t.Placement, "STARTING_THING_PLACEMENT_", l.StartingThingPlacement_name)
		if err != nil {
			return fmt.Errorf("placement: %w", err)
		}
		spec.StartingThings = append(spec.StartingThings, &l.StartingThing{
			Placement: &placement, ThingDef: proto.String(t.ThingDef), Stuff: t.Stuff, Count: t.Count,
			Quality: t.Quality, AllowRoofed: t.AllowRoofed,
		})
	}
	for _, t := range parts.ForcedTraits {
		ctx, err := pawnContext(t.Context)
		if err != nil {
			return fmt.Errorf("context: %w", err)
		}
		spec.ForcedTraits = append(spec.ForcedTraits, &l.ForcedTrait{Trait: proto.String(t.Trait), Degree: t.Degree, Chance: t.Chance, Context: ctx})
	}
	for _, h := range parts.ForcedHediffs {
		ctx, err := pawnContext(h.Context)
		if err != nil {
			return fmt.Errorf("context: %w", err)
		}
		spec.ForcedHediffs = append(spec.ForcedHediffs, &l.ForcedHediff{Hediff: proto.String(h.Hediff), Severity: h.Severity, Chance: h.Chance, Context: ctx})
	}
	for _, n := range parts.Naked {
		ctx, err := pawnContext(n.Context)
		if err != nil {
			return fmt.Errorf("context: %w", err)
		}
		spec.Naked = append(spec.Naked, &l.NakedPart{Chance: n.Chance, Context: ctx})
	}
	for _, n := range parts.SetNeedLevels {
		ctx, err := pawnContext(n.Context)
		if err != nil {
			return fmt.Errorf("context: %w", err)
		}
		spec.SetNeedLevels = append(spec.SetNeedLevels, &l.SetNeedLevel{Need: proto.String(n.Need), Level: n.Level, Chance: n.Chance, Context: ctx})
	}
	spec.PermaGameConditions = parts.PermaGameConditions
	spec.DisallowedBuildings = parts.DisallowedBuildings
	spec.DisabledIncidents = parts.DisabledIncidents
	if parts.PlayerFaction != "" {
		v, err := enumByName[l.ScenarioPlayerFaction](parts.PlayerFaction, "SCENARIO_PLAYER_FACTION_", l.ScenarioPlayerFaction_name)
		if err != nil {
			return fmt.Errorf("playerFaction: %w", err)
		}
		spec.PlayerFaction = &v
	}
	if parts.ArrivalMethod != "" {
		v, err := enumByName[l.ScenarioArrivalMethod](parts.ArrivalMethod, "SCENARIO_ARRIVAL_METHOD_", l.ScenarioArrivalMethod_name)
		if err != nil {
			return fmt.Errorf("arrivalMethod: %w", err)
		}
		spec.ArrivalMethod = &v
	}
	return nil
}

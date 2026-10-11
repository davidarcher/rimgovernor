// lifecycle/new_colony_parts is the native acceptance for scenario parts on
// the new-colony op (#2753): a one-colonist start carries one of each part
// kind, and the generated save shows each landed (the trait on the pawn, the
// thing counts, the permanent condition active, the scenario copy holding the
// rules and the overrides). Unknown defNames are refused before generation.
package lifecycle

import (
	"context"
	"fmt"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "lifecycle/new_colony_parts",
		Scope: "Scenario parts on the new-colony start (#2753): one colonist with a forced trait, a hediff, a need level, naked, start_with / start_near / scattered things, " +
			"a permanent ColdSnap (not a canBePermanent def), a disallowed building, a disabled incident and the new-tribe faction override; the save shows the trait on the pawn, " +
			"each thing count on the map, the condition active and permanent, and every part in the scenario copy; unknown defNames and an unusable part are refused with the field named before generation.",
		Start:  cases.Owned{},
		Reason: "the start needs a fresh main menu process; the case opens and retires its own game",
		NoKeep: true,
		Budget: 20 * time.Minute,
		Crew:   cases.Crew{Size: 1}, Run: runNewColonyParts,
	})
}

const partsSave = "newcolony-parts-accept"

// Things the base scenario (Crashlanded) does not start with, so their map
// totals are exactly the parts' counts.
var partsThings = []struct {
	placement, def string
	count          int
}{
	{"start_with", "Pemmican", 11},
	{"start_near", "Hay", 20},
	{"scattered", "Kibble", 15},
}

func newColonyPartsSpec() map[string]any {
	spec := newColonySpec()
	spec["colonistCount"], spec["saveName"], spec["seed"] = 1, partsSave, "newcolony-parts-accept"
	var things []any
	for _, t := range partsThings {
		things = append(things, map[string]any{"placement": t.placement, "thingDef": t.def, "count": t.count})
	}
	spec["startingThings"] = things
	spec["forcedTraits"] = []any{map[string]any{"trait": "Jogger", "degree": 0, "chance": 1, "context": "player_starter"}}
	spec["forcedHediffs"] = []any{map[string]any{"hediff": "ToxicBuildup", "severity": 0.1, "chance": 1, "context": "player_starter"}}
	spec["naked"] = []any{map[string]any{"chance": 1, "context": "player_starter"}}
	spec["setNeedLevels"] = []any{map[string]any{"need": "Rest", "level": 0.5, "chance": 1, "context": "player_starter"}}
	spec["permaGameConditions"] = []any{"ColdSnap"}
	spec["disallowedBuildings"] = []any{"Heater"}
	spec["disabledIncidents"] = []any{"WandererJoin"}
	spec["playerFaction"], spec["arrivalMethod"] = "new_tribe", "standing"
	return spec
}

func runNewColonyParts(ctx context.Context, s cases.Session) error {
	cfg, report := s.Config(), s.Report()
	return newColonyMenu(ctx, cfg, report, "parts", func(h *na.Harness, row map[string]any) error {
		if err := newColonyPartsRefusals(ctx, h, row); err != nil {
			return err
		}
		id := fmt.Sprintf("newcolony-parts-%d", time.Now().UnixNano())
		if _, _, err := newColonyRun(ctx, h, id, newColonyPartsSpec(), "parts"); err != nil {
			return err
		}
		if err := newColonyPartsTrait(ctx, h, row); err != nil {
			return err
		}
		data, err := os.ReadFile(cfg.ProfileSave(partsSave))
		if err != nil {
			return fmt.Errorf("the completed start left no save: %w", err)
		}
		return checkPartsSave(string(data), row)
	})
}

// newColonyPartsRefusals: an unknown name in each part kind, and parts the game
// cannot use, are refused before generation, naming the field.
func newColonyPartsRefusals(ctx context.Context, h *na.Harness, row map[string]any) error {
	refusals := map[string]any{}
	row["refusals"] = refusals
	for _, c := range []struct {
		name, field string
		value       any
		contains    string
	}{
		{"thing", "startingThings", []any{map[string]any{"placement": "start_with", "thingDef": "NoSuchThing", "count": 1}}, "starting_things[0].thing_def"},
		{"trait", "forcedTraits", []any{map[string]any{"trait": "NoSuchTrait", "degree": 0, "chance": 1, "context": "all"}}, "forced_traits[0].trait"},
		{"trait degree", "forcedTraits", []any{map[string]any{"trait": "Jogger", "degree": 3, "chance": 1, "context": "all"}}, "forced_traits[0].degree"},
		{"hediff", "forcedHediffs", []any{map[string]any{"hediff": "NoSuchHediff", "severity": 0.1, "chance": 1, "context": "all"}}, "forced_hediffs[0].hediff"},
		{"need", "setNeedLevels", []any{map[string]any{"need": "NoSuchNeed", "level": 0.5, "chance": 1, "context": "all"}}, "set_need_levels[0].need"},
		{"condition", "permaGameConditions", []any{"NoSuchCondition"}, "perma_game_conditions[0]"},
		{"building", "disallowedBuildings", []any{"NoSuchBuilding"}, "disallowed_buildings[0]"},
		{"incident", "disabledIncidents", []any{"NoSuchIncident"}, "disabled_incidents[0]"},
		{"stuff on a stuffless thing", "startingThings", []any{map[string]any{"placement": "start_with", "thingDef": "Silver", "stuff": "Steel", "count": 1}}, "starting_things[0].stuff"},
	} {
		spec := newColonyPartsSpec()
		spec[c.field] = c.value
		reply, err := h.Wire(ctx, "refuse-"+c.name, "lifecycle_new_colony", newColonyRequest("newcolony-parts-bad-"+strings.ReplaceAll(c.name, " ", "-"), spec))
		if err != nil {
			return err
		}
		if err := wantNewColonyFailure(reply, "FAILURE_CODE_INVALID_REQUEST", c.contains); err != nil {
			return fmt.Errorf("bad %s: %w", c.name, err)
		}
		refusals[c.name] = true
	}
	return nil
}

// newColonyPartsTrait: the colonist carries the forced trait.
func newColonyPartsTrait(ctx context.Context, h *na.Harness, row map[string]any) error {
	identity, err := na.ReadIdentity(ctx, h, "parts-identity")
	if err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "parts-pawns", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
		"details": map[string]any{"biography": true},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return fmt.Errorf("pawns: %w", err)
	}
	pawns := na.AsSlice(observed["pawns"])
	if len(pawns) != 1 {
		return fmt.Errorf("want 1 colonist, got %d", len(pawns))
	}
	pawn, _ := na.AsMap(pawns[0])
	bio, _ := na.AsMap(pawn["biography"])
	var traits []string
	for _, raw := range na.AsSlice(bio["traits"]) {
		trait, _ := na.AsMap(raw)
		traits = append(traits, na.AsString(trait["defName"]))
	}
	if indexOf(traits, "Jogger") < 0 {
		return fmt.Errorf("the forced trait Jogger did not land: colonist traits %v", traits)
	}
	row["traits"] = traits
	return nil
}

var (
	partsThingBlock = func(def string) *regexp.Regexp {
		return regexp.MustCompile(`(?s)<thing Class="[^"]*">\s*<def>` + regexp.QuoteMeta(def) + `</def>(.*?)</thing>`)
	}
	partsStackCount  = regexp.MustCompile(`<stackCount>(\d+)</stackCount>`)
	partsScenarioXML = regexp.MustCompile(`(?s)<scenario>.*?</scenario>`)
	partsConditions  = regexp.MustCompile(`(?s)<activeConditions>(.*?)</activeConditions>`)
)

// checkPartsSave asserts, from the saved game, that each part took effect.
func checkPartsSave(save string, row map[string]any) error {
	// Thing counts: the sum of a def's stacks (an omitted stackCount is 1).
	counts := map[string]int{}
	for _, t := range partsThings {
		total := 0
		for _, m := range partsThingBlock(t.def).FindAllStringSubmatch(save, -1) {
			n := 1
			if c := partsStackCount.FindStringSubmatch(m[1]); c != nil {
				n, _ = strconv.Atoi(c[1])
			}
			total += n
		}
		counts[t.def] = total
		if total != t.count {
			return fmt.Errorf("%s %s: %d on the map, want %d", t.placement, t.def, total, t.count)
		}
	}
	row["thing_counts"] = counts

	// The permanent condition is active on the colony map.
	active := strings.Join(func() (all []string) {
		for _, m := range partsConditions.FindAllStringSubmatch(save, -1) {
			all = append(all, m[1])
		}
		return
	}(), "\n")
	if !strings.Contains(active, "<def>ColdSnap</def>") || !strings.Contains(active, "<permanent>True</permanent>") {
		return fmt.Errorf("ColdSnap is not an active permanent condition in the save")
	}

	// The scenario copy holds the parts and the overrides.
	scenario := partsScenarioXML.FindString(save)
	if scenario == "" {
		return fmt.Errorf("the save has no scenario")
	}
	for _, want := range []string{
		`Class="ScenPart_ForcedTrait"`, `<trait>Jogger</trait>`,
		`Class="ScenPart_ForcedHediff"`, `<hediff>ToxicBuildup</hediff>`,
		`Class="ScenPart_Naked"`,
		`Class="ScenPart_SetNeedLevel"`, `<need>Rest</need>`,
		`Class="ScenPart_PermaGameCondition"`, `<gameCondition>ColdSnap</gameCondition>`,
		`Class="ScenPart_DisallowBuilding"`, `<building>Heater</building>`,
		`Class="ScenPart_DisableIncident"`, `<incident>WandererJoin</incident>`,
		`Class="ScenPart_StartingThing_Defined"`, `Class="ScenPart_ScatterThingsNearPlayerStart"`, `Class="ScenPart_ScatterThingsAnywhere"`,
		`<factionDef>PlayerTribe</factionDef>`,
	} {
		if !strings.Contains(scenario, want) {
			return fmt.Errorf("the saved scenario lacks %s", want)
		}
	}
	// The disallowed building is in force.
	if !regexp.MustCompile(`(?s)<disallowedBuildings>.*?<li>Heater</li>`).MatchString(save) {
		return fmt.Errorf("the saved game rules do not disallow Heater")
	}
	// Replace by kind: the base's own things and its absence of the supplied
	// kinds' parts mean Crashlanded's Silver and rifle are gone, not layered under.
	for _, gone := range []string{`<thingDef>Silver</thingDef>`, `<thingDef>Gun_BoltActionRifle</thingDef>`, `<thingDef>MealSurvivalPack</thingDef>`} {
		if strings.Contains(scenario, gone) {
			return fmt.Errorf("the saved scenario still holds the base's %s; the supplied things should replace them", gone)
		}
	}
	row["scenario_parts"] = true
	return nil
}

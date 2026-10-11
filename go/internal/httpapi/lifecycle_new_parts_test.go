package httpapi

import (
	"encoding/json"
	"testing"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

const newColonyPartsJSON = `{
 "startingThings":[
  {"placement":"start_with","thingDef":"Silver","count":800},
  {"placement":"start_near","thingDef":"Rifle","stuff":"Steel","count":1,"quality":"Good"},
  {"placement":"scattered","thingDef":"Steel","count":720,"allowRoofed":false}],
 "forcedTraits":[{"trait":"Gene_Sanguine","degree":0,"chance":1,"context":"player_starter"}],
 "forcedHediffs":[{"hediff":"Flu","severity":0.5,"chance":0.25,"context":"all"}],
 "naked":[{"chance":1,"context":"non_player"}],
 "setNeedLevels":[{"need":"Food","level":0.2,"chance":1,"context":"player_starter"}],
 "permaGameConditions":["ColdSnap"],
 "disallowedBuildings":["TurretGun"],
 "disabledIncidents":["RaidEnemy"],
 "playerFaction":"new_tribe","arrivalMethod":"drop_pods"}`

func TestLifecycleNewColonyScenarioPartsRoundTrip(t *testing.T) {
	s, f, token := lifecycleAPI(t, "automate")
	body := newColonyBodyWith(t, func(spec, _ map[string]any) {
		var parts map[string]any
		if err := json.Unmarshal([]byte(newColonyPartsJSON), &parts); err != nil {
			t.Fatal(err)
		}
		for k, v := range parts {
			spec[k] = v
		}
	})
	out := playerCall(s, "POST", "/api/lifecycle/new", body, token)
	if out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	spec := f.seenNew.GetSpec()
	things := spec.GetStartingThings()
	if len(things) != 3 || things[0].GetPlacement() != l.StartingThingPlacement_STARTING_THING_PLACEMENT_START_WITH || things[0].GetThingDef() != "Silver" || things[0].GetCount() != 800 ||
		things[1].GetPlacement() != l.StartingThingPlacement_STARTING_THING_PLACEMENT_START_NEAR || things[1].GetStuff() != "Steel" || things[1].GetQuality() != "Good" ||
		things[2].GetPlacement() != l.StartingThingPlacement_STARTING_THING_PLACEMENT_SCATTERED || things[2].AllowRoofed == nil || things[2].GetAllowRoofed() {
		t.Fatal(things)
	}
	tr, hd, nk, nl := spec.GetForcedTraits(), spec.GetForcedHediffs(), spec.GetNaked(), spec.GetSetNeedLevels()
	if len(tr) != 1 || tr[0].GetTrait() != "Gene_Sanguine" || tr[0].Degree == nil || tr[0].GetChance() != 1 || tr[0].GetContext() != l.ScenarioPawnContext_SCENARIO_PAWN_CONTEXT_PLAYER_STARTER ||
		len(hd) != 1 || hd[0].GetHediff() != "Flu" || hd[0].GetSeverity() != 0.5 || hd[0].GetChance() != 0.25 || hd[0].GetContext() != l.ScenarioPawnContext_SCENARIO_PAWN_CONTEXT_ALL ||
		len(nk) != 1 || nk[0].GetContext() != l.ScenarioPawnContext_SCENARIO_PAWN_CONTEXT_NON_PLAYER ||
		len(nl) != 1 || nl[0].GetNeed() != "Food" || nl[0].GetLevel() != 0.2 {
		t.Fatal(spec)
	}
	if spec.GetPermaGameConditions()[0] != "ColdSnap" || spec.GetDisallowedBuildings()[0] != "TurretGun" || spec.GetDisabledIncidents()[0] != "RaidEnemy" ||
		spec.GetPlayerFaction() != l.ScenarioPlayerFaction_SCENARIO_PLAYER_FACTION_NEW_TRIBE || spec.GetArrivalMethod() != l.ScenarioArrivalMethod_SCENARIO_ARRIVAL_METHOD_DROP_PODS {
		t.Fatal(spec)
	}
}

func TestLifecycleNewColonyScenarioPartsRejected(t *testing.T) {
	thing := func(placement string, count any, extra map[string]any) []any {
		m := map[string]any{"placement": placement, "thingDef": "Silver", "count": count}
		for k, v := range extra {
			m[k] = v
		}
		return []any{m}
	}
	part := func(m map[string]any) []any { return []any{m} }
	for name, edit := range map[string]func(spec map[string]any){
		"pawn count above 10": func(s map[string]any) { s["colonistCount"] = 11 },
		"zero thing count":    func(s map[string]any) { s["startingThings"] = thing("start_with", 0, nil) },
		"huge thing count":    func(s map[string]any) { s["startingThings"] = thing("start_with", 1e9, nil) },
		"bad placement":       func(s map[string]any) { s["startingThings"] = thing("nowhere", 1, nil) },
		"roofed on start_with": func(s map[string]any) {
			s["startingThings"] = thing("start_with", 1, map[string]any{"allowRoofed": true})
		},
		"trait degree": func(s map[string]any) {
			s["forcedTraits"] = part(map[string]any{"trait": "T", "degree": 11, "chance": 1, "context": "all"})
		},
		"trait chance above 1": func(s map[string]any) {
			s["forcedTraits"] = part(map[string]any{"trait": "T", "degree": 0, "chance": 1.5, "context": "all"})
		},
		"trait chance missing": func(s map[string]any) {
			s["forcedTraits"] = part(map[string]any{"trait": "T", "degree": 0, "context": "all"})
		},
		"negative chance": func(s map[string]any) { s["naked"] = part(map[string]any{"chance": -0.1, "context": "all"}) },
		"bad context":     func(s map[string]any) { s["naked"] = part(map[string]any{"chance": 1, "context": "everyone"}) },
		"need level above 1": func(s map[string]any) {
			s["setNeedLevels"] = part(map[string]any{"need": "Food", "level": 2, "chance": 1, "context": "all"})
		},
		"hediff severity": func(s map[string]any) {
			s["forcedHediffs"] = part(map[string]any{"hediff": "Flu", "severity": 1.5, "chance": 1, "context": "all"})
		},
		"duplicate condition": func(s map[string]any) { s["permaGameConditions"] = []any{"ColdSnap", "ColdSnap"} },
		"empty building":      func(s map[string]any) { s["disallowedBuildings"] = []any{""} },
		"bad faction":         func(s map[string]any) { s["playerFaction"] = "pirates" },
		"bad arrival":         func(s map[string]any) { s["arrivalMethod"] = "teleport" },
	} {
		t.Run(name, func(t *testing.T) {
			s, f, token := lifecycleAPI(t, "automate")
			body := newColonyBodyWith(t, func(spec, _ map[string]any) { edit(spec) })
			out := playerCall(s, "POST", "/api/lifecycle/new", body, token)
			if out.Code != 400 || f.calls != 0 {
				t.Fatal(out.Code, out.Body.String())
			}
		})
	}
}

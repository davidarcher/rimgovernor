package nativeaccept

import (
	"fmt"
	"os"
	"sort"
	"strings"
)

// UnknownKeyProbe is the sentinel argument name compatibility acceptance sends to
// prove (or fail to prove) that a legacy home/* binder actually rejects unknown
// arguments.
const UnknownKeyProbe = "n01UnknownArgumentProbe"

// GameComponents are the native save components a fresh colony's <game> scope must
// carry exactly once.
var GameComponents = map[string]bool{}

// MapComponent is the native save component a loaded map's scope must carry exactly
// once.
const MapComponent = "HomeBridge.BridgeTools.HomeCoverageState"

func init() {
	for _, name := range []string{
		"ColonyIdentity", "ConstructionLineageState", "GearOwnership", "HaulTrackingState",
		"MiningState", "ProductionPolicyState", "RecoveryAreas", "WallRemovalState",
	} {
		GameComponents["HomeBridge.BridgeTools."+name] = true
	}
}

// PageNames extracts one discovery page's tool names and next cursor: a page missing an explicit
// tools list, a row missing its native gabpName, or a non-string cursor is a
// malformed reply, never an empty/absent page.
func PageNames(page map[string]any) ([]string, string, error) {
	rows, ok := page["tools"].([]any)
	if !ok {
		return nil, "", fmt.Errorf("discovery tools must be an explicit list")
	}
	names := make([]string, 0, len(rows))
	for _, raw := range rows {
		entry, ok := AsMap(raw)
		if !ok {
			return nil, "", fmt.Errorf("discovery row must be an object")
		}
		name, ok := entry["gabpName"].(string)
		if !ok || name == "" || !strings.Contains(name, "/") {
			return nil, "", fmt.Errorf("discovery row requires its native gabpName")
		}
		names = append(names, name)
	}
	cursor := ""
	if raw, present := page["nextCursor"]; present && raw != nil {
		s, ok := raw.(string)
		if !ok {
			return nil, "", fmt.Errorf("discovery cursor must be a string or null")
		}
		cursor = s
	}
	return names, cursor, nil
}

func numericStrict(v any) (float64, bool) {
	switch n := v.(type) {
	case float64:
		return n, true
	case int:
		return float64(n), true
	}
	return 0, false
}

// VerifyReload asserts a reload/restart preserved colony identity and map while
// rotating the load token and advancing at most the documented one load-boundary
// tick, and that the reloaded clock is paused at exactly the reported tick.
func VerifyReload(before, after, clock map[string]any) error {
	colonyID, ok := before["colonyId"].(string)
	if !ok || colonyID == "" {
		return fmt.Errorf("missing saved colony identity")
	}
	if !DeepEqual(after["colonyId"], before["colonyId"]) {
		return fmt.Errorf("colony identity changed on reload")
	}
	beforeToken, ok := before["loadToken"].(string)
	if !ok || beforeToken == "" {
		return fmt.Errorf("missing original load token")
	}
	afterToken, ok := after["loadToken"].(string)
	if !ok || afterToken == "" {
		return fmt.Errorf("missing reloaded token")
	}
	if afterToken == beforeToken {
		return fmt.Errorf("load token did not rotate")
	}
	if !DeepEqual(after["mapId"], before["mapId"]) {
		return fmt.Errorf("reload changed active map")
	}
	tickBefore, beforeOK := numericStrict(before["tick"])
	tickAfter, afterOK := numericStrict(after["tick"])
	if !beforeOK || !afterOK {
		return fmt.Errorf("missing native ticks")
	}
	// Existing paired-resume acceptance permits one load-boundary tick. Observation
	// within each loaded session must remain at the exact same paused tick.
	if diff := tickAfter - tickBefore; diff < 0 || diff > 1 {
		return fmt.Errorf("reload advanced beyond the load-boundary tick allowance")
	}
	paused, pausedOK := AsBool(clock["paused"])
	ticksGame, ticksOK := numericStrict(clock["ticksGame"])
	if !pausedOK || !paused || !ticksOK || ticksGame != tickAfter {
		return fmt.Errorf("reload is not paused at the observed tick")
	}
	return nil
}

// BinderObservation classifies a legacy home/* tool's reply to an unknown-argument
// probe. It
// never claims strict validation is proven: a legacy binder that silently drops an
// unknown key looks identical to one that never received it.
func BinderObservation(data map[string]any, isError bool) map[string]any {
	outcome := "unknown-key-not-reported-legacy-binder-may-drop-it"
	if success, ok := AsBool(data["success"]); isError || (ok && !success) {
		outcome = "request-refused-inspect-raw-error"
	} else {
		for _, v := range AsSlice(data["unknownArguments"]) {
			if AsString(v) == UnknownKeyProbe {
				outcome = "unknown-key-reported"
				break
			}
		}
	}
	return map[string]any{
		"outcome": outcome, "strict_unknown_key_validation_proven": false,
		"scope": "Observed reply only; an empty list or success does not prove unknown-key rejection.",
	}
}

// ComponentCensus counts each native HomeBridge.* save component per scope (the
// overall game, and each loaded map): every component the
// unified mod owns must appear exactly once per scope, never duplicated or absent.
func ComponentCensus(path string) (map[string]map[string]int, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", path, err)
	}
	_, root, err := parseXML(data)
	if err != nil {
		return nil, err
	}
	game := root.find("game")
	if game == nil {
		return nil, fmt.Errorf("save has no game")
	}
	type scopeGroup struct {
		scope string
		elem  *xmlElem
	}
	groups := []scopeGroup{{"game", game.find("components")}}
	maps := game.find("maps").findAll("li")
	if len(maps) == 0 {
		return nil, fmt.Errorf("save has no maps")
	}
	for index, mapElem := range maps {
		groups = append(groups, scopeGroup{fmt.Sprintf("map-%d", index), mapElem.find("components")})
	}
	census := map[string]map[string]int{}
	for _, group := range groups {
		if group.elem == nil {
			return nil, fmt.Errorf("save has no component list for %s", group.scope)
		}
		counts := map[string]int{}
		for _, child := range group.elem.children() {
			name := strings.SplitN(child.attr("Class"), ",", 2)[0]
			counts[name]++
		}
		native := map[string]int{}
		for name, count := range counts {
			if strings.HasPrefix(name, "HomeBridge.") {
				native[name] = count
			}
		}
		for name, count := range native {
			if count != 1 {
				return nil, fmt.Errorf("duplicate native components in %s: %s=%d", group.scope, name, count)
			}
		}
		expected := GameComponents
		if group.scope != "game" {
			expected = map[string]bool{MapComponent: true}
		}
		var missing []string
		for name := range expected {
			if _, ok := native[name]; !ok {
				missing = append(missing, name)
			}
		}
		if len(missing) > 0 {
			sort.Strings(missing)
			return nil, fmt.Errorf("missing native components in %s: %v", group.scope, missing)
		}
		census[group.scope] = native
	}
	return census, nil
}

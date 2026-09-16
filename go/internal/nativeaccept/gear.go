package nativeaccept

import (
	"fmt"
	"math"
	"sort"
)

// AuditGear compares paused canonical gear planning facts against the native
// upkeep read: the typed
// gear census must exactly match the native pawn set, each pawn's snapshot must
// carry the colony's own context and the native loadout token, its deficit flag
// must be an explicit boolean matching the native fact, and its eligible
// candidates and replacement needs must reproduce the native set exactly.
func AuditGear(colony, legacy map[string]any) (map[string]any, error) {
	context, _ := AsMap(colony["context"])
	planning, _ := AsMap(colony["planning"])
	observed, _ := AsMap(planning["observed"])
	gear, _ := AsMap(observed["gear"])
	if !DeepEqual(gear["context"], context) {
		return nil, fmt.Errorf("gear context differs from colony")
	}
	legacySuccess, _ := AsBool(legacy["success"])
	if !legacySuccess {
		return nil, fmt.Errorf("legacy read was not successful")
	}
	if int(AsNumber(legacy["tick"])) != int(AsNumber(context["tick"])) {
		return nil, fmt.Errorf("legacy tick does not match colony context tick")
	}
	identity, _ := AsMap(context["identity"])
	if !DeepEqual(legacy["mapId"], identity["mapId"]) {
		return nil, fmt.Errorf("legacy mapId does not match colony identity")
	}
	gearCompleteness, _ := AsMap(gear["completeness"])
	gearPage, _ := AsMap(gearCompleteness["page"])
	if complete, ok := AsBool(gearPage["complete"]); !ok || !complete {
		return nil, fmt.Errorf("gear completeness page is not complete")
	}
	gearPawns := AsSlice(gear["pawns"])
	if int(AsNumber(gearCompleteness["returned"])) != len(gearPawns) || len(gearPawns) != int(AsNumber(colony["colonistCount"])) {
		return nil, fmt.Errorf("gear completeness returned count does not match pawns or colonistCount")
	}
	legacyPawns := AsSlice(legacy["pawns"])
	native := map[string]map[string]any{}
	for _, raw := range legacyPawns {
		row, ok := AsMap(raw)
		if !ok {
			return nil, fmt.Errorf("legacy pawn row must be an object")
		}
		native[AsString(row["pawn"])] = row
	}
	typed := map[string]map[string]any{}
	for _, raw := range gearPawns {
		row, ok := AsMap(raw)
		if !ok {
			return nil, fmt.Errorf("typed gear pawn row must be an object")
		}
		pawn, _ := AsMap(row["pawn"])
		typed[AsString(pawn["id"])] = row
	}
	if len(native) != len(legacyPawns) || len(typed) != len(gearPawns) {
		return nil, fmt.Errorf("duplicate pawn id in native or typed gear census")
	}
	if !sameKeySet(native, typed) {
		return nil, fmt.Errorf("gear census differs")
	}
	candidates, deficits, blocked := 0, 0, 0
	for pawnID, row := range typed {
		ref := native[pawnID]
		snapshot, _ := AsMap(row["snapshot"])
		if !DeepEqual(snapshot["context"], gear["context"]) {
			return nil, fmt.Errorf("pawn %s snapshot context differs from gear context", pawnID)
		}
		if AsString(snapshot["entityId"]) != pawnID {
			return nil, fmt.Errorf("pawn %s snapshot entityId mismatch", pawnID)
		}
		if AsString(snapshot["token"]) != AsString(ref["loadout"]) {
			return nil, fmt.Errorf("pawn %s snapshot token does not match native loadout", pawnID)
		}
		deficit, ok := AsBool(row["deficit"])
		refDeficit, refOK := AsBool(ref["deficit"])
		if !ok || !refOK || deficit != refDeficit {
			return nil, fmt.Errorf("pawn %s deficit flag does not match native fact", pawnID)
		}
		if !DeepEqual(row["blocker"], ref["blocker"]) {
			return nil, fmt.Errorf("pawn %s blocker does not match native fact", pawnID)
		}
		rowCompleteness, _ := AsMap(row["completeness"])
		rowPage, _ := AsMap(rowCompleteness["page"])
		if complete, ok := AsBool(rowPage["complete"]); !ok || !complete {
			return nil, fmt.Errorf("pawn %s completeness page is not complete", pawnID)
		}
		rowCandidates := AsSlice(row["candidates"])
		refCandidates := AsSlice(ref["candidates"])
		actual := map[string]map[string]any{}
		for _, raw := range rowCandidates {
			candidate, ok := AsMap(raw)
			if !ok {
				return nil, fmt.Errorf("pawn %s typed candidate must be an object", pawnID)
			}
			item, _ := AsMap(candidate["item"])
			thing, _ := AsMap(item["thing"])
			actual[AsString(thing["id"])] = candidate
		}
		expected := map[string]map[string]any{}
		for _, raw := range refCandidates {
			candidate, ok := AsMap(raw)
			if !ok {
				return nil, fmt.Errorf("pawn %s native candidate must be an object", pawnID)
			}
			expected[AsString(candidate["target"])] = candidate
		}
		if len(actual) != len(rowCandidates) || len(actual) != int(AsNumber(rowCompleteness["returned"])) {
			return nil, fmt.Errorf("pawn %s candidate count does not match completeness", pawnID)
		}
		if !sameKeySet(actual, expected) {
			return nil, fmt.Errorf("pawn %s eligible candidates differ", pawnID)
		}
		for target, candidate := range actual {
			baseline := expected[target]
			candidateItem, _ := AsMap(candidate["item"])
			candidateThing, _ := AsMap(candidateItem["thing"])
			baselineGear, _ := AsMap(baseline["gear"])
			if AsString(candidateThing["defName"]) != AsString(baselineGear["defName"]) {
				return nil, fmt.Errorf("pawn %s candidate %s defName mismatch", pawnID, target)
			}
			if !isCloseFloatTol(AsNumber(candidate["gain"]), AsNumber(baseline["gain"]), 1e-6, 1e-7) {
				return nil, fmt.Errorf("pawn %s candidate %s gain does not match native gain", pawnID, target)
			}
			candidateApparel, _ := AsBool(candidateItem["apparel"])
			candidateWeapon, _ := AsBool(candidateItem["weapon"])
			kind := AsString(baseline["kind"])
			if candidateApparel != (kind == "apparel") {
				return nil, fmt.Errorf("pawn %s candidate %s apparel flag does not match native kind", pawnID, target)
			}
			if candidateWeapon != (kind == "weapon") {
				return nil, fmt.Errorf("pawn %s candidate %s weapon flag does not match native kind", pawnID, target)
			}
		}
		if !DeepEqual(sortedReplacementNeeds(AsSlice(row["replacementNeeds"])), sortedReplacementNeeds(AsSlice(ref["replacementNeeds"]))) {
			return nil, fmt.Errorf("pawn %s replacement needs differ", pawnID)
		}
		if deficit {
			deficits++
		}
		if row["blocker"] != nil {
			blocked++
		}
		candidates += len(actual)
	}
	return map[string]any{
		"pawns": len(typed), "deficits": deficits, "blocked": blocked, "candidates": candidates,
		"same_tick_native_parity": true,
	}, nil
}

func isCloseFloatTol(a, b, relTol, absTol float64) bool {
	diff := math.Abs(a - b)
	return diff <= math.Max(relTol*math.Max(math.Abs(a), math.Abs(b)), absTol)
}

func sameKeySet(a, b map[string]map[string]any) bool {
	if len(a) != len(b) {
		return false
	}
	for k := range a {
		if _, ok := b[k]; !ok {
			return false
		}
	}
	return true
}

// sortedReplacementNeeds normalizes a replacementNeeds list into a sorted slice of
// (defName, stuff, reason) triples:
// a missing/empty "stuff" is treated the same as an absent one.
func sortedReplacementNeeds(rows []any) [][3]string {
	out := make([][3]string, 0, len(rows))
	for _, raw := range rows {
		row, ok := AsMap(raw)
		if !ok {
			continue
		}
		out = append(out, [3]string{AsString(row["defName"]), AsString(row["stuff"]), AsString(row["reason"])})
	}
	sort.Slice(out, func(i, j int) bool {
		a, b := out[i], out[j]
		for k := 0; k < 3; k++ {
			if a[k] != b[k] {
				return a[k] < b[k]
			}
		}
		return false
	})
	return out
}

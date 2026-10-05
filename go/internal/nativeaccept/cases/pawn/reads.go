// The pawn/reads case reads typed pawn rows from a fresh debug game and
// asserts the typed pawn read facts against each other.
package pawn

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "pawn/reads",
		Scope:  "Fresh native pawn read facts, exact filters, explicit detail presence, bounded refusals and paused identity/tick invariance. Draft-control snapshot/claim read validation only; no pawn operation or health/settings CAS acceptance.",
		Start:  cases.LabStart(),
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	// The lab holds only its colonists: add an animal so the census reads a
	// non-colonist pawn too.
	c := nativeaccept.LabMapSize / 2
	if _, _, err := nativeaccept.LabSpawn(ctx, h, nativeaccept.LabThing{Def: "Muffalo", X: c + 4, Z: c, Unowned: true}); err != nil {
		return err
	}
	if !nativeaccept.Contains(s.Names(), "rimgovernor/observations_list_pawns") {
		return fmt.Errorf("missing rimgovernor/observations_list_pawns in discovery")
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := nativeaccept.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := nativeaccept.AsBool(loaded["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	loadedContext, _ := loaded["context"].(map[string]any)
	identity, _ := loadedContext["identity"].(map[string]any)
	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}

	read := func(label string, fields map[string]any) ([]any, map[string]any, error) {
		request := nativeaccept.Merge(scope, fields)
		reply, err := h.Wire(ctx, label, "observations_list_pawns", request)
		if err != nil {
			return nil, nil, err
		}
		_, observed, err := nativeaccept.Outcome(reply, "observed")
		if err != nil {
			return nil, nil, fmt.Errorf("%s: %w", label, err)
		}
		if observedContext, _ := observed["context"].(map[string]any); observedContext == nil || !nativeaccept.DeepEqual(observedContext, loadedContext) {
			return nil, nil, fmt.Errorf("%s: context drifted mid-run", label)
		}
		rows := nativeaccept.AsSlice(observed["pawns"])
		for _, raw := range rows {
			row, _ := nativeaccept.AsMap(raw)
			if err := draftControl(row, observed["context"]); err != nil {
				return nil, nil, fmt.Errorf("%s: %w", label, err)
			}
		}
		return rows, observed, nil
	}

	// The unfiltered, all-details default request (details omitted entirely)
	// asks the native tool to populate every detail family for every pawn on
	// the map, per its documented default ("All detail families default
	// requested" in the rimgovernor/observations_list_pawns tool description,
	// integrations/rimgovernor-native/src/Bridge/Protocol/NativePawnObservationTools.cs:20).
	// The reply carries no envelope size cap any more; a LIMIT_EXCEEDED
	// refusal from a per-collection bound is still tolerated here, and any
	// other unavailable reply is rejected (contracts/native-operation-variants.md).
	defaultReply, err := h.Wire(ctx, "default-pawns", "observations_list_pawns", scope)
	if err != nil {
		return err
	}
	if _, observedPresent, _ := nativeaccept.Outcome(defaultReply, "observed"); observedPresent == nil {
		if reason, ok := nativeaccept.UnavailableReason(defaultReply); !ok || reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
			return fmt.Errorf("default-pawns: expected observed or UNAVAILABLE_REASON_LIMIT_EXCEEDED, got %q", reason)
		}
	}

	// Prove the happy path with a bounded, filtered read over the same
	// population: every detail family explicitly disabled keeps the reply
	// well under the envelope while still exercising the core pawn facts,
	// CAS-snapshot invariants that draftControl checks.
	baseline, _, err := read("default-pawns-bounded", map[string]any{
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if len(baseline) <= 3 {
		return fmt.Errorf("expected more than 3 fresh pawns, found %d", len(baseline))
	}
	if err := requireCore(baseline); err != nil {
		return err
	}

	colonists, _, err := read("colonists", map[string]any{"filter": map[string]any{"colonist": true, "humanlike": true, "animal": false}})
	if err != nil {
		return err
	}
	if len(colonists) != 3 {
		return fmt.Errorf("expected exactly 3 colonists, found %d", len(colonists))
	}
	if err := requireDetails(colonists); err != nil {
		return err
	}

	firstColonist, _ := nativeaccept.AsMap(colonists[0])
	firstPawn, _ := nativeaccept.AsMap(firstColonist["pawn"])
	target := nativeaccept.AsString(firstPawn["id"])
	exact, _, err := read("exact-id", map[string]any{"filter": map[string]any{"ids": []any{target}, "colonist": true, "animal": false}})
	if err != nil {
		return err
	}
	if len(exact) != 1 {
		return fmt.Errorf("exact id filter did not return exactly one pawn")
	}
	contradictory, _, err := read("contradictory-filter", map[string]any{"filter": map[string]any{"ids": []any{target}, "colonist": false}})
	if err != nil {
		return err
	}
	if len(contradictory) != 0 {
		return fmt.Errorf("contradictory filter unexpectedly matched a pawn")
	}
	unknown, _, err := read("unknown-id", map[string]any{"filter": map[string]any{"ids": []any{"missing-pawn-id"}}})
	if err != nil {
		return err
	}
	if len(unknown) != 0 {
		return fmt.Errorf("unknown id filter unexpectedly matched a pawn")
	}
	// As with default-pawns above, omitting details requests every detail family
	// for every matched pawn; a fresh map's animal population can carry enough
	// hediff/needs/social data. Only pawn.snapshot is checked
	// below, which does not depend on Details, so keep this small the same way.
	animals, _, err := read("animals", map[string]any{
		"filter": map[string]any{"colonist": false, "animal": true},
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if len(animals) == 0 {
		return fmt.Errorf("expected at least one animal in a fresh tribal colony")
	}
	for _, raw := range animals {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		if err := RequireSnapshotStrict(pawn["snapshot"]); err != nil {
			return fmt.Errorf("animal row missing exact target snapshot: %w", err)
		}
	}
	disabled, _, err := read("details-disabled", map[string]any{
		"filter": map[string]any{"ids": []any{target}},
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if len(disabled) != 1 {
		return fmt.Errorf("details-disabled did not return exactly one pawn")
	}
	disabledRow, _ := nativeaccept.AsMap(disabled[0])
	for _, section := range []string{"needs", "health", "equipment", "biography", "settings", "social", "animalState"} {
		if _, present := disabledRow[section]; present {
			return fmt.Errorf("disabled section %s unexpectedly present", section)
		}
	}
	// drafted:false,downed:false can match nearly the whole population; only the
	// drafted/downed booleans are checked below, so keep this small like
	// default-pawns above.
	knownFalse, _, err := read("known-false", map[string]any{
		"filter": map[string]any{"drafted": false, "downed": false},
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	for _, raw := range knownFalse {
		row, _ := nativeaccept.AsMap(raw)
		drafted, _ := nativeaccept.AsBool(row["drafted"])
		downed, _ := nativeaccept.AsBool(row["downed"])
		if drafted || downed {
			return fmt.Errorf("known-false filter returned a drafted or downed pawn")
		}
	}
	invalidCases := []struct {
		label  string
		change map[string]any
	}{
		{"duplicate-id", map[string]any{"filter": map[string]any{"ids": []any{target, target}}}},
		{"negative-distance", map[string]any{"filter": map[string]any{"withinColonistDistance": -1}}},
	}
	for _, c := range invalidCases {
		reply, err := h.Wire(ctx, c.label, "observations_list_pawns", nativeaccept.Merge(scope, c.change))
		if err != nil {
			return err
		}
		code, ok := nativeaccept.FailureCode(reply)
		if !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
	}
	staleScope := map[string]any{"scope": map[string]any{"expectedIdentity": nativeaccept.Merge(identity, map[string]any{"loadToken": "stale-load"})}}
	staleReply, err := h.Wire(ctx, "stale-identity", "observations_list_pawns", staleScope)
	if err != nil {
		return err
	}
	if code, ok := nativeaccept.FailureCode(staleReply); !ok || code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("expected FAILURE_CODE_STALE_IDENTITY for a stale load token")
	}
	identityAfter, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, finalLoaded, err := nativeaccept.Outcome(identityAfter, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := nativeaccept.AsBool(finalLoaded["paused"]); !paused {
		return fmt.Errorf("game unexpectedly resumed during the read pass")
	}
	if finalContext, _ := finalLoaded["context"].(map[string]any); !nativeaccept.DeepEqual(finalContext, loadedContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	// Repeat the exact same request that produced baseline (nil would compare an
	// all-details reply against baseline's all-details-disabled shape, which can
	// never match).
	repeat, _, err := read("repeat-default", map[string]any{
		"details": map[string]any{
			"needs": false, "health": false, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false,
		},
	})
	if err != nil {
		return err
	}
	if !nativeaccept.DeepEqual(repeat, baseline) {
		return fmt.Errorf("repeating the default read returned a different snapshot")
	}
	report["context"] = loadedContext
	report["pawns"] = len(baseline)
	report["colonists"] = len(colonists)
	report["animals"] = len(animals)
	return nil
}

// snapshotIssueReason returns the unavailable reason of the row's pawn.snapshot issue.
func snapshotIssueReason(row map[string]any) string {
	for _, raw := range nativeaccept.AsSlice(row["issues"]) {
		issue, _ := nativeaccept.AsMap(raw)
		if nativeaccept.AsString(issue["field"]) == "pawn.snapshot" {
			unavailable, _ := nativeaccept.AsMap(issue["unavailable"])
			return nativeaccept.AsString(unavailable["reason"])
		}
	}
	return ""
}

// draftControl validates a pawn row's snapshot invariants. The health
// section carries its own populated CAS snapshot (PawnHealth.snapshot=19), and social is
// now a populated PawnSocial block: earlier acceptance runs predated both and
// asserted their absence, which this port corrects rather than preserves.
func draftControl(row map[string]any, context any) error {
	pawn, _ := nativeaccept.AsMap(row["pawn"])
	if err := nativeaccept.RequireIdentifier(pawn["id"]); err != nil {
		return fmt.Errorf("pawn id: %w", err)
	}
	if _, present := pawn["snapshot"]; !present {
		reason := snapshotIssueReason(row)
		if reason != "UNAVAILABLE_REASON_NOT_APPLICABLE" && reason != "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING" {
			return fmt.Errorf("a row without a pawn snapshot needs a pawn.snapshot issue, got reason %q", reason)
		}
		contextMap, _ := nativeaccept.AsMap(context)
		identity, _ := nativeaccept.AsMap(contextMap["identity"])
		dead, _ := nativeaccept.AsBool(row["dead"])
		animal, _ := nativeaccept.AsBool(row["animal"])
		onCurrentMap := nativeaccept.AsNumber(pawn["mapId"]) == nativeaccept.AsNumber(identity["mapId"])
		if animal && !dead && onCurrentMap && reason != "UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING" {
			return fmt.Errorf("a live current-map animal cannot be blanket %q", reason)
		}
	} else {
		snapshot, _ := nativeaccept.AsMap(pawn["snapshot"])
		if err := RequireSnapshotStrict(snapshot); err != nil {
			return fmt.Errorf("draftable pawn missing a populated snapshot: %w", err)
		}
		if nativeaccept.AsString(snapshot["entityId"]) != nativeaccept.AsString(pawn["id"]) {
			return fmt.Errorf("snapshot entityId does not match the row's pawn id")
		}
		if !nativeaccept.DeepEqual(snapshot["context"], context) {
			return fmt.Errorf("snapshot context does not match the read context")
		}
	}
	// "health" carries a populated CAS snapshot: NativePawnDetails.Health via
	// NativeObservationSnapshot.Snapshot.
	for _, section := range []string{"health"} {
		sectionValue, present := row[section]
		if !present {
			continue
		}
		sectionMap, _ := nativeaccept.AsMap(sectionValue)
		if err := RequireSnapshotStrict(sectionMap["snapshot"]); err != nil {
			return fmt.Errorf("%s section missing a populated CAS snapshot: %w", section, err)
		}
	}
	if _, present := row["social"]; present {
		if err := nativeaccept.RequireSocial(row); err != nil {
			return err
		}
	}
	return nil
}

// RequireSnapshotStrict is RequireSnapshot with a pawn-acceptance-specific message.
func RequireSnapshotStrict(v any) error { return nativeaccept.RequireSnapshot(v) }

// requireCore checks every core classification of every typed row is an
// explicit boolean.
func requireCore(typed []any) error {
	if len(typed) == 0 {
		return fmt.Errorf("typed pawn read returned no rows")
	}
	for _, raw := range typed {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		if nativeaccept.AsString(pawn["id"]) == "" || nativeaccept.AsString(pawn["defName"]) == "" {
			return fmt.Errorf("typed pawn row without an id and defName: %#v", pawn)
		}
		for _, key := range []string{"colonist", "freeColonist", "prisoner", "animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted"} {
			if _, ok := nativeaccept.AsBool(row[key]); !ok {
				return fmt.Errorf("%s must be an explicit boolean for pawn %s, found %#v", key, pawn["id"], row[key])
			}
		}
	}
	return nil
}

// requireDetails checks each colonist's health section carries a populated
// CAS snapshot and its social section is well formed.
func requireDetails(typed []any) error {
	for _, raw := range typed {
		row, _ := nativeaccept.AsMap(raw)
		pawn, _ := nativeaccept.AsMap(row["pawn"])
		health, _ := nativeaccept.AsMap(row["health"])
		if err := RequireSnapshotStrict(health["snapshot"]); err != nil {
			return fmt.Errorf("health section missing populated snapshot for %s: %w", pawn["id"], err)
		}
		if err := nativeaccept.RequireSocial(row); err != nil {
			return fmt.Errorf("colonist %s: %w", pawn["id"], err)
		}
	}
	return nil
}

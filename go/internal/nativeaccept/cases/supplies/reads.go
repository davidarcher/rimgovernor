// The supplies/reads case proves typed supply-stock reads are internally
// consistent, for both held and spawned-only ownership modes, on the lab.
package supplies

import (
	"context"
	"fmt"
	"strconv"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "supplies/reads",
		Scope:  "Fresh typed supply census: quantities, instances and holders agree within each row for held and spawned-only ownership over spawned WoodLog and Steel; no gameplay orders.",
		Start:  cases.LabStart(),
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, names := s.Harness(), s.Names()
	// The lab is bare: lay the starting materials the census reads.
	c := na.LabMapSize / 2
	for i, def := range []string{"WoodLog", "Steel"} {
		if _, _, err := na.LabSpawn(ctx, h, na.LabThing{Def: def, X: c + 3 + i, Z: c + 3, Count: 50}); err != nil {
			return err
		}
	}
	if !na.Contains(names, "rimgovernor/observations_list_supplies") {
		return fmt.Errorf("missing rimgovernor/observations_list_supplies in discovery")
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(before["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)

	for _, includeHeld := range []bool{true, false} {
		label := "spawned"
		if includeHeld {
			label = "held"
		}
		selected := []string{"WoodLog", "Steel"}
		defNames := make([]any, len(selected))
		for i, s := range selected {
			defNames[i] = s
		}
		request := map[string]any{
			"scope":  map[string]any{"expectedIdentity": identity},
			"filter": map[string]any{"defNames": defNames, "ownership": "STOCK_OWNERSHIP_ALL", "includeHeld": includeHeld},
		}
		reply, err := h.Wire(ctx, label+"-typed-census", "observations_list_supplies", request)
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		if observedContext, _ := observed["context"].(map[string]any); !na.DeepEqual(observedContext, beforeContext) {
			return fmt.Errorf("%s: context drifted mid-run", label)
		}
		rows := na.AsSlice(observed["stocks"])
		if len(rows) != len(selected) {
			return fmt.Errorf("%s: expected %d stock rows, found %d", label, len(selected), len(rows))
		}
		for _, raw := range rows {
			row, _ := na.AsMap(raw)
			if err := checkStock(row, identity, includeHeld); err != nil {
				return fmt.Errorf("%s: %w", label, err)
			}
		}
		report[label+"_definitions_checked"] = selected
	}

	defaultRequest := map[string]any{
		"scope":  map[string]any{"expectedIdentity": identity},
		"filter": map[string]any{"defNames": []any{"WoodLog", "Steel"}},
	}
	defaultReply, err := h.Wire(ctx, "default-ownership-held", "observations_list_supplies", defaultRequest)
	if err != nil {
		return err
	}
	_, defaultObserved, err := na.Outcome(defaultReply, "observed")
	if err != nil {
		return err
	}
	defaultRows := na.AsSlice(defaultObserved["stocks"])
	for _, raw := range defaultRows {
		row, _ := na.AsMap(raw)
		if na.AsNumber(row["ours"]) <= 0 {
			return fmt.Errorf("default read: expected a positive ours quantity")
		}
		if _, ok := row["carried"]; !ok {
			return fmt.Errorf("default read: carried should be populated (includeHeld defaults true)")
		}
		if _, ok := row["inContainer"]; !ok {
			return fmt.Errorf("default read: inContainer should be populated (includeHeld defaults true)")
		}
	}

	if err := checkWeaponClasses(ctx, h, identity, report); err != nil {
		return err
	}

	invalidCases := []struct {
		label   string
		request map[string]any
	}{
		{"bad-category", map[string]any{"filter": map[string]any{"category": "unknown"}}},
	}
	for _, c := range invalidCases {
		request := na.Merge(c.request, map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
		reply, err := h.Wire(ctx, c.label, "observations_list_supplies", request)
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
	}
	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, after, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if pausedAfter, _ := na.AsBool(after["paused"]); !pausedAfter {
		return fmt.Errorf("game unexpectedly resumed during the read pass")
	}
	if afterContext, _ := after["context"].(map[string]any); !na.DeepEqual(afterContext, beforeContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	return nil
}

// checkStock asserts one typed ResourceStock row is internally consistent:
// every quantity a canonical count, spawned plus held units equal to the
// total, one item per stack with its CAS snapshot, and holders summing to
// the held units.
func checkStock(row map[string]any, identity map[string]any, includeHeld bool) error {
	definition, _ := na.AsMap(row["definition"])
	if na.AsString(definition["defName"]) == "" {
		return fmt.Errorf("stock row without a definition: %v", row)
	}
	keys := []string{"units", "stacks", "ours", "oursUnforbidden", "forbidden", "otherFaction", "fogged", "reserved", "inStockpile", "inHomeArea"}
	if includeHeld {
		keys = append(keys, "carried", "inContainer", "traderStock")
	}
	values := map[string]int64{}
	for _, key := range keys {
		value, err := count(row[key])
		if err != nil {
			return fmt.Errorf("native stock %v.%s: %w", row["definition"], key, err)
		}
		values[key] = value
	}
	if values["units"] <= 0 {
		return fmt.Errorf("native stock %v has no units", row["definition"])
	}
	held := values["carried"] + values["inContainer"]
	spawned, err := count(row["spawned"])
	if err != nil {
		return fmt.Errorf("spawned for %v: %w", row["definition"], err)
	}
	units, err := count(row["units"])
	if err != nil {
		return fmt.Errorf("units for %v: %w", row["definition"], err)
	}
	if spawned+held != units {
		return fmt.Errorf("spawned+held does not equal units for %v", row["definition"])
	}
	playerFaction, err := count(row["playerFaction"])
	if err != nil {
		return fmt.Errorf("playerFaction for %v: %w", row["definition"], err)
	}
	if playerFaction > units || values["ours"] > units {
		return fmt.Errorf("playerFaction or ours exceeds units for %v", row["definition"])
	}
	stacks, err := count(row["stacks"])
	if err != nil {
		return fmt.Errorf("stacks for %v: %w", row["definition"], err)
	}
	items := na.AsSlice(row["items"])
	if int64(len(items)) != stacks {
		return fmt.Errorf("item count does not match stacks for %v", row["definition"])
	}
	// Not every loose supply item supports a CAS snapshot: NativeSupplyAllow.Snapshot
	// (integrations/rimgovernor-native/src/Bridge/Protocol/NativeSupplyAllow.cs:48)
	// returns null for a spawned item that fails Eligible (e.g. missing
	// CompForbiddable, as techprints and some other special items do), and
	// ListSupplies records that with an "items.snapshot"/UNSUPPORTED row issue
	// instead of failing the read (NativeSuppliesObservationTools.cs:72-73,262).
	// Held items always get a stateless CAS token from HeldSnapshot regardless, so
	// only a snapshot-less item backed by that documented gap is acceptable.
	rowAllowsSnapshotless := na.RequireIssueReason(na.AsSlice(row["issues"]), "items.snapshot", "UNAVAILABLE_REASON_UNSUPPORTED")
	seenIDs := map[string]bool{}
	for _, raw := range items {
		item, _ := na.AsMap(raw)
		id := na.AsString(item["id"])
		if id == "" {
			return fmt.Errorf("stock item missing id for %v", row["definition"])
		}
		if seenIDs[id] {
			return fmt.Errorf("duplicate stock item id %s for %v", id, row["definition"])
		}
		seenIDs[id] = true
		if int(na.AsNumber(item["mapId"])) != int(na.AsNumber(identity["mapId"])) {
			return fmt.Errorf("stock item %s has the wrong mapId", id)
		}
		if err := na.RequireSnapshot(item["snapshot"]); err != nil {
			if rowAllowsSnapshotless && item["snapshot"] == nil {
				continue // documented Eligible()-gated gap, not a Go-port bug
			}
			return fmt.Errorf("stock item %s missing a populated CAS snapshot: %w", id, err)
		}
	}
	holders := na.AsSlice(row["holders"])
	if includeHeld {
		total := int64(0)
		for _, raw := range holders {
			holder, _ := na.AsMap(raw)
			units, err := count(holder["units"])
			if err != nil {
				return fmt.Errorf("holder units for %v: %w", row["definition"], err)
			}
			if units <= 0 {
				return fmt.Errorf("holder with non-positive units for %v", row["definition"])
			}
			holderRef, _ := na.AsMap(holder["holder"])
			if na.AsString(holderRef["id"]) == "" {
				return fmt.Errorf("holder missing id for %v", row["definition"])
			}
			total += units
		}
		if total != held {
			return fmt.Errorf("holder unit total %v does not match held %v for %v", total, held, row["definition"])
		}
	} else {
		if len(holders) != 0 {
			return fmt.Errorf("holders populated without includeHeld for %v", row["definition"])
		}
		for _, field := range []string{"carried", "inContainer", "traderStock"} {
			if _, present := row[field]; present {
				return fmt.Errorf("%s unexpectedly present without includeHeld for %v", field, row["definition"])
			}
		}
		for _, field := range []string{"carried", "in_container", "trader_stock"} {
			if !na.RequireIssueReason(na.AsSlice(row["issues"]), field, "UNAVAILABLE_REASON_NOT_REQUESTED") {
				return fmt.Errorf("missing NOT_REQUESTED issue for %s on %v", field, row["definition"])
			}
		}
	}
	return nil
}

// checkWeaponClasses proves the "weapons" census carries each definition's
// Weapons-category membership and IsRangedWeapon/IsMeleeWeapon (#287): the
// category is IsWeapon, so a wood log lists as a melee equippable that is no
// weapon by trade, and the Core def naming (Bow_/Gun_/Pila ranged,
// MeleeWeapon_ melee) that the planner used to infer from is here only the
// oracle for what the flags must say.
func checkWeaponClasses(ctx context.Context, h *na.Harness, identity map[string]any, report map[string]any) error {
	request := map[string]any{
		"scope":  map[string]any{"expectedIdentity": identity},
		"filter": map[string]any{"category": "STOCK_CATEGORY_WEAPONS", "ownership": "STOCK_OWNERSHIP_ALL", "includeHeld": true},
	}
	reply, err := h.Wire(ctx, "weapons-census", "observations_list_supplies", request)
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	classes := map[string]string{}
	for _, raw := range na.AsSlice(observed["stocks"]) {
		row, _ := na.AsMap(raw)
		definition, _ := na.AsMap(row["definition"])
		name := na.AsString(definition["defName"])
		byTrade, tok := na.AsBool(row["weaponByTrade"])
		ranged, rok := na.AsBool(row["ranged"])
		melee, mok := na.AsBool(row["melee"])
		if !tok || !rok || !mok || ranged == melee {
			return fmt.Errorf("weapons census row %s lacks a consistent weapon class: weaponByTrade=%v ranged=%v melee=%v", name, row["weaponByTrade"], row["ranged"], row["melee"])
		}
		class := "makeshift"
		switch {
		case ranged && byTrade:
			class = "ranged"
		case melee && byTrade:
			class = "melee"
		}
		want := "makeshift"
		switch {
		case len(name) > 4 && (name[:4] == "Bow_" || name[:4] == "Gun_"), name == "Pila":
			want = "ranged"
		case len(name) > 12 && name[:12] == "MeleeWeapon_":
			want = "melee"
		}
		if class != want {
			return fmt.Errorf("weapons census row %s reports class %s, Core naming says %s", name, class, want)
		}
		classes[name] = class
	}
	if classes["WoodLog"] != "makeshift" {
		return fmt.Errorf("WoodLog missing from the weapons census as a makeshift equippable: %v", classes)
	}
	report["weapon_classes"] = classes
	return nil
}

// count asserts value is a canonical ProtoJSON int64 quantity string -- ASCII decimal
// digits only, no leading zero (except the literal "0"), no sign, and within int64
// range. A missing/absent
// quantity (nil, a bare number, or a malformed string) must never be read as zero.
func count(v any) (int64, error) {
	s, ok := v.(string)
	if !ok {
		return 0, fmt.Errorf("missing/noncanonical ProtoJSON quantity: %#v", v)
	}
	if s == "" {
		return 0, fmt.Errorf("missing/noncanonical ProtoJSON quantity: %q", s)
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("missing/noncanonical ProtoJSON quantity: %q", s)
		}
	}
	if s != "0" && s[0] == '0' {
		return 0, fmt.Errorf("noncanonical leading zero: %q", s)
	}
	n, err := strconv.ParseUint(s, 10, 64)
	if err != nil {
		return 0, fmt.Errorf("quantity is not a valid uint64: %q", s)
	}
	if n > 9223372036854775807 {
		return 0, fmt.Errorf("quantity exceeds int64 max: %q", s)
	}
	return int64(n), nil
}

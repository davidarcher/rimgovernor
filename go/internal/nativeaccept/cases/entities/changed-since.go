// The entities/changed-since case proves the native per-entity change
// tracking and tombstones behind changed_since_tick on the zones, buildings
// and bills list reads (issue #358): full reads of all three prime the
// tracker and stamp as_of_tick; the fixture then relabels and grows its
// stockpile zone, adds a bill to its crafting spot and spawns a wall, and
// a read of each family since the first read's tick lists exactly those
// entities with unchanged covering the rest, merging the delta over the
// first read reproducing a fresh full read row for row (drift 0). The
// fixture then deletes the zone and destroys the bench and the wall, and a
// read since the same tick names each in removed_ids. Finally the game
// tick is moved past the tombstone window and the same ask is refused as
// STALE while a full read still answers.
package entities

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "entities/changed-since",
		Scope: "Native per-entity change tracking: after full observations_list_zones, observations_list_buildings and " +
			"observations_read_bills reads, a relabelled and grown stockpile zone, a bench with a new bill and a spawned wall " +
			"come back from reads with changed_since_tick set to the first reads' tick, unchanged counts the rest, the delta " +
			"merged over the first read equals a fresh full read (drift 0), the deleted zone and destroyed bench and wall are " +
			"named in removed_ids, and an ask older than the 2500-tick tombstone window is refused as STALE while a full read answers.",
		Start:  cases.Fixture{Op: "test/entities_prepare"},
		Budget: 3 * time.Minute,
		Run:    run,
	})
}

// family is one entity list read as the case drives it: the method, the
// request without its since tick, and how a reply's rows are keyed.
type family struct {
	name, method string
	request      func() map[string]any
	rows         string
	id           func(row map[string]any) string
}

// listing is one reply: the rows by id (as canonical JSON), the removed
// ids, the unchanged count and the context tick.
type listing struct {
	rows      map[string]string
	removed   []string
	unchanged int
	tick      int64
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	prepared := s.Prepared()
	for _, required := range []string{"rimgovernor/observations_list_zones", "rimgovernor/observations_list_buildings", "rimgovernor/observations_read_bills", "test/entities_mutate"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery (fixture build required)", required)
		}
	}
	origin, _ := na.AsMap(prepared["origin"])
	ox, oz := int(na.AsNumber(origin["x"])), int(na.AsNumber(origin["z"]))
	zoneID, _ := prepared["zoneId"].(string)
	benchID, _ := prepared["benchId"].(string)
	if zoneID == "" || benchID == "" {
		return fmt.Errorf("prepare: missing fixture entities: %#v", prepared)
	}
	report["site"] = map[string]any{"x": ox, "z": oz, "zone": zoneID, "bench": benchID}
	scope := map[string]any{"expectedIdentity": identity}
	families := []family{
		{name: "zones", method: "observations_list_zones", rows: "zones", id: func(row map[string]any) string { id, _ := row["id"].(string); return id },
			request: func() map[string]any {
				return map[string]any{"scope": scope, "includeCells": false, "includeContents": false, "includeFilter": false, "page": map[string]any{"limit": 16}}
			}},
		{name: "buildings", method: "observations_list_buildings", rows: "buildings", id: func(row map[string]any) string {
			e, _ := na.AsMap(row["building"])
			id, _ := e["id"].(string)
			return id
		},
			request: func() map[string]any {
				return map[string]any{"scope": scope, "playerOnly": true, "category": "artificial", "page": map[string]any{"limit": 256}}
			}},
		{name: "bills", method: "observations_read_bills", rows: "benches", id: func(row map[string]any) string { e, _ := na.AsMap(row["bench"]); id, _ := e["id"].(string); return id },
			request: func() map[string]any { return map[string]any{"scope": scope, "page": map[string]any{"limit": 256}} }},
	}

	// read pages one family to the end; since 0 is a full read. A STALE
	// unavailability comes back as stale.
	read := func(label string, f family, since int64) (out listing, stale bool, err error) {
		out.rows = map[string]string{}
		cursor := ""
		for page := 0; ; page++ {
			request := f.request()
			if since > 0 {
				request["changedSinceTick"] = fmt.Sprint(since)
			}
			if cursor != "" {
				request["page"].(map[string]any)["cursor"] = cursor
			}
			reply, err := h.Wire(ctx, fmt.Sprintf("%s-%s-%d", label, f.name, page), f.method, request)
			if err != nil {
				return out, false, err
			}
			kind, observed, err := na.Outcome(reply, "observed", "unavailable")
			if err != nil {
				return out, false, err
			}
			if kind == "unavailable" {
				if reason, _ := observed["reason"].(string); reason == "UNAVAILABLE_REASON_STALE" && since > 0 {
					return out, true, nil
				}
				return out, false, fmt.Errorf("%s %s: unavailable: %#v", label, f.name, observed)
			}
			obsContext, _ := na.AsMap(observed["context"])
			tick := int64(na.AsNumber(obsContext["tick"]))
			if observed["asOfTick"] == nil || int64(na.AsNumber(observed["asOfTick"])) != tick {
				return out, false, fmt.Errorf("%s %s: as_of_tick %v is not the context tick %d", label, f.name, observed["asOfTick"], tick)
			}
			if page == 0 {
				out.tick = tick
				if observed["unchanged"] != nil {
					out.unchanged = int(na.AsNumber(observed["unchanged"]))
				}
				for _, raw := range na.AsSlice(observed["removedIds"]) {
					if id, ok := raw.(string); ok {
						out.removed = append(out.removed, id)
					}
				}
			} else if tick != out.tick {
				return out, false, fmt.Errorf("%s %s: page %d at tick %d, page 0 at %d", label, f.name, page, tick, out.tick)
			}
			for _, raw := range na.AsSlice(observed[f.rows]) {
				row, _ := na.AsMap(raw)
				id := f.id(row)
				if id == "" {
					return out, false, fmt.Errorf("%s %s: row without an id: %#v", label, f.name, row)
				}
				encoded, err := json.Marshal(row)
				if err != nil {
					return out, false, err
				}
				out.rows[id] = string(encoded)
			}
			completeness, _ := na.AsMap(observed["completeness"])
			pageInfo, _ := na.AsMap(completeness["page"])
			if complete, _ := na.AsBool(pageInfo["complete"]); complete {
				return out, false, nil
			}
			cursor, _ = pageInfo["nextCursor"].(string)
			if cursor == "" || page > 64 {
				return out, false, fmt.Errorf("%s %s: incomplete page without a cursor", label, f.name)
			}
		}
	}
	mutate := func(label, phase string) ([]string, int64, error) {
		result, err := h.Call(ctx, label, "test/entities_mutate", map[string]any{"originX": ox, "originZ": oz, "phase": phase})
		if err != nil {
			return nil, 0, err
		}
		if success, _ := na.AsBool(result["success"]); !success {
			return nil, 0, fmt.Errorf("%s: entities_mutate refused: %#v", label, result)
		}
		var ids []string
		for _, raw := range na.AsSlice(result["ids"]) {
			if id, ok := raw.(string); ok {
				ids = append(ids, id)
			}
		}
		return ids, int64(na.AsNumber(result["tick"])), nil
	}
	keys := func(rows map[string]string) []string {
		out := make([]string, 0, len(rows))
		for id := range rows {
			out = append(out, id)
		}
		sort.Strings(out)
		return out
	}
	defer func() {
		if _, _, err := mutate("cleanup", "cleanup"); err != nil {
			report["cleanup_error"] = err.Error()
		}
	}()

	// Full reads prime the tracker; the fixture's entities are listed.
	full0 := map[string]listing{}
	var tick0 int64
	for _, f := range families {
		l, _, err := read("full-before", f, 0)
		if err != nil {
			return err
		}
		if l.unchanged != 0 || len(l.removed) != 0 {
			return fmt.Errorf("full %s read: unchanged %d removed %v", f.name, l.unchanged, l.removed)
		}
		full0[f.name] = l
		tick0 = l.tick
	}
	if _, ok := full0["zones"].rows[zoneID]; !ok {
		return fmt.Errorf("full zones read omits the fixture zone %s: %v", zoneID, keys(full0["zones"].rows))
	}
	if _, ok := full0["buildings"].rows[benchID]; !ok {
		return fmt.Errorf("full buildings read omits the fixture bench %s: %v", benchID, keys(full0["buildings"].rows))
	}
	if _, ok := full0["bills"].rows[benchID]; !ok {
		return fmt.Errorf("full bills read omits the fixture bench %s: %v", benchID, keys(full0["bills"].rows))
	}
	report["tick_before"] = tick0
	report["counts"] = map[string]int{"zones": len(full0["zones"].rows), "buildings": len(full0["buildings"].rows), "bills": len(full0["bills"].rows)}

	// First round: change the zone and the bench's stack, spawn a wall.
	first, tickFirst, err := mutate("mutate-first", "first")
	if err != nil {
		return err
	}
	if tickFirst != tick0 || len(first) != 3 {
		return fmt.Errorf("the first round ran at tick %d (paused at %d) touching %v", tickFirst, tick0, first)
	}
	wallID := first[2]
	want := map[string][]string{"zones": {zoneID}, "buildings": {wallID}, "bills": {benchID}}
	drift := map[string]int{}
	for _, f := range families {
		delta, stale, err := read("delta-since-before", f, tick0)
		if err != nil || stale {
			return fmt.Errorf("delta %s since %d: stale=%v err=%v", f.name, tick0, stale, err)
		}
		// The bench's own row may relist alongside the wall (its bill
		// stack is not in the buildings row, but any changed field would
		// be honest); the must-list set is exact, the rest must be few.
		for _, id := range want[f.name] {
			if _, ok := delta.rows[id]; !ok {
				return fmt.Errorf("delta %s since %d omits %s: listed %v unchanged %d", f.name, tick0, id, keys(delta.rows), delta.unchanged)
			}
		}
		total := len(full0[f.name].rows)
		if f.name == "buildings" {
			total++ // the wall
		}
		if len(delta.rows)+delta.unchanged != total || len(delta.rows) > len(want[f.name])+1 {
			return fmt.Errorf("delta %s since %d lists %v, unchanged %d, of %d", f.name, tick0, keys(delta.rows), delta.unchanged, total)
		}
		full1, _, err := read("full-after", f, 0)
		if err != nil {
			return err
		}
		merged := map[string]string{}
		for id, row := range full0[f.name].rows {
			merged[id] = row
		}
		for id, row := range delta.rows {
			merged[id] = row
		}
		for _, id := range delta.removed {
			delete(merged, id)
		}
		d := 0
		for id, row := range full1.rows {
			if merged[id] != row {
				d++
			}
		}
		d += len(merged) - len(full1.rows) + countMissing(merged, full1.rows)
		drift[f.name] = d
		if d != 0 {
			return fmt.Errorf("merging the %s delta over the first read drifts from a full read on %d rows", f.name, d)
		}
	}
	report["first_round"] = map[string]any{"touched": first, "drift": drift}

	// Second round: the zone, the bench and the wall go; each family names
	// its own in removed_ids for an ask since the original tick.
	second, _, err := mutate("mutate-second", "second")
	if err != nil {
		return err
	}
	if len(second) != 3 {
		return fmt.Errorf("the second round touched %v", second)
	}
	removedWant := map[string][]string{"zones": {zoneID}, "buildings": {benchID, wallID}, "bills": {benchID}}
	for _, f := range families {
		delta, stale, err := read("delta-after-removal", f, tick0)
		if err != nil || stale {
			return fmt.Errorf("delta %s after removal: stale=%v err=%v", f.name, stale, err)
		}
		for _, id := range removedWant[f.name] {
			if !na.Contains(delta.removed, id) {
				return fmt.Errorf("delta %s since %d does not name %s removed: removed %v listed %v", f.name, tick0, id, delta.removed, keys(delta.rows))
			}
			if _, listed := delta.rows[id]; listed {
				return fmt.Errorf("delta %s since %d still lists the removed %s", f.name, tick0, id)
			}
		}
	}
	report["second_round"] = map[string]any{"touched": second}

	// Past the tombstone window the same ask is refused; a full read answers.
	if _, tickExpired, err := mutate("expire", "expire"); err != nil {
		return err
	} else if tickExpired-tick0 <= 2500 {
		return fmt.Errorf("expire moved the tick to %d, within the window of %d", tickExpired, tick0)
	}
	for _, f := range families {
		_, stale, err := read("delta-expired", f, tick0)
		if err != nil {
			return err
		}
		if !stale {
			return fmt.Errorf("delta %s since %d past the tombstone window was not refused as STALE", f.name, tick0)
		}
		full2, _, err := read("full-fallback", f, 0)
		if err != nil {
			return err
		}
		for _, id := range removedWant[f.name] {
			if _, listed := full2.rows[id]; listed {
				return fmt.Errorf("full %s read after removal still lists %s", f.name, id)
			}
		}
	}
	return nil
}

// countMissing counts the merged ids a full read does not carry, so the
// drift is rows differing plus rows in one read and not the other.
func countMissing(merged, full map[string]string) int {
	n := 0
	for id := range merged {
		if _, ok := full[id]; !ok {
			n++
		}
	}
	return n
}

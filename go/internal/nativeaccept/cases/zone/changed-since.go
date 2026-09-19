package zone

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"slices"
	"time"
)

func init() {
	cases.Register(cases.Case{Name: "zone/changed-since", Scope: "Zone add, cell add/remove, crop change and removal return deltas and tombstones; merged facts equal a full census, and the colony aggregate omits farms, food_storage and zone_map_snapshot.", Start: cases.Fixture{Op: "test/cells_prepare"}, Budget: 3 * time.Minute, Run: func(ctx context.Context, s cases.Session) error { return zoneDelta(ctx, s, false) }})
	cases.Register(cases.Case{Name: "zone/tombstone-expiry", Scope: "After 2501 actual ticks a removed zone's delta cursor is refused with entity_tombstone_window_expired; the production Go reader falls back to a full census without resurrecting the zone.", Start: cases.Fixture{Op: "test/cells_prepare"}, Budget: 3 * time.Minute, Run: func(ctx context.Context, s cases.Session) error { return zoneDelta(ctx, s, true) }})
}

func zoneDelta(ctx context.Context, s cases.Session, expire bool) error {
	h := s.Harness()
	encoded, err := json.Marshal(s.Identity())
	if err != nil {
		return err
	}
	id := &c.Identity{}
	if err = protojson.Unmarshal(encoded, id); err != nil {
		return err
	}
	origin, _ := na.AsMap(s.Prepared()["origin"])
	mutate := func(phase string) (string, error) {
		v, err := h.Call(ctx, "zone-"+phase, "test/zones_delta_mutate", map[string]any{"originX": int(na.AsNumber(origin["x"])), "originZ": int(na.AsNumber(origin["z"])), "phase": phase})
		if err != nil {
			return "", err
		}
		ok, _ := na.AsBool(v["success"])
		if !ok {
			return "", fmt.Errorf("zone fixture %s refused: %v", phase, v)
		}
		name, _ := v["id"].(string)
		return name, nil
	}
	read := func(since int64) (bridge.ZonesRead, error) {
		r, _, err := h.Client.ReadZoneSection(ctx, id, since)
		return r, err
	}
	initial, err := read(0)
	if err != nil {
		return err
	}
	name, err := mutate("add")
	if err != nil {
		return err
	}
	removed := false
	defer func() {
		if !removed {
			_, _ = mutate("remove")
		}
	}()
	added, err := read(initial.AsOf)
	if err != nil {
		return err
	}
	find := func(v bridge.ZonesRead) *o.ZoneState {
		for _, z := range v.Rows {
			if z.GetId() == name {
				return z
			}
		}
		return nil
	}
	if !added.Delta || find(added) == nil || find(added).GetFarm().GetZoneId() != name {
		return fmt.Errorf("added growing zone not observed in delta: %s", name)
	}
	merged := bridge.MergeZones(initial, added)
	if _, err = mutate("tick"); err != nil {
		return err
	}
	beforeChange, err := read(0)
	if err != nil {
		return err
	}
	stable, err := read(beforeChange.AsOf)
	if err != nil {
		return err
	}
	if find(stable) != nil || stable.Unchanged == 0 {
		return fmt.Errorf("unchanged zone was relisted after a tick")
	}
	s.Report()["unchanged"] = stable.Unchanged
	if _, err = mutate("change"); err != nil {
		return err
	}
	changed, err := read(beforeChange.AsOf)
	if err != nil {
		return err
	}
	row := find(changed)
	if !changed.Delta || row == nil || row.GetCropDefName() != "Plant_Potato" || row.GetSnapshot().GetToken() == find(beforeChange).GetSnapshot().GetToken() {
		return fmt.Errorf("zone resize/crop delta missing")
	}
	merged = bridge.MergeZones(merged, changed)
	full, err := read(0)
	if err != nil {
		return err
	}
	drift := bridge.ZoneDrift(merged, full)
	s.Report()["drift"] = drift
	if drift != 0 {
		return fmt.Errorf("zones resync drift=%d", drift)
	}
	if _, err = mutate("remove"); err != nil {
		return err
	}
	removed = true
	delta, err := read(full.AsOf)
	if err != nil {
		return err
	}
	if !delta.Delta || !slices.Contains(delta.Removed, name) || find(delta) != nil {
		return fmt.Errorf("zone tombstone missing: %+v", delta)
	}
	merged = bridge.MergeZones(full, delta)
	if find(merged) != nil {
		return fmt.Errorf("tombstone did not remove stored zone")
	}
	s.Report()["removed_id"] = name
	if expire {
		if _, err = mutate("expire"); err != nil {
			return err
		}
		reply, err := h.Wire(ctx, "expired-zone-delta", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "changedSinceTick": fmt.Sprint(full.AsOf)})
		if err != nil {
			return err
		}
		_, failure, err := na.Outcome(reply, "unavailable")
		if err != nil {
			return err
		}
		if failure["detail"] != bridge.ZoneDeltaExpired {
			return fmt.Errorf("wrong expiry refusal: %v", failure)
		}
		fallback, err := read(full.AsOf)
		if err != nil {
			return err
		}
		if !fallback.Fallback || fallback.Delta || find(fallback) != nil {
			return fmt.Errorf("expired zone delta failed to resync")
		}
		s.Report()["refusal"] = failure["detail"]
		s.Report()["fallback"] = fallback.Fallback
	}
	aggregate, err := h.Wire(ctx, "aggregate-without-zones", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "planning": true})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(aggregate, "observed")
	if err != nil {
		return err
	}
	planning, _ := na.AsMap(observed["planning"])
	plan, _ := na.AsMap(planning["observed"])
	if _, ok := observed["farms"]; ok {
		return fmt.Errorf("aggregate still emits farms")
	}
	if _, ok := observed["foodStorage"]; ok {
		return fmt.Errorf("aggregate still emits foodStorage")
	}
	if _, ok := plan["zoneMapSnapshot"]; ok {
		return fmt.Errorf("aggregate still emits zoneMapSnapshot")
	}
	s.Report()["aggregate_zone_fields_absent"] = true
	return nil
}

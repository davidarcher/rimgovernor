// Package smoke holds the cases that prove the runner itself: open a game,
// read what the runner prepared, pass.
package smoke

import (
	"context"
	"fmt"
	"time"

	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
)

func init() {
	cases.Register(cases.Case{
		Name:  "smoke/identity",
		Scope: "Runner smoke: the shared runner opens the blank lab (#731), freezes needs and hands the case a loaded identity.",
		Start: cases.LabStart(),
		// Boot plus a handful of reads on a kept process.
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: func(ctx context.Context, s cases.Session) error {
			identity := s.Identity()
			for _, key := range []string{"colonyId", "loadToken"} {
				if na.AsString(identity[key]) == "" {
					return fmt.Errorf("identity has no %s: %#v", key, identity)
				}
			}
			if !na.Contains(s.Names(), "rimgovernor/operations_apply") {
				return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
			}
			// A second read through the same session must agree with the
			// runner's: the game is loaded and stable under the case.
			reply, err := s.Harness().Wire(ctx, "identity-again", "lifecycle_read_identity", map[string]any{})
			if err != nil {
				return err
			}
			_, loaded, err := na.Outcome(reply, "loaded")
			if err != nil {
				return err
			}
			loadedContext, _ := na.AsMap(loaded["context"])
			again, _ := na.AsMap(loadedContext["identity"])
			if na.AsString(again["loadToken"]) != na.AsString(identity["loadToken"]) {
				return fmt.Errorf("loadToken changed under the case: %q then %q", identity["loadToken"], again["loadToken"])
			}
			s.Report()["identity_again"] = again
			// Exercise the typed read against the live game, including the
			// complete-empty result on a fresh map. This does not admit removal.
			// The census scopes the scan to Home (#414); on the lab it reads an
			// empty map.
			census, err := s.Harness().Call(ctx, "map-census", "test/debug_map_census", map[string]any{})
			if err != nil {
				return err
			}
			s.Report()["map_census"] = census
			if cells, buildings := na.AsNumber(census["mapCells"]), na.AsNumber(census["nonPlayerBuildings"]); cells != na.LabMapSize*na.LabMapSize || buildings != 0 {
				return fmt.Errorf("the lab is not blank: %v cells, %v non-player buildings", cells, buildings)
			}
			clearance, err := s.Harness().Wire(ctx, "clearance", "observations_get_clearance_targets", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
			if err != nil {
				return err
			}
			_, observed, err := na.Outcome(clearance, "observed")
			if err != nil {
				return err
			}
			encoded, err := json.Marshal(observed)
			if err != nil {
				return err
			}
			snapshot := &o.ClearanceTargetsSnapshot{}
			if err := protojson.Unmarshal(encoded, snapshot); err != nil {
				return err
			}
			encoded, err = json.Marshal(identity)
			if err != nil {
				return err
			}
			expected := &c.Identity{}
			if err := protojson.Unmarshal(encoded, expected); err != nil {
				return err
			}
			if err := bridge.ValidateClearanceTargets(snapshot, expected); err != nil {
				return err
			}
			s.Report()["clearance"] = observed
			shrines, err := s.Harness().Wire(ctx, "shrines", "observations_get_ancient_shrines", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
			if err != nil {
				return err
			}
			_, observed, err = na.Outcome(shrines, "observed")
			if err != nil {
				return err
			}
			if encoded, err = json.Marshal(observed); err != nil {
				return err
			}
			shrineCensus := &o.AncientShrinesSnapshot{}
			if err := protojson.Unmarshal(encoded, shrineCensus); err != nil {
				return err
			}
			if err := bridge.ValidateAncientShrines(shrineCensus, expected); err != nil {
				return err
			}
			s.Report()["shrines"] = observed
			return nil
		},
	})
}

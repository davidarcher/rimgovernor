package layout

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// layout/tidy (#611): the field family plants its first patch at Camp
// tier, where no grid alignment applies, so the colony's own field is a
// stray; Stonecutting is then finished (build tier Masonry) and the tidy
// family, once the field work is closed, re-sites that field onto a free
// Fields sub-cell on the grid keeping its crop and deletes the old zone.
// The case proves the old zone id is gone from the native census, a new
// managed zone stands as a module patch on the grid with the same crop,
// and the journal records the tidy done with its explanation.
// The defense and supply families ride along because the run reaches the
// storyteller's first raid (~tick 90k): without them the raid parks every
// goal (#620) and the window ends on emergency_park.
const (
	tidyPrepare  = "test/layout_tidy_prepare"
	tidyResearch = "test/layout_tidy_research"
	tidyStage    = "camp-field"
	tidyStateKey = "layout_tidy_camp"
	// tidyWindow covers the remaining field plans at Masonry, the sowing
	// of the re-sited field and the old zone's deletion.
	tidyWindow = 14 * time.Minute
)

func init() {
	cases.Register(cases.Case{
		Name: "layout/tidy",
		Scope: "Issue #611: on the tribal " + sustained.BaselineSave + " colony with a fixture hut, the field family plants its first " +
			"patch at Camp tier (off the grid); after Stonecutting is finished the TidyLayout goal re-sites that managed field onto a " +
			"free Fields sub-cell of the colony grid keeping its crop, deletes the old zone once the new one is planted, and journals " +
			"the re-site: the old zone id is gone from the census, the new zone is a module patch on the grid with the same crop.",
		Start:       cases.Fixture{Op: tidyPrepare, Args: map[string]any{}, On: cases.Save{Name: sustained.BaselineSave}},
		RequiredOps: []string{tidyResearch, gridAudit},
		Keep:        []string{string(na.NeedFood)},
		Serve:       &cases.ServeSpec{Families: []string{"field", "tidy", "defense", "supply"}, NativeTimeout: 30 * time.Second, Prefix: "layout-tidy"},
		Stages:      []string{tidyStage},
		Budget:      25 * time.Minute,
		Reason:      "two serve stages: the Camp field plan, then the Masonry re-site through sowing and deletion of the old zone",
		Run:         tidy,
	})
}

func tidy(ctx context.Context, s cases.Session) error {
	report := s.Report()
	report["fixture"] = s.Prepared()
	var camp map[string]any
	if err := s.Stage(ctx, tidyStage, func(ctx context.Context) error {
		var audit map[string]any
		_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
			WatchConfig: sustainedfood.WatchConfig{Watch: window, Until: fieldsPlanned},
			Spec:        func(spec *na.ServeSpec) { spec.Families = []string{"field"} },
			Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
				var err error
				if audit, err = h.Call(ctx, "camp-audit", gridAudit, map[string]any{}); err != nil {
					return err
				}
				report["camp_audit"] = audit
				return nil
			},
		})
		if err != nil {
			return err
		}
		var fields []any
		for _, row := range na.AsSlice(audit["zones"]) {
			zone, _ := na.AsMap(row)
			fields = append(fields, map[string]any{"id": zone["loadId"], "crop": zone["crop"], "cells": zone["cells"]})
		}
		if len(fields) == 0 {
			return fmt.Errorf("no Camp field after the first fields plan: %#v", audit)
		}
		camp = map[string]any{"fields": fields}
		na.SetCheckpointState(tidyStateKey, camp)
		return nil
	}); err != nil {
		return err
	}
	if restored := cases.RestoredState(s, tidyStateKey); restored != nil {
		camp, _ = na.AsMap(restored)
	}
	if camp == nil {
		return fmt.Errorf("missing staged Camp field")
	}
	report["camp_fields"] = camp
	// The Camp fields by id: the tidy re-sites the one with the largest
	// alignment gain first, whichever that is.
	type campField struct {
		crop string
		rect policy.Rectangle
	}
	campFields := map[string]campField{}
	for _, row := range na.AsSlice(camp["fields"]) {
		zone, _ := na.AsMap(row)
		id, crop, cells := na.AsString(zone["id"]), na.AsString(zone["crop"]), cellsOf(zone["cells"])
		if id == "" || crop == "" || len(cells) == 0 {
			return fmt.Errorf("staged Camp field lacks an id, crop or cells: %#v", zone)
		}
		campFields[id] = campField{crop, bounding(cells)}
	}
	var record store.ColonyGridRecord
	var tidies []store.LayoutTidy
	var audit map[string]any
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: tidyWindow, Goal: policy.TidyLayout, Until: tidyDeleted},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			finished, err := h.Call(ctx, "finish-stonecutting", tidyResearch, map[string]any{})
			if err != nil {
				return err
			}
			report["research"] = finished
			if ok, _ := na.AsBool(finished["finished"]); !ok {
				return fmt.Errorf("fixture did not finish Stonecutting: %#v", finished)
			}
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return fmt.Errorf("load routine review: %w", err)
			}
			report["layout_review"] = review.Layout
			var ok bool
			if record, ok, err = journal.ColonyGrid(ctx, review.Snapshot, review.Tick); err != nil {
				return err
			}
			if !ok {
				return fmt.Errorf("no colony grid recorded by the review at tick %d", review.Tick)
			}
			if tidies, err = journal.LayoutTidies(ctx, review.Snapshot, review.Tick); err != nil {
				return err
			}
			report["tidies"] = tidies
			if audit, err = h.Call(ctx, "tidy-audit", gridAudit, map[string]any{}); err != nil {
				return err
			}
			report["audit"] = audit
			return nil
		},
	})
	if err != nil {
		return err
	}
	g := record.Grid
	if !g.Valid() {
		return fmt.Errorf("invalid colony grid %+v", g)
	}
	// The staged Camp fields are the strays the case sets up, but the field
	// family keeps planting while Stonecutting finishes, so a field planned
	// just before the tier changed is an equally valid stray: any finished
	// field re-site proves the behaviour, the staged ones first.
	var done *store.LayoutTidy
	for i := range tidies {
		if tidies[i].Kind != policy.TidyField || tidies[i].Status != store.LayoutTidyDone {
			continue
		}
		if _, camp := campFields[tidies[i].Item]; camp || done == nil {
			done = &tidies[i]
		}
	}
	if done == nil {
		return fmt.Errorf("the journal records no finished field tidy: %+v", tidies)
	}
	oldID, crop := done.Item, done.Crop
	if staged, ok := campFields[oldID]; ok {
		if staged.crop != crop {
			return fmt.Errorf("the tidy of staged Camp field %s kept crop %s, not %s", oldID, crop, staged.crop)
		}
		report["old_field"] = describe(staged.rect, g)
	}
	if modulePatch(g, done.From) {
		return fmt.Errorf("the tidied field %s %+v was already a module patch on the grid", oldID, done.From)
	}
	if crop == "" || done.NewZone == "" || done.Explanation == "" {
		return fmt.Errorf("the finished tidy of %s lacks its crop, new zone or explanation: %+v", oldID, *done)
	}
	if !strings.Contains(done.Explanation, "alignment gain") {
		return fmt.Errorf("tidy explanation names no alignment gain: %q", done.Explanation)
	}
	var described []map[string]any
	var moved *policy.Rectangle
	for _, row := range na.AsSlice(audit["zones"]) {
		zone, _ := na.AsMap(row)
		id := na.AsString(zone["loadId"])
		if id == oldID {
			return fmt.Errorf("the old zone %s still stands: %#v", oldID, zone)
		}
		cells := cellsOf(zone["cells"])
		if len(cells) == 0 {
			continue
		}
		rect := bounding(cells)
		d := describe(rect, g)
		d["id"], d["crop"], d["cells"] = id, zone["crop"], len(cells)
		described = append(described, d)
		if id == done.NewZone {
			if na.AsString(zone["crop"]) != crop {
				return fmt.Errorf("the re-sited zone %s grows %v, not the old field's %s", id, zone["crop"], crop)
			}
			if !modulePatch(g, rect) || rect != done.To {
				return fmt.Errorf("the re-sited zone %s %+v is not the proposed module patch %+v on the grid", id, rect, done.To)
			}
			if d := g.District(domain.Cell{X: rect.X, Z: rect.Z}); d == policy.DistrictPlaza {
				return fmt.Errorf("the re-sited zone %s %+v shares the plaza with the hut", id, rect)
			}
			r := rect
			moved = &r
		}
	}
	report["zones"] = described
	if moved == nil {
		return fmt.Errorf("the re-sited zone %s is not in the census: %v", done.NewZone, described)
	}
	return nil
}

// tidyDeleted reports a tidy plan whose zone_delete action completed.
func tidyDeleted(sample map[string]any) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		id, _ := plan["plan"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		kinds, _ := plan["kinds"].(map[string]int)
		if strings.HasPrefix(id, "routine-tidy-") && actions > 0 && kinds[string(domain.ZoneDeleteAction)] > 0 && stages["completed"] == actions {
			return true
		}
	}
	return false
}

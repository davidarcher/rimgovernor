package upkeep

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{Name: "upkeep/colony-extent", Scope: "Runtime extent producer and expansion add/remove leave the complete native Home mask byte-identical (#519, #580); existing Home maintenance covers the ready corridor.", Start: cases.Save{Name: sustained.BaselineSave}, Keep: sleeping.keep, Serve: &cases.ServeSpec{Families: []string{"home-coverage", "work"}, Extra: sleeping.extra, Prefix: prefix}, Budget: 4 * time.Minute, Reason: "Ready connected rooms; Home orders require no construction waits.", Run: runColonyExtent})
}

func runColonyExtent(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	prepared, err := callFixture(ctx, h, s.Identity(), "test/sleeping_setup", map[string]any{"connectedRooms": true})
	if err != nil {
		return err
	}
	s.Report()["prepared"] = prepared
	corridor, _ := na.AsMap(prepared["corridor"])
	outside, _ := na.AsMap(prepared["outside"])
	cx, cz := int(na.AsNumber(corridor["x"])), int(na.AsNumber(corridor["z"]))
	ready, err := callFixture(ctx, h, s.Identity(), "test/extent_home_prepare", map[string]any{"x": cx, "z": cz})
	if err != nil {
		return err
	}
	s.Report()["ready_home"] = ready
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	if _, err = followMethods(ctx, journal, policy.MaintainHomeCoverage, "home", func(domain.Action) error { return nil }, s.Report()); err != nil {
		return err
	}
	if _, err = waitNeed(ctx, journal, policy.MaintainHomeCoverage, domain.NeedRecovered); err != nil {
		return err
	}
	service.Stop()
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	if err = auditHomeCells(ctx, h, cx, cz, outside, true); err != nil {
		return err
	}
	before, err := h.Call(ctx, "extent-home-before", "test/home_mask", nil)
	if err != nil {
		return err
	}
	service, err = s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err = serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	history, err := journal.EstablishedColonyExtent(ctx, review.Snapshot, review.Tick)
	if err != nil || len(history) == 0 {
		return fmt.Errorf("runtime extent missing: %d regions: %w", len(history), err)
	}
	occupied := map[domain.Cell]bool{}
	for _, row := range history {
		for _, c := range row.Region.Cells {
			occupied[c.Cell] = true
		}
	}
	if !occupied[domain.Cell{X: int32(cx), Z: int32(cz)}] {
		return fmt.Errorf("runtime extent omitted observed corridor")
	}
	cell := domain.Cell{}
	if occupied[cell] {
		return fmt.Errorf("expansion fixture origin already belongs to extent")
	}
	change := func(path string, add bool) error {
		body := map[string]any{"expected": service.Identity, "id": "extent-smoke", "reason": "explicit expansion smoke"}
		if add {
			body["cells"] = []map[string]int32{{"x": cell.X, "z": cell.Z}}
		}
		response, status, e := service.API("POST", "/api/player/expansion-area/"+path, body, service.Token)
		if e != nil || status != 200 {
			return fmt.Errorf("expansion %s: status %d, %v: %w", path, status, response, e)
		}
		return nil
	}
	if err = change("add", true); err != nil {
		return err
	}
	// Read past the API's native tick, not the earlier review tick.
	areas, err := journal.ExpansionAreas(ctx, review.Snapshot, domain.Tick(1<<60))
	if err != nil || len(areas) != 1 || len(areas[0].Cells) != 1 || areas[0].Cells[0] != cell {
		return fmt.Errorf("expansion did not grow extent: %v %v", areas, err)
	}
	s.Report()["established_regions"] = len(history)
	s.Report()["extent_cells_before"] = len(occupied)
	s.Report()["extent_cells_after"] = len(occupied) + 1
	service.Stop()
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	grown, err := h.Call(ctx, "extent-home-expanded", "test/home_mask", nil)
	if err != nil {
		return err
	}
	if na.AsString(before["mask"]) == "" || na.AsString(before["mask"]) != na.AsString(grown["mask"]) {
		return fmt.Errorf("expansion growth mutated native Home")
	}
	service, err = s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err = serveStage(ctx, service, s.Report())
	if err != nil {
		return err
	}
	if err = change("remove", false); err != nil {
		return err
	}
	areas, err = journal.ExpansionAreas(ctx, review.Snapshot, domain.Tick(1<<60))
	if err != nil || len(areas) != 0 {
		return fmt.Errorf("expansion removal failed: %v %v", areas, err)
	}
	service.Stop()
	h, err = reattachPaused(ctx, s)
	if err != nil {
		return err
	}
	after, err := h.Call(ctx, "extent-home-after", "test/home_mask", nil)
	if err != nil {
		return err
	}
	mask := na.AsString(before["mask"])
	if mask == "" || mask != na.AsString(after["mask"]) {
		return fmt.Errorf("extent changes mutated the native Home mask")
	}
	s.Report()["home_mask_byte_identical"] = true
	return auditHomeCells(ctx, h, cx, cz, outside, true)
}

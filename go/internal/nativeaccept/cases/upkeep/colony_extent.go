package upkeep

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	sleeping := scenarios()["sleeping"]
	cases.Register(cases.Case{Name: "upkeep/colony-extent", Scope: "Runtime extent producer leaves the complete native Home mask byte-identical (#519, #580); existing Home maintenance covers the ready corridor.", Start: cases.LabStart(), Keep: sleeping.keep, Serve: &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.HomeCoverage, routinefamily.Work}, Extra: sleeping.extra, Prefix: prefix}, Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3}, Reason: "Ready connected rooms; Home orders require no construction waits.", Run: runColonyExtent})
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
	if _, err = waitNeed(ctx, journal, policy.MaintainHomeCoverage, domain.FindingMet); err != nil {
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
	review, err := journal.LoadRounds(ctx)
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
	service.Stop()
	s.Report()["established_regions"] = len(history)
	s.Report()["extent_cells"] = len(occupied)
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

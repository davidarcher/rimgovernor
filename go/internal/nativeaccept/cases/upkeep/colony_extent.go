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
	cases.Register(cases.Case{Name: "upkeep/colony-extent", Scope: "Runtime extent producer leaves the complete native Home mask byte-identical; Home maintenance covers the ready corridor and base margin, leaving cells beyond the margin outside Home.", Start: cases.LabStart(), Keep: sleeping.keep, Serve: &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.HomeCoverage, routinefamily.Work}, Extra: sleeping.extra, Prefix: prefix}, Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3}, Reason: "Ready connected rooms; Home orders require no construction waits.", Run: runColonyExtent})
}

func runColonyExtent(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	prepared, err := callFixture(ctx, h, s.Identity(), "test/sleeping_setup", map[string]any{"connectedRooms": true})
	if err != nil {
		return err
	}
	s.Report()["prepared"] = prepared
	corridor, _ := na.AsMap(prepared["corridor"])
	margin, _ := na.AsMap(prepared["outside"])
	// The fixture point is one cell beyond the east wall, inside the Home
	// margin. The first excluded cell is a full margin farther east.
	outside := map[string]any{"x": int(na.AsNumber(margin["x"])) + int(policy.HomeAreaMargin), "z": int(na.AsNumber(margin["z"]))}
	s.Report()["home_margin_cell"], s.Report()["home_outside_margin"] = margin, outside
	cx, cz := int(na.AsNumber(corridor["x"])), int(na.AsNumber(corridor["z"]))
	recordHome := func(stage string, wantMargin bool) error {
		mask, err := h.Call(ctx, stage+"-mask", "test/home_mask", nil)
		if err != nil {
			return err
		}
		cell, err := h.Call(ctx, stage+"-outside", "test/home_cell_read", outside)
		if err != nil {
			return err
		}
		marginCell, err := h.Call(ctx, stage+"-margin", "test/home_cell_read", margin)
		if err != nil {
			return err
		}
		s.Report()[stage] = map[string]any{"mask": mask["mask"], "outside": cell, "margin": marginCell}
		outsideHome, outsideKnown := na.AsBool(cell["home"])
		marginHome, marginKnown := na.AsBool(marginCell["home"])
		outsideOK, _ := na.AsBool(cell["success"])
		marginOK, _ := na.AsBool(marginCell["success"])
		if !outsideOK || !marginOK || !outsideKnown || !marginKnown || outsideHome || marginHome != wantMargin {
			return fmt.Errorf("%s: outside Home=%v, margin Home=%v (want %t)", stage, cell, marginCell, wantMargin)
		}
		return nil
	}
	if err := recordHome("home_after_sleeping_setup", false); err != nil {
		return err
	}

	ready, err := callFixture(ctx, h, s.Identity(), "test/extent_home_prepare", map[string]any{"x": cx, "z": cz})
	if err != nil {
		return err
	}
	s.Report()["ready_home"] = ready
	if err := recordHome("home_before_maintenance", false); err != nil {
		return err
	}
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
	if err := recordHome("home_after_maintenance", true); err != nil {
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

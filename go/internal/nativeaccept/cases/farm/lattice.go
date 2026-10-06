package farm

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	latticePrepare = "test/lattice_sow_prepare"
	latticeCensus  = "test/lattice_sow_census"
	latticeWidth   = 5
	latticeHeight  = 4
	// latticeTicks bounds the native sowing run: two small zones sown by the
	// fixture's colonists take well under three game days.
	latticeTicks = 3 * na.TicksPerDay
)

func init() {
	cases.Register(cases.Case{
		Name: "farm/lattice-sow",
		Scope: "Issue #2290: a growing zone of trees (blockAdjacentSow) sown by colonists ends with exactly ceil(N/2)*ceil(M/2) trees, " +
			"every one on the 2x2 lattice anchored on the zone's minimum corner, while a rice zone of the same size is filled completely. " +
			"A Go snapshot cannot cover it: the rule is a Harmony patch on vanilla WorkGiver_GrowerSow.JobOnCell and the sow-blocking is vanilla's own (PlantUtility.AdjacentSowBlocker).",
		Start:       cases.Fixture{Op: latticePrepare, On: cases.LabStart(), Args: map[string]any{"width": latticeWidth, "height": latticeHeight}},
		NoKeep:      true,
		RequiredOps: []string{latticePrepare, latticeCensus},
		Budget:      10 * time.Minute,
		Run:         runLatticeSow,
	})
}

type latticeZone struct {
	plant      string
	cells      int
	minX, minZ int
	planted    [][2]int
}

func readLatticeZones(ctx context.Context, h *na.Harness, label string) (map[string]latticeZone, error) {
	reply, err := h.Call(ctx, label, latticeCensus, map[string]any{})
	if err != nil {
		return nil, err
	}
	zones := map[string]latticeZone{}
	for _, raw := range na.AsSlice(reply["zones"]) {
		row, _ := na.AsMap(raw)
		z := latticeZone{plant: na.AsString(row["plant"]), cells: int(na.AsNumber(row["cells"])),
			minX: int(na.AsNumber(row["minX"])), minZ: int(na.AsNumber(row["minZ"]))}
		for _, c := range na.AsSlice(row["planted"]) {
			cell, _ := na.AsMap(c)
			z.planted = append(z.planted, [2]int{int(na.AsNumber(cell["x"])), int(na.AsNumber(cell["z"]))})
		}
		zones[fmt.Sprint(int(na.AsNumber(row["id"])))] = z
	}
	return zones, nil
}

func runLatticeSow(ctx context.Context, s cases.Session) error {
	report, prepared, h := s.Report(), s.Prepared(), s.Harness()
	oakID, riceID := fmt.Sprint(int(na.AsNumber(prepared["oakZone"]))), fmt.Sprint(int(na.AsNumber(prepared["riceZone"])))
	if _, ok := prepared["oakZone"]; !ok || oakID == riceID { // zone ids start at 0
		return fmt.Errorf("fixture: unexpected lattice staging: %#v", prepared)
	}
	report["fixture"] = prepared
	wantTrees := domain.TreeLatticeCount(latticeWidth, latticeHeight)
	wantRice := latticeWidth * latticeHeight

	var zones map[string]latticeZone
	_, err := na.RunUntil(ctx, h, "lattice-sow", latticeTicks, na.Wait{Stall: na.StallBudget()},
		func(ctx context.Context) (string, bool, error) {
			var err error
			if zones, err = readLatticeZones(ctx, h, "lattice-census"); err != nil {
				return "", false, err
			}
			oak, rice := zones[oakID], zones[riceID]
			return fmt.Sprintf("oak=%d rice=%d", len(oak.planted), len(rice.planted)), len(oak.planted) >= wantTrees && len(rice.planted) >= wantRice, nil
		})
	report["lattice_zones"] = zones
	if err != nil {
		return fmt.Errorf("sowing: %w", err)
	}
	oak, rice := zones[oakID], zones[riceID]
	if len(oak.planted) != wantTrees {
		return fmt.Errorf("oak zone holds %d trees, want ceil(%d/2)*ceil(%d/2)=%d", len(oak.planted), latticeWidth, latticeHeight, wantTrees)
	}
	for _, c := range oak.planted {
		if (c[0]-oak.minX)%domain.TreeLatticePitch != 0 || (c[1]-oak.minZ)%domain.TreeLatticePitch != 0 {
			return fmt.Errorf("oak at (%d,%d) is off the lattice anchored at (%d,%d)", c[0], c[1], oak.minX, oak.minZ)
		}
	}
	if len(rice.planted) != wantRice {
		return fmt.Errorf("rice zone holds %d plants, want every one of %d cells", len(rice.planted), wantRice)
	}
	return nil
}

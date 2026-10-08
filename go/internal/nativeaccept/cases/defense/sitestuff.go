package defense

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// defense/site-stuff proves the defense-site census's edifice stuff
// (#1065): every colonist wall the lighting fixture builds reads back from
// observations_read_defense_site with the stuff the building census gives
// it. The wait hardening tells a wooden door from a plasteel one by this
// field. Read-only: no orders, no clock.
func init() {
	cases.Register(cases.Case{
		Name:   "defense/site-stuff",
		Scope:  "The defense-site census carries each colonist wall's stuff def, equal to the building census's (#1065); read-only.",
		Start:  cases.Fixture{Op: "test/lighting_prepare", On: cases.LabStart()},
		Budget: 3 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runSiteStuff,
	})
}

// siteStuffWalls bounds the walls checked, one census cell each.
const siteStuffWalls = 16

func runSiteStuff(ctx context.Context, s cases.Session) error {
	report, h := s.Report(), s.Harness()
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	context_, _ := loaded["context"].(map[string]any)
	scope := map[string]any{"expectedIdentity": context_["identity"]}
	reply, err := h.Wire(ctx, "walls", "observations_list_buildings", map[string]any{"scope": scope, "defNames": []any{"Wall"}, "statuses": []any{"built"}, "playerOnly": true})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	checked := 0
	for _, raw := range na.AsSlice(observed["buildings"]) {
		if checked == siteStuffWalls {
			break
		}
		row, _ := na.AsMap(raw)
		stuff := na.AsString(row["stuff"])
		occupied := na.RectCells(row["occupied"])
		if stuff == "" || len(occupied) != 1 {
			continue
		}
		at := map[string]any{"x": float64(occupied[0].X), "z": float64(occupied[0].Z)}
		siteReply, err := h.Wire(ctx, fmt.Sprintf("site-%d", checked), "observations_read_defense_site", map[string]any{"scope": scope, "region": map[string]any{"minimum": at, "maximum": at}})
		if err != nil {
			return err
		}
		_, site, err := na.Outcome(siteReply, "observed")
		if err != nil {
			return err
		}
		cells := na.AsSlice(site["cells"])
		if len(cells) != 1 {
			return fmt.Errorf("wall at %v: %d census cells", at, len(cells))
		}
		got, _ := na.AsMap(cells[0])
		if na.AsString(got["edificeDefName"]) != "Wall" || na.AsString(got["edificeStuffDefName"]) != stuff {
			return fmt.Errorf("wall at %v of %s: census reads %v/%v", at, stuff, got["edificeDefName"], got["edificeStuffDefName"])
		}
		checked++
	}
	if checked == 0 {
		return fmt.Errorf("no single-cell colonist wall with stuff; the assertion would be vacuous")
	}
	report["walls_checked"] = checked
	return nil
}

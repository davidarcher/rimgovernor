package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// drawStockOverlay pushes the stockpile shortfall tint (#825) after a
// review, gated by the layout overlay flag like the other layers: every
// stockpile the zone census lists, tinted by the review's resource targets
// and the food runway, sent when it changes or an hour passed, and cleared
// once with the flag off or no stockpile. Output only: a failure is
// dropped and the next review draws again.
func (r *Rounder) drawStockOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, result store.RoundsResult) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	var layer policy.LayoutOverlay
	if r.layoutOverlay && projection.Zones.Complete {
		zones, err := r.stockZones(ctx, snapshot, projection)
		if err != nil {
			return
		}
		f := projection.Facts
		levels := policy.StockLevels{Targets: result.Needs.ResourceTargets, Stock: map[policy.Resource]int64{}, FoodDays: f.FoodDays, FoodTargetDays: r.seasonal(f).FoodTargetDays}
		if rows, known := policy.WoodStock(f.Resources, f.Wood, levels.Targets).Value(); known {
			for _, row := range rows {
				levels.Stock[row.Resource] += row.Count
			}
		}
		layer = policy.StockOverlay(zones, levels, projection.Bounds)
	}
	on := len(layer.Layers) > 0 || len(layer.Labels) > 0
	tick := projection.Identity.Tick
	if !on {
		if !r.stock.cleared {
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.StockLayer, policy.LayoutOverlay{}, false); err != nil {
				return
			}
			r.stock = overlayState{cleared: true}
		}
		return
	}
	key := fmt.Sprint(layer)
	if key == r.stock.key && tick >= r.stock.drawn && tick-r.stock.drawn < overlayResendEvery {
		return
	}
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.StockLayer, layer, true); err != nil {
		return
	}
	r.stock = overlayState{key: key, drawn: tick}
}

// stockZones is every stockpile the zone census lists (#719), its cells
// from the projection's planning cells (else its census bounds), with the
// colony's claim role and settings, the latest patch superseding them,
// when the colony created it.
func (r *Rounder) stockZones(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) ([]policy.StockZone, error) {
	tick := projection.Identity.Tick
	claims, err := r.player.journal.ZoneClaims(ctx, snapshot, tick)
	if err != nil {
		return nil, err
	}
	patches, err := r.player.journal.StockpilePatches(ctx, snapshot, tick)
	if err != nil {
		return nil, err
	}
	owned := map[string]store.OwnedZone{}
	if rows, known := claims.Value(); known {
		for _, z := range rows {
			owned[z.ID] = z
		}
	}
	cells := map[string][]domain.Cell{}
	for _, cell := range projection.Cells {
		if id, known := cell.ZoneID.Value(); known && id != "" {
			cells[id] = append(cells[id], cell.Cell)
		}
	}
	var out []policy.StockZone
	for _, row := range projection.Zones.Value.Rows {
		if row.GetType() != "stockpile" {
			continue
		}
		z := policy.StockZone{ID: row.GetId(), Label: row.GetLabel(), FoodStorage: row.GetFoodStorage(), Cells: cells[row.GetId()]}
		if claim, ok := owned[z.ID]; ok && claim.Kind == domain.StockpileZone {
			z.Role, z.Filter = claim.Role, domain.Known(claim.Filter)
			if patch, ok := patches[z.ID]; ok && patch.Kind == domain.StorageZoneTarget {
				z.Filter = domain.Known(patch.Filter)
				if patch.Role != "" {
					z.Role = patch.Role
				}
			}
		}
		if b := row.GetBounds(); len(z.Cells) == 0 && b != nil {
			for x := b.GetMinimum().GetX(); x <= b.GetMaximum().GetX(); x++ {
				for zz := b.GetMinimum().GetZ(); zz <= b.GetMaximum().GetZ(); zz++ {
					z.Cells = append(z.Cells, domain.Cell{X: x, Z: zz})
				}
			}
		}
		out = append(out, z)
	}
	return out, nil
}

package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// benchCensus is the bench read general-store siting ranks by (#723); a
// source without it keeps the anchor order.
type benchCensus interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
}

// benchConsumers is every located bench as a haul consumer weighted by its
// traffic: one trip share for the bench plus one per active bill.
func benchConsumers(benches []policy.GearBench) []policy.HaulConsumer {
	var out []policy.HaulConsumer
	for _, b := range benches {
		at, known := b.Cell.Value()
		if !known {
			continue
		}
		weight := int64(1)
		if bills, ok := b.Bills.Value(); ok {
			for _, bill := range bills {
				if active, ak := bill.Active.Value(); ak && active {
					weight++
				}
			}
		}
		out = append(out, policy.HaulConsumer{Cells: []domain.Cell{at}, Weight: weight})
		if len(out) == 64 {
			break
		}
	}
	return out
}

// benchCentralSites reorders general-store room sites so the one central to
// the benches by traffic-weighted walking distance comes first (#723). With
// no bench census, or no located bench, the incoming order stands.
func benchCentralSites(ctx context.Context, native any, identity *c.Identity, cells []policy.SiteCell, sites []policy.Rectangle) ([]policy.Rectangle, error) {
	source, ok := native.(benchCensus)
	if !ok || len(sites) < 2 {
		return sites, nil
	}
	census, _, err := source.ReadGearBenches(ctx, identity)
	if err != nil {
		return nil, err
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	consumers := benchConsumers(benches)
	if len(consumers) == 0 {
		return sites, nil
	}
	costs, err := policy.HaulCosts(cells, consumers)
	if err != nil {
		return nil, err
	}
	return policy.RankSitesByHaul(sites, costs), nil
}

package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HaulConsumer is one place hauled stock is carried to: a bench, a
// hospital bed. Cells are the consumer's own footprint, which need not be
// walkable (a bench is an edifice); Weight is its expected traffic, the
// number of trips per unit of stock relative to the other consumers.
type HaulConsumer struct {
	Cells  []domain.Cell
	Weight int64
}

// HaulCosts is the traffic-weighted walking distance from every census cell
// to the consumers: for each consumer a breadth-first search over walkable
// cells (8-neighbour, RimWorld's pather) starting from the cells beside its
// footprint, summed as Weight x steps. A cell some consumer cannot reach
// from it is absent: stock stored there is unreachable to that consumer.
// Unknown walkability is never walked.
func HaulCosts(cells []SiteCell, consumers []HaulConsumer) (map[domain.Cell]int64, error) {
	walkable := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		walkable[c.Cell] = positive(c.Walkable)
	}
	total := map[domain.Cell]int64{}
	reached := map[domain.Cell]int{}
	used := 0
	for _, consumer := range consumers {
		if consumer.Weight <= 0 || len(consumer.Cells) == 0 {
			continue
		}
		used++
		distance := map[domain.Cell]int64{}
		var queue []domain.Cell
		for _, c := range consumer.Cells {
			distance[c] = 0
			queue = append(queue, c)
		}
		for len(queue) > 0 {
			at := queue[0]
			queue = queue[1:]
			for dx := int32(-1); dx <= 1; dx++ {
				for dz := int32(-1); dz <= 1; dz++ {
					next := domain.Cell{X: at.X + dx, Z: at.Z + dz}
					if _, seen := distance[next]; seen || !walkable[next] {
						continue
					}
					distance[next] = distance[at] + 1
					queue = append(queue, next)
				}
			}
		}
		for c, d := range distance {
			if !walkable[c] {
				continue
			}
			total[c] += consumer.Weight * d
			reached[c]++
		}
	}
	for c := range total {
		if reached[c] != used {
			delete(total, c)
		}
	}
	return total, nil
}

// RankSitesByHaul reorders candidate sites cheapest haul first: a site costs
// its cheapest cell, since haulers drop stock on the nearest free cell. A
// site no consumer path reaches keeps its incoming order after every
// reachable one; with no costs (no consumers) the order is unchanged.
func RankSitesByHaul(sites []Rectangle, costs map[domain.Cell]int64) []Rectangle {
	type ranked struct {
		site  Rectangle
		cost  int64
		ok    bool
		order int
	}
	rows := make([]ranked, len(sites))
	for i, site := range sites {
		rows[i] = ranked{site: site, order: i}
		for _, c := range rectCells(site) {
			if v, ok := costs[c]; ok && (!rows[i].ok || v < rows[i].cost) {
				rows[i].cost, rows[i].ok = v, true
			}
		}
	}
	sort.SliceStable(rows, func(i, j int) bool {
		if rows[i].ok != rows[j].ok {
			return rows[i].ok
		}
		if rows[i].ok && rows[i].cost != rows[j].cost {
			return rows[i].cost < rows[j].cost
		}
		return rows[i].order < rows[j].order
	})
	out := make([]Rectangle, len(rows))
	for i, r := range rows {
		out[i] = r.site
	}
	return out
}

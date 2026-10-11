package policy

import (
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// siteFreeFloor is the roofed, walkable, unbuilt, unzoned and unprotected
// floor keep accepts: where a basin, lamp or heater may stand.
func siteFreeFloor(site FarmSiteRequest, keep func(SiteCell) bool) map[domain.Cell]bool {
	blocked := map[domain.Cell]bool{}
	for _, cell := range site.Protected {
		blocked[cell] = true
	}
	free := map[domain.Cell]bool{}
	for _, s := range site.Cells {
		if keep(s) && !blocked[s.Cell] && positive(s.Roofed) && positive(s.Walkable) && !s.Occupied() && siteKnownFalse(s.Zone) {
			free[s.Cell] = true
		}
	}
	return free
}

// siteBasinPlacements packs up to limit 1x4 basins on free floor, nearest the
// anchor first.
func siteBasinPlacements(free map[domain.Cell]bool, definition string, anchor domain.Cell, limit int) []SiteBuilding {
	ordered := make([]domain.Cell, 0, len(free))
	for cell := range free {
		ordered = append(ordered, cell)
	}
	sort.Slice(ordered, func(i, j int) bool {
		a, b := ordered[i], ordered[j]
		da, db := siteManhattan(a, anchor), siteManhattan(b, anchor)
		if da != db {
			return da < db
		}
		return cellLess(a, b)
	})
	taken := map[domain.Cell]bool{}
	var out []SiteBuilding
	for _, cell := range ordered {
		if len(out) >= limit {
			break
		}
		footprint := siteBasinFootprint(cell)
		fits := true
		for _, f := range footprint {
			fits = fits && free[f] && !taken[f]
		}
		if !fits {
			continue
		}
		for _, f := range footprint {
			taken[f] = true
		}
		out = append(out, SiteBuilding{Definition: definition, Cell: cell, Rotation: domain.North})
	}
	return out
}

// siteBasinWalk is the summed Manhattan distance from the anchor over every
// footprint cell of the basins.
func siteBasinWalk(basins []SiteBuilding, anchor domain.Cell) int {
	walk := 0
	for _, b := range basins {
		for _, f := range siteBasinFootprint(b.Cell) {
			walk += siteManhattan(f, anchor)
		}
	}
	return walk
}

// siteHydroponicsUnit sites a soil-free hydroponics unit where no sun lamp
// exists: one lamp, a heater when the room or outdoors is below siteColdC, and
// basins on the roofed indoor floor inside the lamp's growth disc, as one plan
// (a lamp alone lights nothing, and the lamp builder needs soil). The unit is
// the largest basin count one network carries (lamp by day, basins and heater
// all day) and the accessible stock pays for; with none it holds with the
// reason. Wiring the buildings to the network is not this plan's work.
func siteHydroponicsUnit(r SiteTypeRequest, w SiteTypeWeights, env ControlledEnvironment, v viableCrop, c SiteTypeCandidate, basin Infrastructure, basinDraw, fertility, unit float64) SiteTypeCandidate {
	lamp, lk := r.Lamp.Value()
	lampDraw, dk := lamp.PowerW.Value()
	if !lk || !positive(lamp.Available) || !dk || !foodNumber(lampDraw) || lamp.Name == "" {
		c.Reason = "sun lamp unavailable"
		return c
	}
	site := r.Field.Site
	// Indoor soil the crop grows in belongs to the greenhouse kinds; the unit
	// is the soil-free bootstrap.
	minimum, _ := v.crop.FertilityMin.Value()
	for _, s := range site.Cells {
		if _, soil := freeCropSoil(siteUnroofed(s), minimum, nil); soil && positive(s.Roofed) && positive(s.Indoors) {
			c.Reason = "free indoor soil: a greenhouse lamp is the cheaper kind"
			return c
		}
	}
	roomOf := map[domain.Cell]string{}
	for _, s := range site.Cells {
		if id, known := s.Room.Value(); known && id != "" {
			roomOf[s.Cell] = id
		}
	}
	free := siteFreeFloor(site, func(s SiteCell) bool { return positive(s.Indoors) })
	cells := make([]domain.Cell, 0, len(free))
	for cell := range free {
		if roomOf[cell] == "" {
			delete(free, cell)
			continue
		}
		cells = append(cells, cell)
	}
	sort.Slice(cells, func(i, j int) bool { return cellLess(cells[i], cells[j]) })
	// A basin's fertility yields several soil cells' harvest, so the unit
	// needs fewer cells than the soil-cell target.
	cellsPerSoil := cropRate(v.crop, fertility) / unit
	if !foodNumber(cellsPerSoil) || cellsPerSoil < 1 {
		cellsPerSoil = 1
	}
	limit := min(int(math.Ceil(float64(v.needed)/cellsPerSoil/siteBasinCells)), farmSitePatchLimit)
	var center domain.Cell
	var basins []SiteBuilding
	bestWalk := 0
	for _, candidate := range cells {
		disc := map[domain.Cell]bool{}
		for cell := range free {
			if cell != candidate && roomOf[cell] == roomOf[candidate] && siteWithin(cell, candidate, r.LampGrowthRadius) {
				disc[cell] = true
			}
		}
		placed := siteBasinPlacements(disc, basin.Name, site.Anchor, limit)
		walk := siteBasinWalk(placed, site.Anchor)
		if len(placed) > len(basins) || len(placed) == len(basins) && len(placed) > 0 && walk < bestWalk {
			center, basins, bestWalk = candidate, placed, walk
		}
	}
	if len(basins) == 0 {
		c.Reason = "no free roofed floor for a lamp and a basin"
		return c
	}
	cold := false
	if t, known := env.OutdoorTemperatureC.Value(); known && t < siteColdC {
		cold = true
	}
	for _, room := range env.Rooms {
		if t, known := room.TemperatureC.Value(); known && room.ID == roomOf[center] && t < siteColdC {
			cold = true
		}
	}
	var heater Infrastructure
	heaterDraw := 0.0
	var heaterAt domain.Cell
	if cold {
		var hk, hdk bool
		heater, hk = r.Heater.Value()
		heaterDraw, hdk = heater.PowerW.Value()
		if !hk || !positive(heater.Available) || !hdk || !foodNumber(heaterDraw) || heater.Name == "" {
			c.Reason = "room too cold and no heater available"
			return c
		}
		used := map[domain.Cell]bool{center: true}
		for _, b := range basins {
			for _, f := range siteBasinFootprint(b.Cell) {
				used[f] = true
			}
		}
		found := false
		for _, cell := range cells {
			if used[cell] || roomOf[cell] != roomOf[center] {
				continue
			}
			if !found || siteManhattan(cell, center) < siteManhattan(heaterAt, center) {
				heaterAt, found = cell, true
			}
		}
		if !found {
			c.Reason = "no free floor for a heater"
			return c
		}
	}
	stock, stockKnown := r.Stock.Value()
	unitCosts := func(n int) map[Resource]int64 {
		need := map[Resource]int64{}
		add := func(infra Infrastructure, count int64) {
			costs, _ := infra.Costs.Value()
			for _, a := range costs {
				need[a.Resource] += a.Count * count
			}
		}
		add(lamp, 1)
		if cold {
			add(heater, 1)
		}
		add(basin, int64(n))
		return need
	}
	// The largest count both power and stock carry; the reason is the one
	// that refuses the smallest unit.
	n, reason := 0, ""
	for k := len(basins); k >= 1; k-- {
		powered := false
		for _, network := range env.Networks {
			one := env
			one.Networks = []PowerHeadroom{network}
			_, _, day := sitePower(one, lampDraw+float64(k)*basinDraw+heaterDraw, false)
			_, _, night := sitePower(one, float64(k)*basinDraw+heaterDraw, true)
			powered = powered || day && night
		}
		short := ""
		if stockKnown {
			need := unitCosts(k)
			names := make([]string, 0, len(need))
			for resource := range need {
				names = append(names, string(resource))
			}
			sort.Strings(names)
			for _, name := range names {
				if have := stock[Resource(name)]; have < need[Resource(name)] {
					short = fmt.Sprintf("not enough %s for a hydroponics unit: need %d have %d", name, need[Resource(name)], have)
					break
				}
			}
		}
		if powered && short == "" {
			n = k
			break
		}
		if k == 1 {
			reason = "no power headroom for a hydroponics unit"
			if powered {
				reason = short
			}
		}
	}
	if n == 0 {
		c.Reason = reason
		return c
	}
	c.Buildings = []SiteBuilding{{Definition: lamp.Name, Cell: center, Rotation: domain.North}}
	if cold {
		c.Buildings = append(c.Buildings, SiteBuilding{Definition: heater.Name, Cell: heaterAt, Rotation: domain.North})
	}
	c.Buildings = append(c.Buildings, basins[:n]...)
	type charge struct {
		infra Infrastructure
		count int
	}
	charges := []charge{{lamp, 1}, {basin, n}}
	if cold {
		charges = append(charges, charge{heater, 1})
	}
	build := 0.0
	for _, ch := range charges {
		b, err := siteConstructionCharge(r.Items, w, unit, ch.infra, ch.count)
		if err != nil {
			c.Buildings, c.Reason = nil, err.Error()
			return c
		}
		build += b
	}
	c.Cells = n * siteBasinCells
	walk := siteBasinWalk(basins[:n], site.Anchor)
	draw := lampDraw*siteLampDuty + float64(n)*basinDraw + heaterDraw
	c.Terms = []FarmSiteTerm{{"yield", cropRate(v.crop, fertility) * float64(c.Cells)}, {"travel", -siteTravelWeight * unit * float64(walk)}, {"construction", build}, {"power", -w.Power * unit * draw / 1000}}
	total := 0.0
	for _, t := range c.Terms {
		total += t.Value
	}
	c.Score = total / float64(v.needed)
	c.Reason = fmt.Sprintf("net %.4f/day over a lamp and %d basins", total, n)
	return c
}

package policy

import (
	"fmt"
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SiteKind is where and under what infrastructure a field grows.
type SiteKind string

const (
	// SiteOutdoor is open soil, the PlanField choice.
	SiteOutdoor SiteKind = "outdoor"
	// SiteGreenhouseReuse is roofed soil already lit by a running sun lamp.
	SiteGreenhouseReuse SiteKind = "greenhouse-reuse"
	// SiteGreenhouseNew is roofed indoor soil lit by a sun lamp the plan builds.
	SiteGreenhouseNew SiteKind = "greenhouse-new"
	// SiteHydroponics is new basins on roofed cells a running sun lamp lights.
	SiteHydroponics SiteKind = "hydroponics"
	// SiteDarkRoom is unlit roofed indoor soil for a crop that grows without light.
	SiteDarkRoom SiteKind = "dark-room"
)

// siteKindOrder breaks score ties toward the cheaper, less invasive kind.
var siteKindOrder = map[SiteKind]int{SiteOutdoor: 0, SiteGreenhouseReuse: 1, SiteDarkRoom: 2, SiteGreenhouseNew: 3, SiteHydroponics: 4}

// Infrastructure is a buildable definition a controlled site may add, with
// its native availability and base power draw.
type Infrastructure struct {
	Name      string
	Available domain.Fact[bool]
	PowerW    domain.Fact[float64]
	// Fertility is a grower's native soil equivalent (2.8 for a basin).
	Fertility domain.Fact[float64]
	// Costs is the native cost list; unknown prices as 100 steel.
	Costs domain.Fact[[]Amount]
}

// SiteTypeWeights price infrastructure in normal-soil cells of the crop's
// daily nutrition, like FarmSiteWeights, so a lamp is only worth building
// when the soil it lights outgrows its cost.
type SiteTypeWeights struct {
	// Construction is charged per 100 steel-equivalent of a new building's
	// cost list (siteSteelEquivalent).
	Construction float64
	// Power is charged per kilowatt of added draw, averaged over the day.
	Power float64
}

// DefaultSiteTypeWeights price infrastructure at the game's own numbers
// amortised over one 60-day year. A unit is one normal-soil cell's harvest
// nutrition per grow day; plants grow only while not resting (06:00-19:12),
// so a unit is ~0.055 nutrition/day of rice, 1.1 raw food at 1.1 silver:
// about 1.2 silver/day. A solar generator (100 steel + 3 components, 286
// silver) yields ~0.55 of its 1700W over a day, so a kilowatt-day costs ~5
// silver, 4 units (wood-fired fuel, 22 wood/day per kW, would be ~22). 100
// steel is 190 silver, ~3.2 silver/day over the year: 2.6 units, so a
// 40-steel sun lamp costs ~1 unit, a basin ~3 and a heater ~1.75. A 2900W
// lamp on its 55% duty then costs ~7.4 units: a greenhouse needs eight
// roofed cells to pay for itself, and basins beat lit soil only for crops
// that gain from their fertility (rice, not potatoes).
func DefaultSiteTypeWeights() SiteTypeWeights { return SiteTypeWeights{Construction: 2.6, Power: 4} }

// siteColdC is the native PlantProperties.minOptimalGrowthTemperature:
// growth falls linearly to nothing between it and 0C, and a cell below 0C
// cannot be sown, so a room under it is heated or refused.
const siteColdC = 6.0

// siteLampDuty is the fraction of the day a sun lamp draws power: its
// CompProperties_Schedule runs 0.25-0.8 of the day, the plants' growing
// hours. Basins and heaters draw all day.
const siteLampDuty = 0.55

// siteSteelEquivalents prices cost-list resources in steel by market value
// (a component is 32 silver to steel's 1.9); unlisted resources count as
// steel.
var siteSteelEquivalents = map[Resource]float64{"Steel": 1, "ComponentIndustrial": 17}

// siteSteelEquivalent is a building's cost list in steel; an unknown list
// prices as 100 steel so every kind still pays for what it builds.
func siteSteelEquivalent(infra Infrastructure) float64 {
	costs, known := infra.Costs.Value()
	if !known {
		return 100
	}
	total := 0.0
	for _, c := range costs {
		rate, listed := siteSteelEquivalents[c.Resource]
		if !listed {
			rate = 1
		}
		total += rate * float64(c.Count)
	}
	return total
}

// siteConstructionCharge is the daily unit cost of count new buildings.
func siteConstructionCharge(w SiteTypeWeights, unit float64, infra Infrastructure, count int) float64 {
	return -w.Construction * unit * siteSteelEquivalent(infra) / 100 * float64(count)
}

// siteBasinCells is the native HydroponicsBasin footprint (1x4).
const siteBasinCells = 4

type SiteTypeRequest struct {
	Paste       *PasteSiteRequest
	Field       FieldRequest
	Environment domain.Fact[ControlledEnvironment]
	Lamp, Basin domain.Fact[Infrastructure]
	Heater      domain.Fact[Infrastructure]
	// LampGrowthRadius is the native Building_SunLamp growth radius in cells.
	LampGrowthRadius float64
	Weights          SiteTypeWeights
}

// siteHydroponicTag is the native sow tag a HydroponicsBasin accepts. A new
// basin sows its definition's default crop; PlanGrowerCrops re-crops it to
// the candidate's crop once it is built.
const siteHydroponicTag = "Hydroponic"

// SiteBuilding is one placement a construction kind needs before its cells
// can grow.
type SiteBuilding struct {
	Definition string
	Cell       domain.Cell
	Rotation   domain.Rotation
}

// SiteTypeCandidate records one crop under one kind. Score is net nutrition
// per day divided by the cells the crop needs, comparable across kinds.
type SiteTypeCandidate struct {
	Kind      SiteKind
	Crop      CropChoice
	Needed    int
	Sites     FarmSitePlan
	Buildings []SiteBuilding
	Cells     int
	Score     float64
	Terms     []FarmSiteTerm
	Reason    string
}

type SiteTypePlan struct {
	Kind       SiteKind
	Crop       CropChoice
	Needed     int
	Sites      FarmSitePlan
	Buildings  []SiteBuilding
	Urgent     bool
	Candidates []SiteTypeCandidate
}

func (p SiteTypePlan) Explain() string {
	var b strings.Builder
	fmt.Fprintf(&b, "%s %s needed=%d urgent=%t buildings=%d", p.Kind, p.Crop.Name, p.Needed, p.Urgent, len(p.Buildings))
	for _, c := range p.Candidates {
		fmt.Fprintf(&b, "\n %s %s needed=%d cells=%d score=%.4f", c.Kind, c.Crop.Name, c.Needed, c.Cells, c.Score)
		for _, t := range c.Terms {
			fmt.Fprintf(&b, " %s=%.4f", t.Name, t.Value)
		}
		if c.Reason != "" {
			fmt.Fprintf(&b, " %s", c.Reason)
		}
	}
	return b.String()
}

// PlanSiteType chooses the crop, the site kind and the cells jointly. Outdoor
// soil is PlanField. Controlled kinds screen crops without the outdoor season,
// take only cells the native environment census supports (a running lamp's
// growth cells, roofed indoor soil, unlit rooms for dark crops), and charge
// construction, power against the best network's observed day or night
// headroom, and a heater when the room or outdoors is below siteColdC. A kind whose
// power, heating, crop compatibility or infrastructure is unavailable is kept
// as an excluded candidate with its reason. Unknown environment facts yield
// only the outdoor candidates.
func PlanSiteType(r SiteTypeRequest) (SiteTypePlan, bool) {
	if r.Paste != nil {
		return planPasteSite(r)
	}
	w := r.Weights
	if w == (SiteTypeWeights{}) {
		w = DefaultSiteTypeWeights()
	}
	if !foodNumber(w.Construction) || !foodNumber(w.Power) || !(r.LampGrowthRadius >= 0 && r.LampGrowthRadius <= 64) {
		return SiteTypePlan{}, false
	}
	plan := SiteTypePlan{}
	field, ok := PlanField(r.Field)
	sowing, sk := r.Field.Climate.Sowing.Value()
	for _, c := range field.Candidates {
		reason := c.Reason
		if !sk || !sowing {
			reason = "outdoor sowing not possible"
		}
		plan.Candidates = append(plan.Candidates, SiteTypeCandidate{Kind: SiteOutdoor, Crop: c.Crop, Needed: c.Needed, Sites: c.Sites, Cells: c.Sites.Cells, Score: c.Score, Terms: c.Terms, Reason: reason})
	}
	if !ok && (!sk || !sowing) && len(field.Candidates) == 0 {
		for _, crop := range r.Field.Choices {
			plan.Candidates = append(plan.Candidates, SiteTypeCandidate{Kind: SiteOutdoor, Crop: crop, Reason: "outdoor sowing not possible"})
		}
	}
	urgent := field.Urgent
	if env, known := r.Environment.Value(); known {
		viables, excluded, vk := viableCrops(r.Field, true)
		if vk {
			urgent = urgent || fieldUrgency(r.Field, viables, true)
			for _, kind := range []SiteKind{SiteGreenhouseReuse, SiteDarkRoom, SiteGreenhouseNew, SiteHydroponics} {
				for _, c := range excluded {
					plan.Candidates = append(plan.Candidates, SiteTypeCandidate{Kind: kind, Crop: c.Crop, Reason: c.Reason})
				}
				for _, v := range viables {
					candidate := siteCandidate(r, w, env, kind, v)
					if candidate.Cells > 0 {
						candidate.Terms = append(candidate.Terms, cropChoiceTerms(r.Field, v.crop, candidate.Cells)...)
						candidate.Terms = append(candidate.Terms, siteRiskTerms(r.Field, v.crop, candidate.Cells)...)
						total := 0.0
						for _, term := range candidate.Terms {
							total += term.Value
						}
						candidate.Score = total / float64(v.needed)
					}
					plan.Candidates = append(plan.Candidates, candidate)
				}
			}
		}
	}
	plan.Urgent = urgent
	sort.SliceStable(plan.Candidates, func(i, j int) bool { return siteCandidateLess(plan.Candidates[i], plan.Candidates[j], urgent) })
	if len(plan.Candidates) == 0 || plan.Candidates[0].Cells == 0 {
		return plan, false
	}
	best := plan.Candidates[0]
	plan.Kind, plan.Crop, plan.Needed, plan.Sites, plan.Buildings = best.Kind, best.Crop, best.Needed, best.Sites, best.Buildings
	return plan, true
}

func siteCandidateLess(a, b SiteTypeCandidate, urgent bool) bool {
	if (a.Cells > 0) != (b.Cells > 0) {
		return a.Cells > 0
	}
	ad, _ := a.Crop.GrowDays.Value()
	bd, _ := b.Crop.GrowDays.Value()
	if urgent && ad != bd {
		return ad < bd
	}
	if a.Score != b.Score {
		return a.Score > b.Score
	}
	if siteKindOrder[a.Kind] != siteKindOrder[b.Kind] {
		return siteKindOrder[a.Kind] < siteKindOrder[b.Kind]
	}
	if ad != bd {
		return ad < bd
	}
	return a.Crop.Name < b.Crop.Name
}

// siteTerms sums the selected patches' terms by name.
func siteTerms(sites FarmSitePlan) []FarmSiteTerm {
	totals := map[string]float64{}
	var order []string
	for _, s := range sites.Selected {
		for _, t := range s.Terms {
			if _, seen := totals[t.Name]; !seen {
				order = append(order, t.Name)
			}
			totals[t.Name] += t.Value
		}
	}
	var out []FarmSiteTerm
	for _, name := range order {
		out = append(out, FarmSiteTerm{name, totals[name]})
	}
	return out
}

func cropRate(crop CropChoice, soil float64) float64 {
	days, _ := crop.GrowDays.Value()
	yield, _ := crop.HarvestNutrition.Value()
	sensitivity, _ := crop.FertilitySensitivity.Value()
	return yield * math.Max(0, 1+(soil-1)*sensitivity) / days
}

// sitePower picks the network with the most headroom for a draw that runs
// by day (lamps follow the sun schedule) or all night (basins, heaters).
// Networks without an active source never qualify; a network without
// batteries must carry a night load through a calm night.
func sitePower(env ControlledEnvironment, drawW float64, night bool) (PowerHeadroom, float64, bool) {
	best, bestRoom, found := PowerHeadroom{}, 0.0, false
	for _, n := range env.Networks {
		if !positive(n.ActiveSource) {
			continue
		}
		var room domain.Fact[float64]
		if !night {
			generation, gk := n.GenerationW.Value()
			consumption, ck := n.ConsumptionW.Value()
			if gk && ck && foodNumber(generation) && foodNumber(consumption) {
				room = domain.Known(generation - consumption)
			}
		} else if capacity, ck := n.CapacityWattDays.Value(); ck && capacity > 0 {
			room = n.NightHeadroomW()
		} else {
			room = n.CalmNightHeadroomW()
		}
		v, known := room.Value()
		if known && (!found || v > bestRoom || v == bestRoom && n.ID < best.ID) {
			best, bestRoom, found = n, v, true
		}
	}
	return best, bestRoom, found && bestRoom >= drawW
}

func siteCandidate(r SiteTypeRequest, w SiteTypeWeights, env ControlledEnvironment, kind SiteKind, v viableCrop) SiteTypeCandidate {
	c := SiteTypeCandidate{Kind: kind, Crop: v.crop, Needed: v.needed}
	if kind != SiteDarkRoom && GrowsInDark(v.crop) {
		c.Reason = "crop needs a dark room"
		return c
	}
	minimum, mk := v.crop.FertilityMin.Value()
	if !mk || !fieldPositive(minimum) {
		c.Reason = "incomplete crop facts"
		return c
	}
	unit := cropRate(v.crop, 1)
	if !fieldPositive(unit) {
		c.Reason = "incomplete crop facts"
		return c
	}
	lit := env.LitCells()
	lamps := map[string]GrowLight{}
	for _, l := range env.Lights {
		lamps[l.ID] = l
	}
	rooms := map[string]GrowRoom{}
	for _, room := range env.Rooms {
		rooms[room.ID] = room
	}
	outdoorCold := false
	if t, known := env.OutdoorTemperatureC.Value(); known && t < siteColdC {
		outdoorCold = true
	}
	charge := func(name string, value float64) { c.Terms = append(c.Terms, FarmSiteTerm{name, value}) }
	// heat charges one heater per cold room on the night headroom, or refuses.
	heat := func(coldRooms int, network string) bool {
		if coldRooms == 0 {
			return true
		}
		heater, hk := r.Heater.Value()
		draw, dk := heater.PowerW.Value()
		if !hk || !positive(heater.Available) || !dk || !foodNumber(draw) {
			c.Reason = "room too cold and no heater available"
			return false
		}
		total := draw * float64(coldRooms)
		if network != "" {
			n, ok := env.Network(network)
			room, rk := n.CalmNightHeadroomW().Value()
			if !ok || !positive(n.ActiveSource) || !rk || room < total {
				c.Reason = "no night power headroom for heating"
				return false
			}
		} else if _, _, ok := sitePower(env, total, true); !ok {
			c.Reason = "no night power headroom for heating"
			return false
		}
		charge("heating", -w.Power*unit*total/1000+siteConstructionCharge(w, unit, heater, coldRooms))
		return true
	}
	roofedIndoorSoil := func(s SiteCell) bool {
		return positive(s.Roofed) && positive(s.Indoors)
	}
	switch kind {
	case SiteHydroponics:
		if positive(v.crop.RequiresPollution) || GrowsInDark(v.crop) {
			c.Reason = "crop needs a compatible soil or dark site"
			return c
		}
		return siteHydroponics(r, w, env, v, c, lit, unit)
	case SiteGreenhouseReuse:
		if len(lit) == 0 {
			c.Reason = "no running sun lamp"
			return c
		}
		site := siteRestricted(r.Field.Site, func(s SiteCell) bool { _, ok := lit[s.Cell]; return ok && positive(s.Roofed) }, nil)
		c.Sites = siteFarm(site, v)
		c.Cells = c.Sites.Cells
		if c.Cells == 0 {
			c.Reason = "no free lit soil"
			return c
		}
		c.Terms = siteTerms(c.Sites)
		cold := map[string]bool{}
		network := ""
		for _, patch := range c.Sites.Patches {
			for _, cell := range rectCells(patch) {
				lamp := lamps[lit[cell]]
				if id, known := lamp.Network.Value(); known && network == "" {
					network = id
				}
				if id, known := lamp.Room.Value(); known {
					if t, tk := rooms[id].TemperatureC.Value(); tk && t < siteColdC {
						cold[id] = true
					}
				}
			}
		}
		if !heat(len(cold), network) {
			c.Cells, c.Sites = 0, FarmSitePlan{}
			return c
		}
	case SiteDarkRoom:
		if !GrowsInDark(v.crop) {
			c.Reason = "crop needs light"
			return c
		}
		site := siteRestricted(r.Field.Site, func(s SiteCell) bool { _, isLit := lit[s.Cell]; return roofedIndoorSoil(s) && !isLit }, nil)
		c.Sites = siteFarm(site, v)
		c.Cells = c.Sites.Cells
		if c.Cells == 0 {
			c.Reason = "no free unlit roofed soil"
			return c
		}
		c.Terms = siteTerms(c.Sites)
		cold := 0
		if outdoorCold {
			cold = 1
		}
		if !heat(cold, "") {
			c.Cells, c.Sites = 0, FarmSitePlan{}
			return c
		}
	case SiteGreenhouseNew:
		lamp, lk := r.Lamp.Value()
		draw, dk := lamp.PowerW.Value()
		if !lk || !positive(lamp.Available) || !dk || !foodNumber(draw) || lamp.Name == "" {
			c.Reason = "sun lamp unavailable"
			return c
		}
		eligible := map[domain.Cell]bool{}
		for _, s := range r.Field.Site.Cells {
			_, isLit := lit[s.Cell]
			if soil, ok := freeCropSoil(siteUnroofed(s), minimum, nil); ok && roofedIndoorSoil(s) && !isLit && foodNumber(soil) {
				eligible[s.Cell] = true
			}
		}
		center, covered := siteLampCenter(eligible, r.LampGrowthRadius)
		if covered == 0 {
			c.Reason = "no free unlit roofed soil"
			return c
		}
		disc := func(s SiteCell) bool {
			return eligible[s.Cell] && s.Cell != center && siteWithin(s.Cell, center, r.LampGrowthRadius)
		}
		site := siteRestricted(r.Field.Site, disc, []domain.Cell{center})
		c.Sites = siteFarm(site, v)
		c.Cells = c.Sites.Cells
		if c.Cells == 0 {
			c.Reason = "no free unlit roofed soil"
			return c
		}
		network, _, ok := sitePower(env, draw, false)
		if !ok {
			c.Cells, c.Sites, c.Reason = 0, FarmSitePlan{}, "no daytime power headroom for a sun lamp"
			return c
		}
		c.Terms = siteTerms(c.Sites)
		charge("construction", siteConstructionCharge(w, unit, lamp, 1))
		charge("power", -w.Power*unit*draw*siteLampDuty/1000)
		c.Buildings = []SiteBuilding{{Definition: lamp.Name, Cell: center, Rotation: domain.North}}
		cold := 0
		if outdoorCold {
			cold = 1
		}
		if !heat(cold, network.ID) {
			c.Cells, c.Sites, c.Buildings = 0, FarmSitePlan{}, nil
			return c
		}
	}
	total := 0.0
	for _, t := range c.Terms {
		total += t.Value
	}
	if math.IsNaN(total) || math.IsInf(total, 0) {
		return SiteTypeCandidate{Kind: kind, Crop: v.crop, Needed: v.needed, Reason: "unscorable"}
	}
	c.Score = total / float64(v.needed)
	c.Reason = fmt.Sprintf("net %.4f/day over %d patches", total, len(c.Sites.Patches))
	return c
}

// siteHydroponics places basins on lit roofed floor under a running lamp
// for any crop carrying the Hydroponic sow tag. Basins ignore soil and draw
// all night, so the count is capped by the best network's night headroom; travel is the walked-step proxy of Manhattan
// distance from the anchor since basins are not square patches.
func siteHydroponics(r SiteTypeRequest, w SiteTypeWeights, env ControlledEnvironment, v viableCrop, c SiteTypeCandidate, lit map[domain.Cell]string, unit float64) SiteTypeCandidate {
	basin, bk := r.Basin.Value()
	draw, dk := basin.PowerW.Value()
	fertility, fk := basin.Fertility.Value()
	if !bk || !positive(basin.Available) || !dk || !foodNumber(draw) || !fk || !fieldPositive(fertility) || basin.Name == "" {
		c.Reason = "hydroponics basin unavailable"
		return c
	}
	tags, tk := v.crop.SowTags.Value()
	if !tk || !containsString(tags, siteHydroponicTag) {
		c.Reason = "crop cannot be sown in a basin"
		return c
	}
	if len(lit) == 0 {
		c.Reason = "no running sun lamp"
		return c
	}
	blocked := map[domain.Cell]bool{}
	for _, cell := range r.Field.Site.Protected {
		blocked[cell] = true
	}
	free := map[domain.Cell]bool{}
	for _, s := range r.Field.Site.Cells {
		_, isLit := lit[s.Cell]
		if isLit && !blocked[s.Cell] && positive(s.Roofed) && positive(s.Walkable) && siteKnownFalse(s.Occupied) && siteKnownFalse(s.Zone) {
			free[s.Cell] = true
		}
	}
	_, room, ok := sitePower(env, draw, true)
	if !ok {
		c.Reason = "no night power headroom for a basin"
		return c
	}
	limit := int(math.Ceil(float64(v.needed) / siteBasinCells))
	if draw > 0 {
		limit = min(limit, int(math.Floor(room/draw)))
	}
	limit = min(limit, farmSitePatchLimit)
	ordered := make([]domain.Cell, 0, len(free))
	for cell := range free {
		ordered = append(ordered, cell)
	}
	sort.Slice(ordered, func(i, j int) bool {
		a, b := ordered[i], ordered[j]
		da, db := siteManhattan(a, r.Field.Site.Anchor), siteManhattan(b, r.Field.Site.Anchor)
		if da != db {
			return da < db
		}
		return cellLess(a, b)
	})
	taken := map[domain.Cell]bool{}
	walk := 0
	for _, cell := range ordered {
		if len(c.Buildings) >= limit {
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
			walk += siteManhattan(f, r.Field.Site.Anchor)
		}
		c.Buildings = append(c.Buildings, SiteBuilding{Definition: basin.Name, Cell: cell, Rotation: domain.North})
	}
	if len(c.Buildings) == 0 {
		c.Reason = "no free lit floor for a basin"
		return c
	}
	n := len(c.Buildings)
	c.Cells = n * siteBasinCells
	weights := r.Field.Site.Weights
	if weights == (FarmSiteWeights{}) {
		weights = DefaultFarmSiteWeights()
	}
	c.Terms = []FarmSiteTerm{{"yield", cropRate(v.crop, fertility) * float64(c.Cells)}, {"travel", -weights.Travel * unit * float64(walk)}, {"construction", siteConstructionCharge(w, unit, basin, n)}, {"power", -w.Power * unit * draw * float64(n) / 1000}}
	total := 0.0
	for _, t := range c.Terms {
		total += t.Value
	}
	c.Score = total / float64(v.needed)
	c.Reason = fmt.Sprintf("net %.4f/day over %d basins", total, n)
	return c
}

func containsString(values []string, want string) bool {
	for _, v := range values {
		if v == want {
			return true
		}
	}
	return false
}

func siteManhattan(a, b domain.Cell) int {
	return int(math.Abs(float64(a.X-b.X)) + math.Abs(float64(a.Z-b.Z)))
}

func siteWithin(a, center domain.Cell, radius float64) bool {
	dx, dz := float64(a.X-center.X), float64(a.Z-center.Z)
	return math.Sqrt(dx*dx+dz*dz) <= radius
}

// siteBasinFootprint is the native occupied rect of a north-facing 1x4 basin
// placed at anchor: RimWorld centres an even-length footprint one cell
// below the placement cell (GenAdj.OccupiedRect), so the basin spans z-1
// through z+2.
func siteBasinFootprint(anchor domain.Cell) []domain.Cell {
	out := make([]domain.Cell, 0, siteBasinCells)
	for i := int32(-(siteBasinCells - 1) / 2); i < siteBasinCells-(siteBasinCells-1)/2; i++ {
		out = append(out, domain.Cell{X: anchor.X, Z: anchor.Z + i})
	}
	return out
}

// siteLampCenter is the eligible cell whose growth disc covers the most
// eligible cells; ties go to the lexically smaller cell.
func siteLampCenter(eligible map[domain.Cell]bool, radius float64) (domain.Cell, int) {
	cells := make([]domain.Cell, 0, len(eligible))
	for c := range eligible {
		cells = append(cells, c)
	}
	sort.Slice(cells, func(i, j int) bool { return cellLess(cells[i], cells[j]) })
	best, covered := domain.Cell{}, 0
	for _, center := range cells {
		n := 0
		for _, c := range cells {
			if c != center && siteWithin(c, center, radius) {
				n++
			}
		}
		if n > covered {
			best, covered = center, n
		}
	}
	return best, covered
}

func siteUnroofed(s SiteCell) SiteCell {
	s.Roofed = domain.Known(false)
	return s
}

// siteRestricted rewrites the census so only cells the kind supports are
// plantable: they are presented as unroofed so PlanFarmSites accepts them,
// every other cell keeps its walkability for travel but has no soil. A
// restricted site fills a lamp disc or a room, not a grid module, so the
// colony grid is dropped and the planner lays its size ladder (#608).
func siteRestricted(site FarmSiteRequest, keep func(SiteCell) bool, protected []domain.Cell) FarmSiteRequest {
	out := site
	out.Grid = domain.Unknown[ColonyGrid]()
	out.Cells = make([]SiteCell, 0, len(site.Cells))
	for _, s := range site.Cells {
		if keep(s) {
			out.Cells = append(out.Cells, siteUnroofed(s))
		} else {
			s.Fertility = domain.Known(0.0)
			out.Cells = append(out.Cells, s)
		}
	}
	out.Protected = append(append([]domain.Cell{}, site.Protected...), protected...)
	return out
}

func siteFarm(site FarmSiteRequest, v viableCrop) FarmSitePlan {
	site.Crop = v.crop
	site.Needed = v.needed
	return PlanFarmSites(site)
}

func siteKnownFalse(f domain.Fact[bool]) bool { v, k := f.Value(); return k && !v }

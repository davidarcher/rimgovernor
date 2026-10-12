package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The site embedder (EmbedSites) as a run that places one node at a time, so
// the power planner can quote a candidate site, take it and quote the next
// (derivePowerSites) without placing the sites before it again.
//
// A free node's origin is priced in sustained watts, the currency of BuildW and
// unitsW, and the cheapest wins; ties go to the lowest cell, so the result
// depends only on the inputs. A candidate costs
//
//	lane       the output its wind lane loses (blocked cells x windBlockedLoss
//	           x WindW), and the output its footprint takes from lanes already
//	           placed: lanes overlap each other and fields, so a site stands
//	           where the loss is least, not where it is none
//	conduit    the cells of conduit between its footprint and the nearest room
//	           or placed site (the Manhattan gap) x ConduitW
//	wall       the ring cells its footprint adds to the enclosure x WallW, read
//	           off the rectangles as layout_embed_enclosure.go does
//	ground     the soil units its footprint covers through unitsW: the harvest
//	           of a normal-soil cell is one unit, field ground below
//	           zoneFieldFertility yields none
//
// so the distance to the rooms falls out of the conduit and the wall, and no
// distance is a rule.

// SitePrice is what a free site node is priced by. WindW is the watts a turbine
// delivers with its lane clear (its nominal output times the profile's daily
// share); ConduitW and WallW the watts a cell of conduit and a cell of wall
// cost to build (BuildW of the def's cheapest cost list). Weights are the
// layout's site weights; the zero value is DefaultSiteTypeWeights.
type SitePrice struct {
	Weights         SiteTypeWeights
	WindW           float64
	ConduitW, WallW float64
}

func (p SitePrice) weights() SiteTypeWeights {
	if p.Weights == (SiteTypeWeights{}) {
		return DefaultSiteTypeWeights()
	}
	return p.Weights
}

// soilFixed is the fixed-point scale of a soil unit in the ground prefix.
const soilFixed = 1000

// siteQuote is the cheapest placement of a node: the turn and origin, the
// cells blocked in its own lane and the price in watts, split into what the
// wind loses (LaneW, its own lane and the lanes it blocks) and the rest.
type siteQuote struct {
	shape   SiteNode
	origin  domain.Cell
	blocked int
	LaneW   float64
	CostW   float64
}

// PriceW is the quote's whole price.
func (q siteQuote) PriceW() float64 { return q.LaneW + q.CostW }

// placedLane is a wind lane already placed: its cells, the footprint it
// belongs to (never a blocker of it) and the cells now blocked in it.
type placedLane struct {
	region  Region
	own     Region
	mask    []bool
	blocked int
}

type siteRun struct {
	in    SiteEmbedInput
	e     *embedder
	price SitePrice
	w, h  int32

	ids    map[string]bool
	rooms  []Rectangle // placed room interiors
	fields []Rectangle // placed field interiors: no footprint stands on one
	taken  []Region
	// foot are the placed footprints' bounds, the conduit's other end.
	foot   []Rectangle
	ground *windGround
	lanes  []placedLane

	// units is the soil units a cell yields, times soilFixed.
	units prefix
	// The wall prefixes count the walkable ground the enclosure does not yet
	// cover: out to the killbox yard (wallIn) and out to the wall's thickness
	// beyond it (wallGrown), as enclosureCost reads them.
	walk                []bool
	coverIn, coverGrown []bool
	wallIn, wallGrown   prefix

	out SiteEmbedding
}

func newSiteRun(in SiteEmbedInput) (*siteRun, error) {
	depth, _, err := roomLimits(in.Embed.RoofDistance)
	if err != nil {
		return nil, err
	}
	scorer := newPlanScorer(in.Embed.Zones, in.Embed.Reservations, in.Embed.Survey)
	r := &siteRun{in: in, price: in.Price, ids: map[string]bool{}, ground: newWindGround(in)}
	r.e = newEmbedder(in.Embed, depth, scorer, in.Embedding.Level)
	r.w, r.h = in.Embed.Survey.Bounds.Width, in.Embed.Survey.Bounds.Height
	n := int(max(r.w*r.h, 0))
	units, walk := make([]int, n), make([]bool, n)
	for i := range walk {
		walk[i] = true // an unread cell is not impassable
	}
	for _, c := range in.Embed.Survey.Cells {
		if c.Cell.X < 0 || c.Cell.Z < 0 || c.Cell.X >= r.w || c.Cell.Z >= r.h {
			continue
		}
		i := int(c.Cell.Z*r.w + c.Cell.X)
		walk[i] = !(c.Rock || c.Hazard || !c.Walkable && !c.Built)
		if !c.Rock && c.Fertility >= zoneFieldFertility {
			units[i] = int(c.Fertility * soilFixed)
		}
	}
	r.walk = walk
	r.units = newPrefix(r.w, r.h, func(c domain.Cell) int { return units[int(c.Z*r.w+c.X)] })
	r.coverIn, r.coverGrown = make([]bool, n), make([]bool, n)
	for _, b := range in.Embedding.Blocks {
		r.rooms = append(r.rooms, b.Interior)
		r.cover(b.Interior)
	}
	for _, f := range in.Embedding.Fields {
		r.fields = append(r.fields, f.Interior)
	}
	r.rebuildWall()
	return r, nil
}

// admit claims a node's id; a node needs a unique id and a footprint.
func (r *siteRun) admit(n SiteNode) error {
	if n.ID == "" || r.ids[n.ID] || n.Footprint.Empty() {
		return fmt.Errorf("%w: %q needs a unique id and a footprint", ErrSiteNode, n.ID)
	}
	r.ids[n.ID] = true
	return nil
}

// cover marks the enclosure a block at b needs: its wall cells and the killbox
// yard (perimeterGap), and the ring cells (perimeterThick) outside them.
func (r *siteRun) cover(b Rectangle) {
	const yard = 1 + perimeterGap
	onMap := Rectangle{Width: r.w, Height: r.h}
	mark := func(cover []bool, rc Rectangle) {
		rc = clipRect(rc, onMap)
		for z := rc.Z; z < rc.Z+rc.Height; z++ {
			for x := rc.X; x < rc.X+rc.Width; x++ {
				cover[int(z*r.w+x)] = true
			}
		}
	}
	mark(r.coverIn, pad(b, yard))
	mark(r.coverGrown, pad(b, yard+perimeterThick))
}

func (r *siteRun) rebuildWall() {
	r.wallIn = newPrefix(r.w, r.h, func(c domain.Cell) int { i := int(c.Z*r.w + c.X); return b2i(r.walk[i] && !r.coverIn[i]) })
	r.wallGrown = newPrefix(r.w, r.h, func(c domain.Cell) int { i := int(c.Z*r.w + c.X); return b2i(r.walk[i] && !r.coverGrown[i]) })
}

// wallCells is the ring cells a footprint at b adds to the enclosure: the
// walkable ground its grown extent covers beyond the enclosure, less the
// walkable ground its own yard turns from ring into interior.
func (r *siteRun) wallCells(b Rectangle) int {
	const yard = 1 + perimeterGap
	onMap := Rectangle{Width: r.w, Height: r.h}
	return max(int(r.wallGrown.sum(clipRect(pad(b, yard+perimeterThick), onMap))-r.wallIn.sum(clipRect(pad(b, yard), onMap))), 0)
}

// conduitCells is the conduit run a footprint at b needs: the Manhattan gap to
// the nearest room or placed site; none while nothing stands.
func (r *siteRun) conduitCells(b Rectangle) int {
	best := -1
	consider := func(t Rectangle) {
		d := max(axisGap(b.X, b.Width, t.X, t.Width), 0) + max(axisGap(b.Z, b.Height, t.Z, t.Height), 0)
		if best < 0 || d < best {
			best = d
		}
	}
	for _, t := range r.rooms {
		consider(t)
	}
	for _, t := range r.foot {
		consider(t)
	}
	return max(best, 0)
}

// freeRect reports a footprint at b off the rooms' walls, the fields and the
// footprints already taken. Lanes never enter into it.
func (r *siteRun) freeRect(b Rectangle) bool {
	for _, t := range r.rooms {
		if collides(b, t) {
			return false
		}
	}
	for _, t := range r.fields {
		if axisGap(b.X, b.Width, t.X, t.Width) < 0 && axisGap(b.Z, b.Height, t.Z, t.Height) < 0 {
			return false
		}
	}
	for i, t := range r.foot {
		if axisGap(b.X, b.Width, t.X, t.Width) < 0 && axisGap(b.Z, b.Height, t.Z, t.Height) < 0 && (r.taken[i].IsRect() || r.taken[i].OverlapsRect(b)) {
			return false
		}
	}
	return true
}

func (r *siteRun) freeRegion(fp Region) bool {
	b := fp.Bounds()
	for _, t := range r.rooms {
		if collides(b, t) {
			return false
		}
	}
	for _, t := range r.fields {
		if fp.OverlapsRect(t) {
			return false
		}
	}
	return !slices.ContainsFunc(r.taken, fp.Overlaps)
}

// inCore reports every cell of a footprint on the level's core ground.
func (r *siteRun) inCore(fp Region) bool {
	for _, c := range fp.Cells() {
		cell := Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}
		if !r.e.level.inside(cell) || r.e.level.sum(cell) != 1 {
			return false
		}
	}
	return true
}

// inflicted is the output a footprint at b takes from the wind lanes already
// placed, in watts: the lane cells it newly blocks.
func (r *siteRun) inflicted(b Rectangle) float64 {
	loss := 0.0
	for _, l := range r.lanes {
		add := 0
		for z := b.Z; z < b.Z+b.Height; z++ {
			for x := b.X; x < b.X+b.Width; x++ {
				if l.mask[int(z*r.w+x)] && !r.ground.cells[domain.Cell{X: x, Z: z}] {
					add++
				}
			}
		}
		if add > 0 {
			loss += (WindOutput(l.blocked) - WindOutput(l.blocked+add)) * r.price.WindW
		}
	}
	return loss
}

// quote is the cheapest placement of a free node over its turns, or false when
// no ground holds it. It changes nothing.
func (r *siteRun) quote(n SiteNode) (siteQuote, bool) {
	weights := r.price.weights()
	var best siteQuote
	found := false
	for _, shape := range append([]SiteNode{n}, n.Alts...) {
		box := shape.Footprint.Bounds()
		rect := shape.Footprint.IsRect()
		for _, o := range r.e.core {
			b := Rectangle{X: box.X + o.X, Z: box.Z + o.Z, Width: box.Width, Height: box.Height}
			if !r.e.level.inside(b) {
				continue
			}
			if rect {
				if r.e.level.sum(b) != int64(b.Width)*int64(b.Height) || !r.freeRect(b) {
					continue
				}
			} else if fp := shape.Footprint.Translate(o.X, o.Z); !r.inCore(fp) || !r.freeRegion(fp) {
				continue
			}
			q := siteQuote{shape: shape, origin: o}
			units := float64(r.units.sum(b)) / soilFixed
			if !rect {
				units = 0
				for _, c := range shape.Footprint.Translate(o.X, o.Z).Cells() {
					units += float64(r.units.sum(Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})) / soilFixed
				}
			}
			q.CostW = float64(r.conduitCells(b))*r.price.ConduitW + float64(r.wallCells(b))*r.price.WallW + unitsW(weights, units)
			if found && q.CostW >= best.PriceW() {
				continue
			}
			for _, l := range shape.Lanes {
				if l.Wind {
					q.blocked += r.ground.blockedAt(l.Region, o.X, o.Z, b)
				}
			}
			q.LaneW = (WindOutput(0)-WindOutput(q.blocked))*r.price.WindW + r.inflicted(b)
			if !found || q.PriceW() < best.PriceW() {
				best, found = q, true
			}
		}
	}
	return best, found
}

// place puts a node on its anchor, or at the cheapest origin; a node no ground
// holds is left out with its decision.
func (r *siteRun) place(n SiteNode) {
	if n.Anchor != nil {
		p := SitePlacement{Node: n.ID, Replaces: n.Replaces, Rotation: n.Rotation, Origin: *n.Anchor, Anchored: true}
		p.Footprint = n.Footprint.Translate(p.Origin.X, p.Origin.Z)
		verdict, reason := EmbedPlace, ReasonSiteAnchored
		if !r.freeRegion(p.Footprint) {
			verdict, reason = EmbedUnsatisfied, ReasonSiteAnchorBlocked
		}
		r.add(p, n.Lanes, verdict, reason)
		return
	}
	q, ok := r.quote(n)
	if !ok {
		r.out.Decisions = append(r.out.Decisions, SiteDecision{Node: n.ID, Verdict: EmbedUnplaceable, Reason: ReasonSiteNoGround})
		return
	}
	r.take(n, q)
}

// take places a free node as quoted.
func (r *siteRun) take(n SiteNode, q siteQuote) {
	p := SitePlacement{Node: n.ID, Replaces: n.Replaces, Origin: q.origin, Rotation: q.shape.Rotation}
	p.Footprint = q.shape.Footprint.Translate(q.origin.X, q.origin.Z)
	r.add(p, q.shape.Lanes, EmbedPlace, ReasonSiteFree)
}

func (r *siteRun) add(p SitePlacement, lanes []SiteLane, verdict, reason string) {
	for _, l := range lanes {
		p.Lanes = append(p.Lanes, SiteLane{ID: l.ID, Region: l.Region.Translate(p.Origin.X, p.Origin.Z), Reason: l.Reason, Wind: l.Wind})
	}
	b := p.Footprint.Bounds()
	r.taken = append(r.taken, p.Footprint)
	r.foot = append(r.foot, b)
	r.ground.add(p.Footprint)
	r.cover(b)
	r.rebuildWall()
	for _, l := range p.Lanes {
		if !l.Wind {
			continue
		}
		mask := make([]bool, max(r.w*r.h, 0))
		for _, c := range l.Region.Cells() {
			if c.X >= 0 && c.Z >= 0 && c.X < r.w && c.Z < r.h {
				mask[int(c.Z*r.w+c.X)] = true
			}
		}
		r.lanes = append(r.lanes, placedLane{region: l.Region, own: p.Footprint, mask: mask})
	}
	for i := range r.lanes {
		r.lanes[i].blocked = r.ground.blocked(r.lanes[i].region, r.lanes[i].own)
	}
	r.out.Sites = append(r.out.Sites, p)
	r.out.Decisions = append(r.out.Decisions, SiteDecision{Node: p.Node, Verdict: verdict, Reason: reason})
}

// result is the placement so far, each wind lane priced against every
// footprint placed after it too.
func (r *siteRun) result() SiteEmbedding {
	out := SiteEmbedding{Decisions: slices.Clone(r.out.Decisions)}
	for _, p := range r.out.Sites {
		p.Lanes = slices.Clone(p.Lanes)
		out.Sites = append(out.Sites, p)
	}
	for i := range out.Sites {
		for j, l := range out.Sites[i].Lanes {
			if !l.Wind {
				continue
			}
			out.Sites[i].Lanes[j].Blocked = r.ground.blocked(l.Region, out.Sites[i].Footprint)
			if out.Sites[i].Lanes[j].Blocked > 0 {
				out.Decisions = append(out.Decisions, SiteDecision{Node: out.Sites[i].Node, Verdict: EmbedUnsatisfied, Reason: ReasonSiteWindBlocked})
			}
		}
	}
	return out
}

// EmbedSites places the site nodes around the rooms. Anchored nodes stand
// first, on their anchors; the rest take, in turn, the origin of least price
// (the file comment), so the result depends only on the inputs.
func EmbedSites(in SiteEmbedInput) (SiteEmbedding, error) {
	r, err := newSiteRun(in)
	if err != nil {
		return SiteEmbedding{}, err
	}
	for _, n := range in.Nodes {
		if err := r.admit(n); err != nil {
			return SiteEmbedding{}, err
		}
	}
	order := slices.Clone(in.Nodes)
	slices.SortStableFunc(order, func(a, b SiteNode) int { return b2i(b.Anchor != nil) - b2i(a.Anchor != nil) })
	for _, n := range order {
		r.place(n)
	}
	return r.result(), nil
}

package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Pens as outdoor nodes of the station graph (#2996, child of #2949). A herd's
// pen is the enclosure its animals live and graze in; it stands outdoors, so a
// haul between a pen and a store is a trip between the store and the outside
// node: clustering counts it as one (outdoor), and the tiler walks it to the
// plan's entrances like any other outside trip. Two flows leave a pen node:
//
//   - products: each herd animal yields Amount of a product every IntervalDays
//     (RaceProduct), milk, wool, eggs and chemfuel (the products the herd plan
//     keeps a job for), hauled to the store that holds them. Weight is items a
//     day, as the crop haul's is.
//   - feed: the herd eats AdultFeedPerDay a head. The pen is sized so its
//     grazing covers the herd in the worst quadrum (PenCells over the weakest
//     pasture terrain), at most penCellsCap cells; what that many cells do not
//     grow is hauled from the store in the race's feed item. A pen on ground
//     that grows nothing is hand-fed whole.
//
// Not modelled: the walk inside the enclosure (milking and shearing happen
// where the animal stands), slaughter, and a barn-only herd's trips to the barn
// (a room of the plan, not a node). An unread yield, hunger, pasture or feed
// item adds no flow: it is unknown, never zero. The node is a layout fact, not
// a station; clustering and the embedder ignore it.

// ReasonPenHerd is why the graph holds a pen: the herd the plan keeps of a race.
const ReasonPenHerd = "pen_herd_of_race"

// Reasons of the pen flows.
const (
	ReasonFlowPenProduct = "flow_pen_product"
	ReasonFlowPenFeed    = "flow_pen_feed"
)

// StationHerd is one race's herd: the planned animals of the race.
type StationHerd struct {
	Race    Resource
	Animals int
}

// PenNode is the pen of one race's herd.
type PenNode struct {
	ID      string
	Race    Resource
	Animals int
	// Hunger is the nutrition a day the herd eats (AdultFeedPerDay times the
	// animals); unknown with the race's feed.
	Hunger domain.Fact[float64]
	// HandFed is the nutrition a day the pen's grazing does not grow, hauled to
	// the pen; unknown while the hunger or the pasture is.
	HandFed domain.Fact[float64]
	Reason  string
}

func penID(race Resource) string { return "pen/" + string(race) }

// Pen is the pen node with the id.
func (g StationGraph) Pen(id string) (PenNode, bool) {
	for _, p := range g.Pens {
		if p.ID == id {
			return p, true
		}
	}
	return PenNode{}, false
}

// outdoor reports a node that is outside the base: the outside node or a pen.
func (g StationGraph) outdoor(id string) bool {
	if id == OutsideNode {
		return true
	}
	_, ok := g.Pen(id)
	return ok
}

// PenItems are the items the herds' pens move: their products and feed items.
// The caller reads their rot and stacks beside the recipes' items.
func PenItems(herds []StationHerd, races AnimalRaceCatalog) []Resource {
	var out []Resource
	for _, h := range herds {
		race, ok := races.Race(h.Race)
		if !ok || h.Animals <= 0 {
			continue
		}
		for _, p := range race.Products {
			if _, kept := herdProductJob(p); kept {
				out = append(out, p.Def)
			}
		}
		for _, f := range race.FeedItems {
			out = append(out, f.Def)
		}
	}
	slices.Sort(out)
	return slices.Compact(out)
}

// penNodes adds a node for each race of the pen herd the catalog knows and the
// haul edges of its products and feed.
func (b *graphBuilder) penNodes() []PenNode {
	var out []PenNode
	for _, h := range b.in.Population.Herds {
		race, ok := b.in.Races.Race(h.Race)
		if !ok || h.Animals <= 0 {
			continue
		}
		node := PenNode{ID: penID(h.Race), Race: h.Race, Animals: h.Animals, Hunger: domain.Unknown[float64](), HandFed: domain.Unknown[float64](), Reason: ReasonPenHerd}
		if feed, known := race.AdultFeedPerDay.Value(); known && foodNumber(feed) {
			node.Hunger = domain.Known(feed * float64(h.Animals))
		}
		for _, p := range race.Products {
			if _, kept := herdProductJob(p); !kept {
				continue
			}
			amount, ak := p.Amount.Value()
			interval, ik := p.IntervalDays.Value()
			if !ak || !ik || amount <= 0 || interval <= 0 {
				continue
			}
			if store, ok := b.storeFor(p.Def); ok {
				b.addEdge(node.ID, store, float64(h.Animals)*amount/interval, false, ReasonFlowPenProduct)
			}
		}
		b.penFeed(&node, race)
		out = append(out, node)
	}
	slices.SortFunc(out, func(a, c PenNode) int {
		switch {
		case a.ID < c.ID:
			return -1
		case a.ID > c.ID:
			return 1
		}
		return 0
	})
	return out
}

// penFeed sizes the pen's hand feeding: the hunger less what PenCells' worth of
// the weakest grazing terrain grows, hauled in the race's richest feed item (the
// fewest items) from the store that holds it.
func (b *graphBuilder) penFeed(node *PenNode, race AnimalRace) {
	hunger, hk := node.Hunger.Value()
	pasture, pk := b.in.Pasture.Value()
	if !hk || !pk {
		return
	}
	perCell, _ := pasture.Weakest()
	short := max(hunger-float64(PenCells(hunger, perCell))*perCell, 0)
	node.HandFed = domain.Known(short)
	if short <= 0 {
		return
	}
	var feed RaceFeedItem
	for _, f := range race.FeedItems {
		if foodNumber(f.Nutrition) && f.Nutrition > feed.Nutrition { // FeedItems are in name order: the first of equals
			feed = f
		}
	}
	if feed.Nutrition <= 0 {
		return
	}
	if store, ok := b.storeFor(feed.Def); ok {
		b.addEdge(node.ID, store, short/feed.Nutrition, false, ReasonFlowPenFeed)
	}
}

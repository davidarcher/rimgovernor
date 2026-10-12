package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Site nodes (#2884): the things the colony builds that have no walls and no
// door (turbines, solar, geothermal, the ring, gates, the killbox) as nodes of
// the station graph, beside the room nodes. One type serves them all. A node
// has a footprint of any shape, in cells relative to its origin; an optional
// fixed Anchor (a geyser, a vent) that the origin lands on; and Lanes, regions
// that travel with the footprint but reserve nothing: a turbine's wind lane
// may lie across fields and across other turbines' lanes. The embedder never
// refuses a node for a lane. A Wind lane is priced as the game prices it
// (layout_power_nodes.go): the cells that are roofed, off the map or held by a
// thing that blocks wind cost output, and a node with turns takes the one that
// loses least. Footprints are exclusive: they
// stay off the rooms' walls, the placed fields and each other, on ground the
// obstacle level the rooms were placed under leaves free. Like a room it may
// Replace a planned room. The station advice emits the power sites
// (PowerSiteNodes, derivePowerSites) and the tiler stores them.
//
// A free node takes the origin and turn of least price, and the price is one
// number in sustained watts (SitePrice, layout_site_run.go): the output its
// lane loses, the conduit run and the wall length it adds to the ring, and the
// soil its footprint covers. There is no distance rule and no points currency:
// a site far from the rooms pays for its conduit and its wall, and a site over
// good soil pays for the harvest it displaces.

// ErrSiteNode marks a site node the embedder cannot read.
var ErrSiteNode = errors.New("site node")

// Embedding decisions for site nodes; the caller journals them.
const (
	ReasonSiteAnchored = "site_placed_at_anchor"
	ReasonSiteFree     = "site_placed_in_envelope"
	ReasonSiteNoGround = "site_no_ground"
	// ReasonSiteAnchorBlocked: the anchor is fixed, so the node stands there
	// although its footprint touches a room's walls or another footprint.
	ReasonSiteAnchorBlocked = "site_anchor_blocked"
	// ReasonSiteWindBlocked: cells of the wind lane are roofed, off the map or
	// held by a thing that blocks wind; the node stands, at the output the
	// game leaves it.
	ReasonSiteWindBlocked = "site_wind_lane_blocked"
)

// SiteLane is a region that belongs to a site node and is never an exclusive
// reservation. Cells are relative to the node's origin.
type SiteLane struct {
	ID     string
	Region Region
	Reason string
	// Wind marks a turbine's wind path: the embedder prices it as the game does
	// (windBlocked, WindOutput) and Blocked is the cells found blocked.
	Wind    bool
	Blocked int
}

// WindOutput is the fraction of its rated output a wind lane leaves.
func (l SiteLane) WindOutput() float64 { return WindOutput(l.Blocked) }

// SiteNode is a site element of the station graph.
type SiteNode struct {
	ID string
	// Kind is the stored node kind of a power site the plan keeps as a
	// PlannedSite (PowerSitesOf); empty for a node the plan stores elsewhere.
	Kind NodeKind
	// Footprint is the cells the node stands on, relative to its origin.
	Footprint Region
	// Anchor is the cell the origin must land on; nil for a node that goes
	// anywhere the envelope and its band allow.
	Anchor *domain.Cell
	Lanes  []SiteLane
	// Replaces is the planned room ID this node supersedes; 0 for none.
	Replaces int32
	Reason   string
	// Rotation is the way the node faces (a turbine's wind side).
	Rotation domain.Rotation
	// Alts are the other shapes the same node may take, its turns: only
	// Footprint, Lanes and Rotation are read from them. A free node takes the
	// shape and origin of least price.
	Alts []SiteNode
}

// SitePlacement is where a site node landed. Lanes are in map cells, as the
// footprint is.
type SitePlacement struct {
	Node      string
	Origin    domain.Cell
	Footprint Region
	Lanes     []SiteLane
	Rotation  domain.Rotation
	Anchored  bool
	Replaces  int32
}

// SiteDecision is one site decision for the caller to journal.
type SiteDecision struct {
	Node    string
	Verdict string
	Reason  string
}

// SiteEmbedInput is the site nodes to place around an embedding of the rooms.
// Embed is the input the embedding was made from; Embedding is its result.
type SiteEmbedInput struct {
	Embed     EmbedInput
	Embedding Embedding
	Nodes     []SiteNode
	// Price is what a free node's origin is priced by; the zero value prices
	// the ground and nothing else, and ties go to the lowest cell.
	Price SitePrice
}

// SiteEmbedding is the placement of the site nodes.
type SiteEmbedding struct {
	Sites     []SitePlacement
	Decisions []SiteDecision
}

// Site is the placement of the node.
func (e SiteEmbedding) Site(id string) (SitePlacement, bool) {
	for _, s := range e.Sites {
		if s.Node == id {
			return s, true
		}
	}
	return SitePlacement{}, false
}

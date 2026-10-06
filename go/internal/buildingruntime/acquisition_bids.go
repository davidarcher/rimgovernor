package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A MaintainResource floor is acquired by the Round's supply plan (the resource
// planner's mines and bills, the acquisition planner's chops, harvests and
// hunts), a deep drill and a caravan (#728). The plan's winner is the single
// resource bid; the drill and the trade planner post their own best catalog
// score, and a resource goes to a fresh strictly higher bid. Rows 30 and 31 of
// epic #2140 move the drill and the caravan onto the plan and delete the board.
type acquisitionBidder string

const (
	bidResource  acquisitionBidder = "resource"
	bidDeepDrill acquisitionBidder = "deep_drill"
	bidTrade     acquisitionBidder = "trade"
)

// acquisitionBidTTL bounds how long a bid stands without being renewed: a
// planner that stopped evaluating a resource stops holding it back.
const acquisitionBidTTL domain.Tick = 5000

type acquisitionBid struct {
	score float64
	kind  policy.AcquisitionKind
	tick  domain.Tick
}

type acquisitionBidKey struct {
	snapshot domain.GenerationSnapshot
	resource policy.Resource
	from     acquisitionBidder
}

type acquisitionBoard struct {
	mu   sync.Mutex
	bids map[acquisitionBidKey]acquisitionBid
}

// bid records from's best score for resource (0 withdraws it) and reports
// whether another planner holds a fresh strictly higher bid, which the
// caller yields the resource to.
func (b *acquisitionBoard) bid(snapshot domain.GenerationSnapshot, resource policy.Resource, from acquisitionBidder, score float64, kind policy.AcquisitionKind, tick domain.Tick) (acquisitionBid, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.prune(snapshot, tick)
	own := acquisitionBidKey{snapshot, resource, from}
	if score > 0 {
		b.bids[own] = acquisitionBid{score, kind, tick}
	} else {
		delete(b.bids, own)
	}
	return b.rival(snapshot, resource, from, score)
}

// outranked is bid without posting: a one-shot method (a drill placed in
// the step it wins) that holds nothing afterwards.
func (b *acquisitionBoard) outranked(snapshot domain.GenerationSnapshot, resource policy.Resource, from acquisitionBidder, score float64, tick domain.Tick) (acquisitionBid, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.prune(snapshot, tick)
	return b.rival(snapshot, resource, from, score)
}

func (b *acquisitionBoard) rival(snapshot domain.GenerationSnapshot, resource policy.Resource, from acquisitionBidder, score float64) (acquisitionBid, bool) {
	var best acquisitionBid
	for key, bid := range b.bids {
		if key.resource == resource && key.from != from && bid.score > best.score {
			best = bid
		}
	}
	return best, best.score > score
}

func (b *acquisitionBoard) prune(snapshot domain.GenerationSnapshot, tick domain.Tick) {
	if b.bids == nil {
		b.bids = map[acquisitionBidKey]acquisitionBid{}
	}
	for key, old := range b.bids {
		if key.snapshot != snapshot || old.tick+acquisitionBidTTL < tick {
			delete(b.bids, key)
		}
	}
}

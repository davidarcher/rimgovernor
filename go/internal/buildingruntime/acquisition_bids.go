package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// MaintainResource is acquired by two planners on one goal (#728): the
// resource planner (bills, mining) and the acquisition planner (chop,
// harvest, hunt). Each posts its best catalog score per resource on the
// reviewer's board and yields a resource to a fresh higher bid from the
// other, so the two rank jointly instead of first-step-wins.
type acquisitionBidder string

const (
	bidResource    acquisitionBidder = "resource"
	bidAcquisition acquisitionBidder = "acquisition"
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
// whether the other planner holds a fresh strictly higher bid, which the
// caller yields the resource to.
func (b *acquisitionBoard) bid(snapshot domain.GenerationSnapshot, resource policy.Resource, from acquisitionBidder, score float64, kind policy.AcquisitionKind, tick domain.Tick) (acquisitionBid, bool) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.bids == nil {
		b.bids = map[acquisitionBidKey]acquisitionBid{}
	}
	for key, old := range b.bids {
		if key.snapshot != snapshot || old.tick+acquisitionBidTTL < tick {
			delete(b.bids, key)
		}
	}
	own := acquisitionBidKey{snapshot, resource, from}
	if score > 0 {
		b.bids[own] = acquisitionBid{score, kind, tick}
	} else {
		delete(b.bids, own)
	}
	other := bidAcquisition
	if from == bidAcquisition {
		other = bidResource
	}
	rival, ok := b.bids[acquisitionBidKey{snapshot, resource, other}]
	return rival, ok && rival.score > score
}

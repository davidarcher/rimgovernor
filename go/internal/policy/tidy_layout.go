package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TidyLayout re-sites a settled colony's off-plan furniture onto each
// room's derived interior plan (#611, #809), one room at a time. It ranks
// below every production, upkeep and defense goal (maintenance priority, no
// deficit) and is active only at tier >= Masonry, when the colony has no
// unfilled construction or hauling work. It holds one re-site in flight and
// never re-sites an item already tidied. Stockpiles are MaintainStockpiles'
// (#725), never re-sited here. It is a Standard whose target is no
// outstanding work: no untidied item (#1024); the idle-only proposal gate
// stays.
const TidyLayout ConcernID = "TidyLayout"

// tidyPriority ranks TidyLayout last: the lowest goal rank, with no deficit
// so it never outscores a deficit-bearing goal for a slot.
const tidyPriority = 4

// tidyDeficit is the deficit fraction a standing proposal ranks with: under
// any partial deficit of a goal doing real work, above zero so the slot an
// idle colony leaves free still falls to the tidy.
const tidyDeficit = 0.05

// TidyKind is the kind of item one re-site moves.
type TidyKind string

// TidyFurniture re-sites a room's off-plan furniture onto its derived
// interior plan (#809); the item is the room, the tidied ids its pieces.
const TidyFurniture TidyKind = "furniture"

// TidyItem is one item a re-site moves: a room by id with its footprint
// and the count of pieces moved.
type TidyItem struct {
	Kind      TidyKind
	ID        string
	Footprint Rectangle
	Cells     int
	Crop      string
}

// TidyRequest is the review's input: the tier gate, whether the colony is
// busy (unknown counts as busy), the ids already tidied or in flight, and
// the rooms whose furniture is measured against their derived interior
// plans; a piece whose thing id is in Tidied is never moved.
type TidyRequest struct {
	Tier domain.Fact[BuildTier]
	Busy domain.Fact[bool]
	// Tidied lists item ids a re-site already moved or is moving; InFlight
	// reports one still moving, which holds every new proposal.
	Tidied   []string
	InFlight bool
	Rooms    []TidyRoom
	// Relocate is the shelter table's move into the laboratory (#2047), a
	// cross-room re-site ranked before the in-room batches.
	Relocate *TidyProposal
}

// TidyProposal is the one re-site the review proposes: the item, the
// rectangle it moves to, the gain and the distance the site moves, with a
// one-line explanation.
type TidyProposal struct {
	Item        TidyItem
	Target      Rectangle
	Gain        int
	Distance    int32
	Explanation string
	// Moves is a furniture proposal's ordered batch (#809).
	Moves []TidyMove `json:",omitempty"`
}

// TidyReview is the review outcome: Known once the gates were evaluated
// against known facts, Active while a proposal stands, and Reason naming
// why none does.
type TidyReview struct {
	Known  bool
	Active bool
	// Proposal is nil when nothing is proposed; a pointer, not a Fact,
	// so the review survives its JSON round trip through the journal.
	Proposal *TidyProposal `json:",omitempty"`
	Reason   string
	// Candidates counts the rooms with off-plan furniture.
	Candidates int
}

// PlanTidyLayout proposes at most one furniture re-site. Deterministic over
// its input.
func PlanTidyLayout(r TidyRequest) TidyReview {
	tier, tk := r.Tier.Value()
	if !tk {
		return TidyReview{Reason: "build tier unknown"}
	}
	if tier < BuildTierMasonry {
		return TidyReview{Known: true, Reason: "camp tier keeps its layout"}
	}
	tidied := map[string]bool{}
	for _, id := range r.Tidied {
		tidied[id] = true
	}
	review := TidyReview{Known: true}
	if r.InFlight {
		// The moving item is already tidied (no candidate, no proposal)
		// but the goal stays in deficit until its move closes; a
		// recovered goal would strand the re-site half done.
		review.Active, review.Reason = true, "re-site in flight"
		return review
	}
	review.Candidates = tidyFurnitureCandidates(r.Rooms, tidied)
	if r.Relocate != nil {
		review.Candidates++
	}
	if review.Candidates == 0 {
		review.Reason = "nothing to tidy"
		return review
	}
	busy, bk := r.Busy.Value()
	if !bk || busy {
		review.Reason = "colony busy"
		return review
	}
	furniture := r.Relocate
	if furniture == nil {
		furniture = tidyFurnitureProposal(r.Rooms, tidied)
	}
	if furniture != nil {
		review.Active, review.Proposal = true, furniture
		return review
	}
	review.Reason = "no feasible furniture move"
	return review
}

func absInt32(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

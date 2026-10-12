package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The interior search improves the greedy placer's layout against the interior
// objective (#2869). It starts from PlaceInRegion's layout and applies a fixed
// number of seeded moves (shift, rotate, mirror, swap, re-place one piece),
// keeping a move only when the layout is feasible and strictly better:
// no gated term worse (InteriorScore.WorseTerms) and at least one better.
// The objective has no scalar total, so a move that trades one term for
// another is never kept, and the result is no worse than the greedy seed on
// any gated term by construction. Every choice comes from a splitmix64 stream
// the caller seeds (room id and plan generation); the iteration count is the
// whole budget and wall time never enters.
//
// Built furniture moves for free when BuiltPiece.Movable: uninstall and
// reinstall labor is negligible (user decision, #2869), and the strictly-better
// acceptance rule already prevents churn, so no labor-to-score rate exists.
// Every other built piece is Pinned. Doors are not searched (they stay as
// given); moving one needs a rate between rebuild labor and the objective's
// terms, which is a later priced variable.

// DefaultInteriorSearchIterations is the move budget when InteriorSearch
// names none: a compute bound, not a game value. Every fixture of the score
// table (TestInteriorObjectiveScoresTemplatesAndGreedy) reaches its result well
// inside it.
const DefaultInteriorSearchIterations = 600

// InteriorSearch is the search's input beside the RegionPlacement it improves.
type InteriorSearch struct {
	// Candidates are the wall cells a door may stand on; the objective's hard
	// constraint on RegionPlacement.Doors. Nil accepts any cell.
	Candidates []domain.Cell
	Shapes     PieceShapes
	Dining     DiningFurniture
	// Flows weights the walks between stations; nil leaves the walk terms
	// zero, so only free floor and facility links can improve.
	Flows  InteriorFlows
	Beauty map[string]float64
	// Pinned are pieces the search keeps where they stand. They block their
	// cells for the greedy seed and count toward required counts.
	Pinned []InteriorPiece
	// Built are the room's built pieces. The movable ones (BuiltPiece.Movable)
	// join the search at their current cells; the rest are pinned.
	Built []BuiltPiece
	// Seed seeds the move stream.
	Seed uint64
	// Iterations is the move budget; 0 is DefaultInteriorSearchIterations.
	Iterations int
}

// BuiltPiece is a piece already standing in the room, with the two facts the
// move rule needs.
type BuiltPiece struct {
	Piece InteriorPiece
	// Minifiable is the def's Station.Minifiable (ThingDef.Minifiable).
	Minifiable bool
	// HoldsState is set when the building carries state a move cannot keep: a
	// bed with owners or a set medical/prisoner/slave use (DeSpawn clears
	// them), a workbench with bills (#2881), a storage building with contents.
	HoldsState bool
}

// Movable is the move rule: a built piece is searched when the game can pack
// it (minifiable) and it holds no state the move would lose. Anything else is
// pinned.
func (b BuiltPiece) Movable() bool { return b.Minifiable && !b.HoldsState }

// SearchMove is one kept move, with the gated terms it improved: the reason
// the caller journals (telemetry.Decide "interior_search_move").
type SearchMove struct {
	Kind     string
	Slot     string
	Improves []string
}

// Reason is the stable reason string of a kept move.
func (m SearchMove) Reason() string { return m.Kind + ":" + strings.Join(m.Improves, "+") }

// InteriorSearchResult is a searched layout.
type InteriorSearchResult struct {
	// Pieces are the pinned pieces first (Pinned, then the unmovable Built),
	// then the ones that stand where the search put them: movable built pieces,
	// then the newly placed ones.
	Pieces []InteriorPiece
	fixed  int
	// Seed and Score are the greedy layout's and the searched layout's
	// measures.
	Seed, Score InteriorScore
	Moves       []SearchMove
}

// Stood is the pieces the caller must stand: everything but the pinned ones.
func (r InteriorSearchResult) Stood() []InteriorPiece { return r.Pieces[r.fixed:] }

// SearchInterior places req's wants with the greedy placer and improves the
// layout by seeded local search. It fails when the greedy seed does, and
// returns the seed unchanged when the request has no door to measure from.
func SearchInterior(req RegionPlacement, s InteriorSearch) (InteriorSearchResult, error) {
	greedy := req
	greedy.Search = nil
	pinned := append([]InteriorPiece(nil), s.Pinned...)
	var movable []InteriorPiece
	for _, b := range s.Built {
		if b.Movable() {
			movable = append(movable, b.Piece)
		} else {
			pinned = append(pinned, b.Piece)
		}
	}
	for _, p := range append(append([]InteriorPiece(nil), pinned...), movable...) {
		greedy.Reserved = append(greedy.Reserved, rectCells(p.Rect)...)
		if c, ok := p.Interaction(); ok {
			greedy.Reserved = append(greedy.Reserved, c)
		}
	}
	placed, err := PlaceInRegion(greedy)
	if err != nil {
		return InteriorSearchResult{}, err
	}
	pieces := append(append(append([]InteriorPiece(nil), pinned...), movable...), placed...)
	required := map[string]int{}
	for _, p := range pinned {
		required[p.Def]++
	}
	for _, p := range movable {
		required[p.Def]++
	}
	for _, w := range req.Wants {
		required[w.Def] += w.Min
	}
	obj := InteriorObjective{
		Region: req.Region, Candidates: s.Candidates, Doors: req.Doors, Required: required,
		Shapes: s.Shapes, Flows: s.Flows, Beauty: s.Beauty, Dining: s.Dining,
	}
	score := func(ps []InteriorPiece) InteriorScore {
		o := obj
		o.Pieces = ps
		return ScoreInterior(o)
	}
	cur := score(pieces)
	free := len(pinned)
	res := InteriorSearchResult{Pieces: pieces, Seed: cur, Score: cur, fixed: free}
	if len(req.Doors) == 0 || len(pieces) == free {
		return res, nil
	}
	iters := s.Iterations
	if iters <= 0 {
		iters = DefaultInteriorSearchIterations
	}
	cells := req.Region.Cells()
	bounds := req.Region.Bounds()
	rng := searchRand(s.Seed)
	for range iters {
		cand, kind, at, ok := perturbPieces(pieces, free, cells, bounds, &rng)
		if !ok {
			continue
		}
		cs := score(cand)
		if !cs.Feasible() {
			continue
		}
		var gained []string
		if cur.Feasible() {
			if len(cs.WorseTerms(cur)) > 0 {
				continue
			}
			if gained = cur.WorseTerms(cs); len(gained) == 0 {
				continue
			}
		}
		pieces, cur = cand, cs
		res.Moves = append(res.Moves, SearchMove{Kind: kind, Slot: pieces[at].Slot, Improves: gained})
	}
	res.Pieces, res.Score = pieces, cur
	return res, nil
}

// perturbPieces applies one random move to a copy of pieces, moving only the
// pieces from index free on. It returns the new layout, the move's kind and
// the index of the piece moved (the first, for a swap).
func perturbPieces(pieces []InteriorPiece, free int, cells []domain.Cell, bounds Rectangle, rng *searchRand) ([]InteriorPiece, string, int, bool) {
	i := free + rng.intn(len(pieces)-free)
	p := pieces[i]
	out := append([]InteriorPiece(nil), pieces...)
	corner := domain.Cell{X: p.Rect.X, Z: p.Rect.Z}
	kind := ""
	switch rng.intn(5) {
	case 0:
		kind = "shift"
		d := [4]domain.Cell{{X: 1}, {X: -1}, {Z: 1}, {Z: -1}}[rng.intn(4)]
		out[i] = replaced(p, p.Rot, domain.Cell{X: corner.X + d.X, Z: corner.Z + d.Z})
	case 1:
		kind = "rotate"
		out[i] = replaced(p, rotateCW(p.Rot, 1+rng.intn(3)), corner)
	case 2:
		kind = "mirror"
		rot := p.Rot
		switch rot {
		case domain.East:
			rot = domain.West
		case domain.West:
			rot = domain.East
		}
		out[i] = replaced(p, rot, domain.Cell{X: 2*bounds.X + bounds.Width - p.Rect.X - p.Rect.Width, Z: corner.Z})
	case 3:
		kind = "swap"
		if len(pieces)-free < 2 {
			return nil, "", 0, false
		}
		j := free + rng.intn(len(pieces)-free)
		q := pieces[j]
		if i == j || p.Def == q.Def {
			return nil, "", 0, false
		}
		out[i] = replaced(p, p.Rot, domain.Cell{X: q.Rect.X, Z: q.Rect.Z})
		out[j] = replaced(q, q.Rot, corner)
	default:
		kind = "replace"
		out[i] = replaced(p, rotationOrder[rng.intn(4)], cells[rng.intn(len(cells))])
	}
	return out, kind, i, true
}

// replaced is p stood at the footprint corner in rotation rot.
func replaced(p InteriorPiece, rot domain.Rotation, corner domain.Cell) InteriorPiece {
	q := NewInteriorPiece(p.Slot, p.Def, p.Size, rot, corner)
	q.InteractionOffset, q.Row, q.Pair, q.Centred = p.InteractionOffset, p.Row, p.Pair, p.Centred
	return q
}

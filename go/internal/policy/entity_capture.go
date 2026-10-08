package policy

import (
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The capture rule (#1742, epic #1694, David 2026-10-03): capture a downed
// entity only when the cell's containment strength reaches what the entity
// needs with a margin, and kill the rest. What the entity needs is its own
// MinimumContainmentStrength (the pawn row), the strength is a standing
// available platform's native one (BuiltHolder), and no species is named.
// A fact that is not read means no capture and a loud refusal, never a kill
// or a guess.
//
// The margin is the strength the cell loses when a lapse of upkeep opens its
// door: a door forced open (ContainmentBreached) zeroes the door term of
// StatWorker_ContainmentStrength (decompile, #1741), the one term the game
// models as a breach and the one #1743's upkeep keeps closed. Requiring
// need + that loss means an entity stays safely contained (strength >=
// MinimumContainmentStrength, the game's own test) through such a lapse.
// Light, roof and facilities are not margin: native strength already
// includes the light and roof now, and the planner adds no facility.

// CaptureMargin is the strength a holder of defs loses when its room's doors
// are forced open: the door term, scaled by the holder's containmentFactor
// like every term it sits in. It errors when the door has no hit points, so
// no margin is invented.
func CaptureMargin(defs ContainmentDefs) (float64, error) {
	if defs.DoorHP <= 0 {
		return 0, errors.New("the cell's door has no hit points")
	}
	return defs.DoorHP / containmentDoorHPDivisor * defs.HolderFactor, nil
}

// CapturableEntity is one entity pawn row's capture facts (#1737): each is
// unknown when native did not read it.
type CapturableEntity struct {
	Pawn domain.PawnID
	// Dead and Downed are the pawn row's; CanBeCaptured and Held are the
	// holding-platform target's (CompHoldingPlatformTarget.CanBeCaptured and
	// CurrentlyHeldOnPlatform); Need is MinimumContainmentStrength.
	Dead, Downed, CanBeCaptured, Held domain.Fact[bool]
	Need                              domain.Fact[float64]
	// NeedsTend and Bleeding are the held entity's health (#1743).
	NeedsTend, Bleeding domain.Fact[bool]
	// Escaping is the held entity's EscapeChance flag (#2437).
	Escaping domain.Fact[bool]
	// CurrentlyStudiable is CompStudiable.CurrentlyStudiable (#1744); a
	// known false for an entity with no study block.
	CurrentlyStudiable domain.Fact[bool]
	// Mode, ExtractBioferrite, HarvesterAttached and BioferritePerDay are
	// the held entity's bioferrite harvest facts (#2434).
	Mode                                 domain.Fact[ContainmentMode]
	ExtractBioferrite, HarvesterAttached domain.Fact[bool]
	BioferritePerDay                     domain.Fact[float64]
}

// EntityDecision is what the rule does with one downed entity.
type EntityDecision string

const (
	// EntityCapture takes the entity to a platform that holds it.
	EntityCapture EntityDecision = "capture"
	// EntityKill finishes the entity: no platform holds it with margin, or
	// the game does not let the colony capture it.
	EntityKill EntityDecision = "kill"
	// EntityRefuse does neither: a fact the rule needs is unread. Reason
	// names it.
	EntityRefuse EntityDecision = "refuse"
)

// EntityVerdict is the rule's decision for one downed living entity and why
// in plain English.
type EntityVerdict struct {
	Pawn     domain.PawnID
	Decision EntityDecision
	Reason   string
}

// EntityVerdicts decides every downed living entity that is not held, by
// pawn ID. A pawn that is dead, standing or already held is not decided.
func EntityVerdicts(p ContainmentPlanning) []EntityVerdict {
	entities, known := p.Entities.Value()
	if !known {
		return nil
	}
	var out []EntityVerdict
	for _, e := range entities {
		if v, ok := entityVerdict(p, e); ok {
			out = append(out, v)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Pawn < out[j].Pawn })
	return out
}

func entityVerdict(p ContainmentPlanning, e CapturableEntity) (EntityVerdict, bool) {
	refuse := func(format string, args ...any) (EntityVerdict, bool) {
		return EntityVerdict{Pawn: e.Pawn, Decision: EntityRefuse, Reason: fmt.Sprintf(format, args...)}, true
	}
	dead, dk := e.Dead.Value()
	if dk && dead {
		return EntityVerdict{}, false
	}
	downed, wk := e.Downed.Value()
	held, hk := e.Held.Value()
	if (wk && !downed) || (hk && held) {
		return EntityVerdict{}, false
	}
	switch {
	case !dk:
		return refuse("whether the entity is dead is unread")
	case !wk:
		return refuse("whether the entity is downed is unread")
	case !hk:
		return refuse("whether a platform holds the entity is unread")
	}
	capturable, ck := e.CanBeCaptured.Value()
	if !ck {
		return refuse("whether the game lets the colony capture the entity is unread")
	}
	if !capturable {
		return EntityVerdict{Pawn: e.Pawn, Decision: EntityKill, Reason: "the game does not let the colony capture it"}, true
	}
	need, nk := e.Need.Value()
	if !nk {
		return refuse("the containment strength the entity needs is unread")
	}
	holders, ok := p.Holders.Value()
	if !ok {
		return refuse("the standing holding platforms are unread")
	}
	defs, ok := p.Defs.Value()
	if !ok {
		return refuse("the margin cannot be read from the definitions: %s", p.DefsReason)
	}
	margin, err := CaptureMargin(defs)
	if err != nil {
		return refuse("the margin is unknown: %v", err)
	}
	best, found := 0.0, false
	for _, h := range holders {
		if h.Available && (!found || h.Strength > best) {
			best, found = h.Strength, true
		}
	}
	switch {
	case !found:
		return EntityVerdict{Pawn: e.Pawn, Decision: EntityKill, Reason: "no holding platform is available"}, true
	case best < need+margin:
		return EntityVerdict{Pawn: e.Pawn, Decision: EntityKill, Reason: fmt.Sprintf("the strongest available platform holds %.1f and the entity needs %.1f plus a margin of %.1f", best, need, margin)}, true
	}
	return EntityVerdict{Pawn: e.Pawn, Decision: EntityCapture}, true
}

// entityCaptureOwed is whether the rule has a downed entity to capture: a
// standing work for MaintainPopulation, whose custody step carries it. A
// refusal or a kill is no deficit.
func entityCaptureOwed(p ContainmentPlanning) bool {
	_, ok := EntityCaptureTarget(p)
	return ok
}

// EntityCaptureTarget is the first downed entity the rule captures, by pawn
// ID.
func EntityCaptureTarget(p ContainmentPlanning) (domain.PawnID, bool) {
	for _, v := range EntityVerdicts(p) {
		if v.Decision == EntityCapture {
			return v.Pawn, true
		}
	}
	return "", false
}

package domain

import "errors"

// PopulationDecision is the explicit per-pawn population direction a player
// may record: rescue a downed guest, capture a downed hostile, recruit an
// existing prisoner, or ignore -- withdraw future population orders for that
// individual.
type PopulationDecision string

const (
	PopulationRescue  PopulationDecision = "rescue"
	PopulationCapture PopulationDecision = "capture"
	PopulationRecruit PopulationDecision = "recruit"
	PopulationIgnore  PopulationDecision = "ignore"
)

// PopulationDirective is an immutable, comparable player-sourced population
// direction for one exact observed pawn.
//
// Like PopulationPolicy it is player intent without being a plan action, and
// for the same reason: recording a decision issues no native RimWorld call.
// The native custody work it eventually describes -- rescue, capture and
// prisoner recruitment -- already exists in this controller and is already
// dispatched natively, but through two other paths, neither of which this
// value needs to trigger synchronously:
//
//   - one-shot player commands. interpreter's "rescue" command already
//     decodes straight to domain.Rescue/NewRescueAction, with a performer the
//     player names, exactly like tend; capture has the same Action and
//     boundary (buildingruntime/capture), and prisoner recruitment has
//     domain.PrisonerInteraction.
//   - autopilot upkeep. policy.MaintainPopulation's custody deficit
//     (policy.CustodyDeficit / SelectCustodyMethod, dispatched by
//     buildingruntime.RoutinePopulationCustodyPlanner) derives capture-vs-
//     rescue purely from observed guest/hostility facts and picks its own
//     performer, the same deficit-detection style every MaintainX vertical
//     uses; RoutinePrisonerInteractionPlanner covers recruitment.
//
// A directive is therefore persistent bookkeeping of what the player asked
// for a named individual, and deliberately does not fabricate an Action, a CAS token or a dispatch of
// its own. Teaching the autopilot custody planner to prefer or suppress
// individuals named here is a separate, behaviour-changing slice.
type PopulationDirective struct {
	pawn     PawnID
	decision PopulationDecision
}

// NewPopulationDirective bounds both fields the way the player command
// contract does: an exact observed pawn identity and one of the four
// supported decisions.
func NewPopulationDirective(pawn PawnID, decision PopulationDecision) (PopulationDirective, error) {
	if !validID(string(pawn)) {
		return PopulationDirective{}, errors.New("invalid population decision pawn")
	}
	switch decision {
	case PopulationRescue, PopulationCapture, PopulationRecruit, PopulationIgnore:
	default:
		return PopulationDirective{}, errors.New("invalid population decision")
	}
	return PopulationDirective{pawn, decision}, nil
}

func (d PopulationDirective) Pawn() PawnID                 { return d.pawn }
func (d PopulationDirective) Decision() PopulationDecision { return d.decision }

// "the player has said nothing about this pawn".
// "the player has said nothing about this pawn", the same way an absent
// plan entry does.
func (d PopulationDirective) Set() bool { return d != PopulationDirective{} }

// Withdrawn reports the ignore decision: future population orders for this
// pawn are withdrawn. It preserves existing native state -- an already
// rescued guest stays rescued and an existing prisoner is never released --
// cancelling only pending steps of the player's own direction.
func (d PopulationDirective) Withdrawn() bool { return d.Set() && d.decision == PopulationIgnore }

// RequiresPolicy reports whether recording this directive requires an already
// established population capacity policy: rescue, capture and recruit do;
// ignore deliberately skips that check, so a
// player can always withdraw a direction they previously gave.
func (d PopulationDirective) RequiresPolicy() bool { return d.Set() && !d.Withdrawn() }

package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// controlRoomDoor binds setting changes and, for emergency heat relief, a
// plan-owned draft and ordinary move to the cooler floor across the door.
// Completed settings do not prove passage or recovered temperature.
func (r *RoundsBuildingPlanner) controlRoomDoor(call, epoch context.Context, state ControlState, goal store.WorkOwner, facts observation.ColonyProjection) (RoundsBuildingResult, bool, error) {
	fights, err := r.reviewer.player.journal.OpenCombatFights(call)
	if err != nil {
		return RoundsBuildingResult{}, true, err
	}
	if len(fights) > 0 {
		return RoundsBuildingResult{}, false, nil
	}
	for _, change := range policy.DoorChanges(facts.Rooms, facts.Facts.Upkeep.Routes, r.reviewer.policy) {
		if r.concern == policy.EnsureTemperatureSafety && !change.Heat {
			continue
		}
		if r.concern == policy.MaintainRoutes && change.Heat {
			continue
		}
		held, _ := change.Door.HoldOpen.Value()
		var opener domain.PawnID
		if change.Heat && change.Passage {
			pawns, known := facts.WorkPawns.Value()
			if known {
				pawns = slices.Clone(pawns)
				slices.SortFunc(pawns, func(a, b policy.WorkPawn) int {
					if a.ID < b.ID {
						return -1
					}
					if a.ID > b.ID {
						return 1
					}
					return 0
				})
				for _, pawn := range pawns {
					available, ak := pawn.Available.Value()
					job, jk := pawn.Job.Value()
					if ak && available && jk && job.Work != policy.WorkDoctor && job.Work != policy.WorkFirefighter && job.Def != "Rescue" && job.Def != "LayDown" && slices.Contains(change.Occupants, domain.PawnID(pawn.ID)) {
						opener = domain.PawnID(pawn.ID)
						break
					}
				}
			}
			// Other temperature methods remain available if nobody can safely
			// open this door; a latch alone is not emergency ventilation.
			if opener == "" {
				continue
			}
		}
		if held == change.HoldOpen && opener == "" {
			return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "door_passage_pending"), NativeWorkTicks: stockWaitTicks}, true, nil
		}
		id := domain.MintPlanID()
		var actions []domain.Action
		if held != change.HoldOpen {
			control, err := domain.NewDoorControl(change.Door.Cell, change.HoldOpen)
			if err != nil {
				return RoundsBuildingResult{}, true, err
			}
			setting, err := domain.NewDoorControlAction(domain.ActionID(fmt.Sprintf("%s-0", id)), control)
			if err != nil {
				return RoundsBuildingResult{}, true, err
			}
			actions = append(actions, setting)
		}
		if opener != "" {
			draft, err := domain.NewOwnedDraft(opener)
			if err != nil {
				return RoundsBuildingResult{}, true, err
			}
			draftAction, err := domain.NewOwnedDraftAction(domain.ActionID(fmt.Sprintf("%s-1", id)), draft)
			if err != nil {
				return RoundsBuildingResult{}, true, err
			}
			movement, err := domain.NewMovement(opener, change.To, draftAction.ID())
			if err != nil {
				return RoundsBuildingResult{}, true, err
			}
			moveAction, err := domain.NewMovementAction(domain.ActionID(fmt.Sprintf("%s-2", id)), movement)
			if err != nil {
				return RoundsBuildingResult{}, true, err
			}
			actions = append(actions, draftAction, moveAction)
		}
		plan, err := domain.NewPlan(id, 1, actions)
		if err != nil {
			return RoundsBuildingResult{}, true, err
		}
		if err := r.reviewer.player.current(call, epoch); err != nil {
			return RoundsBuildingResult{}, true, err
		}
		if r.reviewer.player.session.State() != state {
			return RoundsBuildingResult{}, true, ErrControl
		}
		// A fresh method is needed when a later native state changes again;
		// observed HoldOpen suppresses repeats, not a permanent method key.
		method := domain.MethodID(fmt.Sprintf("door-%d-%d-%s", change.Door.Cell.X, change.Door.Cell.Z, id))
		if err := r.reviewer.player.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
			return RoundsBuildingResult{}, true, err
		}
		return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, true, nil
	}
	return RoundsBuildingResult{}, false, nil
}

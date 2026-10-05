package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
)

// downedEntities are the frame's downed live hostile pawns whose detail row
// reads them as Anomaly entities, by id: the ones the capture rule (#1742)
// decides.
func downedEntities(combat bridge.Combat) []*mp.CombatPawn {
	var out []*mp.CombatPawn
	for _, row := range combat.Pawns {
		if row.GetSide() != mp.CombatSide_COMBAT_SIDE_HOSTILE || !row.GetDowned() || row.GetDead() {
			continue
		}
		detail, _ := combat.Detail.Get(row.GetId())
		if detail == nil {
			continue
		}
		if a, ok := bridge.PawnAnomaly(detail.Anomaly).Value(); ok && positiveFact(a.Entity) {
			out = append(out, row)
		}
	}
	slices.SortFunc(out, func(a, b *mp.CombatPawn) int { return strings.Compare(a.GetId(), b.GetId()) })
	return out
}

// postFightEntities applies the capture rule (#1742) to the fight's downed
// hostile entities: one the rule kills is finished by its nearest colonist
// (held says the fight stays open on it), one it captures is left to the
// custody capture, and one whose facts are unread is neither captured nor
// killed and is logged loudly. The census is read only when a downed
// hostile is on the frame.
func (r *RoundsDefensePlanner) postFightEntities(call, epoch context.Context, state ControlState, tick domain.Tick, fightPlan domain.PlanID, memory policy.CombatMemory, combat bridge.Combat, arbiter *stepArbiter) (RoundsDefenseResult, bool, error) {
	downed := downedEntities(combat)
	if len(downed) == 0 {
		return RoundsDefenseResult{}, false, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsDefenseResult{}, false, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, tick) {
		return RoundsDefenseResult{}, false, fmt.Errorf("%w: postFightEntities: census outside the world", ErrControl)
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsDefenseResult{}, false, err
	}
	verdicts := map[domain.PawnID]policy.EntityVerdict{}
	for _, v := range policy.EntityVerdicts(read.Projection.Facts.Containment) {
		verdicts[v.Pawn] = v
	}
	for _, raider := range downed {
		v, ok := verdicts[domain.PawnID(raider.GetId())]
		switch {
		case !ok || v.Decision == policy.EntityCapture:
		case v.Decision == policy.EntityRefuse:
			defenseAction(call, "routine-defense", slog.LevelWarn, "refused", "entity_capture_refused", raider.GetId(), map[string]any{"plan": string(fightPlan), "outcome": "left_alive", "detail": v.Reason})
		case v.Decision == policy.EntityKill:
			defenseAction(call, "routine-defense", slog.LevelInfo, "applied", "entity_kill", raider.GetId(), map[string]any{"plan": string(fightPlan), "outcome": "not_captured", "detail": v.Reason})
			result, err := r.finishRaider(call, epoch, state, fightPlan, memory, combat, raider, arbiter)
			return result, true, err
		}
	}
	return RoundsDefenseResult{}, false, nil
}

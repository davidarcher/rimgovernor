package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// moodCastReason reports whether a mood proposal leaves the pawn's pressure
// to a psycast: measured need relief is spent or absent and no facility goal
// owns the pressure.
func moodCastReason(reason policy.MoodMethodReason) bool {
	return reason == policy.MoodNoCause || reason == policy.MoodExhausted || reason == policy.MoodUnowned
}

// moodCastPrefix names a caster's attempts on a target; the attempts of one
// caster-psycast pair are bounded like need relief's.
func moodCastPrefix(target, caster policy.PawnID, ability string) string {
	return fmt.Sprintf("mood-cast-%s-%s-%s-", target, caster, ability)
}

// castMoodRelief commits one mood psycast on target, from the royalty read and
// colonists of the review's census; false when no caster qualifies. Native
// owns the cast's guards (cooldown, psyfocus, neural heat, range, target).
func (r *RoundsMoodReliefPlanner) castMoodRelief(call, epoch context.Context, arbiter *stepArbiter, incident store.IncidentState, target policy.PawnID, world domain.GenerationSnapshot) (domain.PlanID, bool, error) {
	royalty, pawns := r.reviewer.census.psycasters()
	var budgetErr error
	choice, ok := policy.SelectMoodCast(target, royalty, pawns, func(caster policy.PawnID, ability string) bool {
		prefix := moodCastPrefix(target, caster, ability)
		_, allowed, err := admitSubject(call, r.reviewer.player.journal, prefix, incidentPlans(incident.Methods, prefix), world)
		if err != nil && budgetErr == nil {
			budgetErr = err
		}
		return !allowed
	})
	if budgetErr != nil {
		return "", false, budgetErr
	}
	if !ok || !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Caster)}) {
		return "", false, nil
	}
	source, err := domain.PsycastSource(choice.Ability)
	if err != nil {
		return "", false, err
	}
	aim, err := domain.AbilityPawnTarget(domain.PawnID(choice.Target))
	if err != nil {
		return "", false, err
	}
	ability, err := domain.NewAbility(domain.PawnID(choice.Caster), source, aim)
	if err != nil {
		return "", false, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewAbilityAction(domain.ActionID(fmt.Sprintf("%s-0", id)), ability)
	if err != nil {
		return "", false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return "", false, err
	}
	if err = r.reviewer.player.current(call, epoch); err != nil {
		return "", false, err
	}
	prefix := moodCastPrefix(target, choice.Caster, choice.Ability)
	method := nextMethodID(prefix, incidentMethodIDs(incident.Methods))
	if _, err = r.reviewer.player.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return "", false, err
	}
	return id, true, nil
}

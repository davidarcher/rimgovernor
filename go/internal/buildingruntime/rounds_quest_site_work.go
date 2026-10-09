package buildingruntime

import (
	"context"
	"fmt"
	"slices"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (r *RoundsPopulationJoinerPlanner) admitSiteWork(call, epoch context.Context, state ControlState, goal store.StandardState, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	f := read.Projection.Facts
	sites, sk := f.QuestSites.Value()
	offers, ok := f.QuestOffers.Value()
	if !sk || !ok {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	sites = slices.Clone(sites)
	sort.Slice(sites, func(i, j int) bool { return sites[i].ID < sites[j].ID })
	for _, site := range sites {
		current, known := site.Map.Value()
		if !known || current != read.Projection.Identity.Map {
			continue
		}
		for _, offer := range offers {
			if !slices.Contains(site.QuestIDs, offer.Quest) {
				continue
			}
			profile, known := offer.Profile.Value()
			if !known || profile.NeverAct {
				continue
			}
			hackTargets := false
			for _, objective := range offer.Objectives {
				hackTargets = hackTargets || len(objective.HackTargets) > 0
			}
			if hackTargets && offer.State != "EndedFailed" && !policy.QuestHackComplete(offer) {
				continue
			}
			id := domain.MintPlanID()
			actionID := domain.ActionID(string(id) + "-0")
			var action domain.Action
			var err error
			var crew []domain.PawnID
			kind := ""
			target := site.ID
			returning := false
			if offer.ScriptDef == "SurveySite" {
				work := policy.SurveyWork(offer, site, f, f.AnimalUpkeep.Food, r.reviewer.policy)
				if work.Reason != "" {
					r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: offer.Quest, Reason: work.Reason})
				}
				if work.Departure != nil {
					action, err = domain.NewCaravanDepartureAction(actionID, *work.Departure)
					crew = work.Departure.Crew()
					kind = "relief"
				} else if work.Return {
					returning = true
				} else {
					continue
				}
			} else {
				work := policy.PlanCoreSiteWork(offer, site, f, current)
				if work.Reason != "" {
					r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: offer.Quest, Reason: policy.QuestSkipReason(work.Reason)})
				}
				switch work.Kind {
				case "mine":
					action, err = domain.NewMineAcquisitionAction(actionID, work.Mine)
					kind = "mine"
					target = work.Mine.Thing()
				case "help":
					action, err = domain.NewRecoveryServiceAction(actionID, work.Help)
					crew = []domain.PawnID{work.Help.Pawn()}
					kind = "help"
					target = work.Help.Thing()
				case "recruit":
					action, err = domain.NewPrisonerInteractionAction(actionID, work.Recruit)
					kind = "recruit"
					target = string(work.Recruit.Pawn())
				case "return":
					returning = true
				default:
					if policy.QuestHackComplete(offer) {
						returning = true
					} else {
						continue
					}
				}
			}
			if returning {
				work := policy.PlanSiteReturn(site, false)
				if work.Departure == nil {
					if work.Reason != "" {
						r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: offer.Quest, Reason: work.Reason})
					}
					continue
				}
				action, err = domain.NewCaravanDepartureAction(actionID, *work.Departure)
				crew, kind = work.Departure.Crew(), "return"
			}
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			if len(crew) > 0 && !arbiter.tryClaim(crew, "quest:"+string(offer.Quest)) {
				return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "quest_site")}, true, nil
			}
			plan, err := domain.NewPlan(id, 1, []domain.Action{action})
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			prefix := fmt.Sprintf("quest-site-%s-%s-%s-%s-", offer.Quest, site.ID, kind, target)
			method, verdict, admitted, err := admitStandardMethod(call, r.reviewer.player.journal, goal, prefix, state.Snapshot)
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			if !admitted {
				return RoundsPopulationJoinerResult{Verdict: verdict}, true, nil
			}
			if err = r.reviewer.player.current(call, epoch); err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			elapsed := r.reviewer.clock.Now().Sub(started)
			if r.reviewer.player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
				return RoundsPopulationJoinerResult{}, false, ErrControl
			}
			if _, err = r.reviewer.player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: stockWaitTicks}, true, nil
		}
	}
	return RoundsPopulationJoinerResult{}, false, nil
}

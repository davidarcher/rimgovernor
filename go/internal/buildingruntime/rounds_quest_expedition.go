package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"slices"
	"time"
)

func (r *RoundsPopulationJoinerPlanner) admitExpedition(call, epoch context.Context, state ControlState, goal store.StandardState, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	work := policy.SelectExpedition(read.Projection.Facts, r.reviewer.policy)
	// Walking through a native exit leaves survivors in a stopped caravan.
	// Match its exact crew to a journaled expedition before routing it home.
	trips, tripsKnown := read.Projection.Facts.QuestExpeditionTrips.Value()
	sites, sitesKnown := read.Projection.Facts.QuestSites.Value()
	if tripsKnown && sitesKnown {
		for _, method := range goal.History {
			previous, err := r.reviewer.player.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoundsPopulationJoinerResult{}, false, err
			}
			for _, action := range previous.Spec.Actions() {
				departed, ok := action.CaravanDeparture()
				if !ok {
					continue
				}
				for _, site := range sites {
					tile, known := site.Tile.Value()
					if !known || tile != departed.DestinationTile() || len(site.QuestIDs) == 0 {
						continue
					}
					crew := departed.Crew()
					for pawn, quest := range policy.QuestRefugeeIDs(read.Projection.Facts.QuestOffers) {
						if slices.Contains(site.QuestIDs, quest) {
							crew = append(crew, pawn)
						}
					}
					for _, trip := range trips {
						if home := policy.PlanStoppedExpeditionReturn(trip, crew); home != nil {
							work = policy.ExpeditionPlan{Quest: site.QuestIDs[0], Site: site.ID, Departure: home}
							break
						}
					}
				}
			}
		}
	}
	if work.Quest == "" {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	if work.Reason != "" {
		r.reviewer.logQuestSkip(call, policy.QuestSkip{Quest: work.Quest, Reason: work.Reason})
	}
	if work.Departure == nil {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: stockWaitTicks}, false, nil
	}
	if !arbiter.tryClaim(work.Departure.Crew(), "quest:"+string(work.Quest)) {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "quest_expedition")}, true, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewCaravanDepartureAction(domain.ActionID(string(id)+"-0"), *work.Departure)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	prefix := fmt.Sprintf("quest-expedition-%s-%s-", work.Quest, work.Site)
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

package store

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// routinePlan is one current-world, non-retired plan and the need (or
// player project) it serves.
type routinePlan struct {
	state    PlanState
	goal     domain.ConcernID
	priority int
}

// routinePlans maps every non-retired plan of the current world to its
// goal: routine plans to their bound need, player submissions to a
// per-plan project id. Plans of another world, and plans with neither a
// method nor a submission, are skipped.
func routinePlans(ctx context.Context, tx *sql.Tx, current domain.GenerationSnapshot, bindings []RoutineGoal) ([]routinePlan, error) {
	plans, err := loadPlans(ctx, tx, 256)
	if err != nil {
		return nil, err
	}
	var result []routinePlan
	for _, plan := range plans {
		var goalID domain.ConcernID
		priority := 3
		world := World{}
		admitted := 0
		var owner, incident, project sql.NullString
		err = tx.QueryRowContext(ctx, "SELECT goal_id,incident_id,project_id,priority FROM goal_methods WHERE plan_id=?", plan.Spec.ID()).Scan(&owner, &incident, &project, &admitted)
		goalID = domain.ConcernID(owner.String)
		if err == nil && project.Valid {
			// A Project's method serves its kind, which is its need.
			p, e := loadProject(ctx, tx, domain.ProjectID(project.String))
			if e != nil {
				return nil, e
			}
			goalID, priority = p.Project.Kind, admitted
			world = World{Colony: p.Project.Snapshot.Colony, Load: p.Project.Snapshot.Load, Map: p.Project.Snapshot.Map}
		} else if err == nil && incident.Valid {
			// An incident's method serves its Response kind (#1020).
			i, e := loadIncident(ctx, tx, domain.IncidentID(incident.String))
			if e != nil {
				return nil, e
			}
			goalID, priority = i.Incident.Kind, admitted
			world = World{Colony: i.Incident.Snapshot.Colony, Load: i.Incident.Snapshot.Load, Map: i.Incident.Snapshot.Map}
		} else if err == nil {
			g, e := loadGoal(ctx, tx, goalID)
			if e != nil {
				return nil, e
			}
			// The priority the method was admitted under, not the goal's
			// current one: work a priority-2 goal admitted outside the
			// ranked queue never turns into a slot hold when the goal
			// drops back to 3 (#705), and slot work stays one.
			priority = admitted
			world = World{Colony: g.Standard.Snapshot.Colony, Load: g.Standard.Snapshot.Load, Map: g.Standard.Snapshot.Map}
			for _, b := range bindings {
				if goalID == b.Goal || routineStandardOwns(goalID, b.Need) {
					goalID = b.Need
					break
				}
			}
		} else if errors.Is(err, sql.ErrNoRows) {
			err = tx.QueryRowContext(ctx, "SELECT colony,load_token,map_id FROM submissions WHERE plan_id=?", plan.Spec.ID()).Scan(&world.Colony, &world.Load, &world.Map)
			if errors.Is(err, sql.ErrNoRows) {
				continue
			}
			if err != nil {
				return nil, err
			}
			goalID = domain.ConcernID(fmt.Sprintf("player-project-%x", sha256.Sum256([]byte(plan.Spec.ID()))))
		} else {
			return nil, err
		}
		if world != (World{Colony: current.Colony, Load: current.Load, Map: current.Map}) {
			continue
		}
		result = append(result, routinePlan{plan, goalID, priority})
	}
	return result, nil
}

func commitmentsOf(ctx context.Context, tx *sql.Tx, plans []routinePlan) ([]policy.Commitment, error) {
	var result []policy.Commitment
	for _, plan := range plans {
		var open domain.Progress
		for _, p := range plan.state.Progress {
			if ProgressOpen(plan.state, p) {
				open = p
				break
			}
		}
		if open.View().Stage == "" || developmentExemptMethod(plan.state.Spec) {
			continue
		}
		labor := policy.GoalLabor(plan.goal)
		if labor == nil && strings.HasPrefix(string(plan.goal), "player-project-") {
			labor = policy.LaborProfile{policy.WorkConstruction}
		}
		targets := domain.Unknown[policy.WorkTargets]()
		for _, a := range plan.state.Spec.Actions() {
			if a.ID() == open.View().Action {
				targets = policy.ActionWorkTargets(a)
			}
		}
		result = append(result, policy.Commitment{Goal: plan.goal, Priority: plan.priority, Progress: open, Labor: labor, Targets: targets})
	}
	return result, nil
}

// readyWorkOf is the shadow ready-work projection (#645) of the review's
// plans: recorded beside the development rows, read by no admission.
// Stage inputs (bill ingredients, crop readiness) are not observed here
// yet, so staged work reads awaiting_observation rather than ready.
func readyWorkOf(r RoundsRequest, plans []routinePlan, goals []policy.DevelopmentGoal) policy.ReadyWorkReport {
	var ready []policy.ReadyPlan
	for _, p := range plans {
		ready = append(ready, policy.ReadyPlan{Goal: p.goal, Spec: p.state.Spec, Progress: p.state.Progress})
	}
	var unserved []domain.ConcernID
	for _, g := range goals {
		if !g.Served && !g.Blocked && g.Labor != nil {
			unserved = append(unserved, g.ID)
		}
	}
	return policy.ProjectReadyWork(policy.ReadyRequest{Snapshot: r.Current, Tick: r.Tick, Plans: ready, Unserved: unserved, Construction: r.Facts.CurrentConstruction, Recipes: r.Facts.Recipes})
}

func rankRoutineDevelopment(ctx context.Context, tx *sql.Tx, r RoundsRequest, needs policy.RoundsFindings, states []WorkOwner, previous policy.DevelopmentState, withheld policy.LaborProfile, stage policy.ColonyStageRecord, records []DependencyRecord) (policy.DevelopmentState, policy.ReadyWorkReport, policy.ShadowRank, []DependencyRecord, error) {
	var bindings []RoutineGoal
	for i, n := range needs.Assessments {
		bindings = append(bindings, RoutineGoal{Need: n.ID, Goal: domain.ConcernID(states[i].OwnerID())})
	}
	plans, err := routinePlans(ctx, tx, r.Current, bindings)
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, policy.ShadowRank{}, nil, err
	}
	commitments, err := commitmentsOf(ctx, tx, plans)
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, policy.ShadowRank{}, nil, err
	}
	kept, dependencies, err := routineDependencies(ctx, tx, records, bindings, states, r.Facts, r.Tick)
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, policy.ShadowRank{}, nil, err
	}
	goals := append([]policy.DevelopmentGoal(nil), needs.Goals...)
	for i := range goals {
		for j, b := range bindings {
			if b.Need == goals[i].ID {
				owner := states[j]
				g, _ := SummarizeOwner(owner)
				goals[i].Blocked = g.Status != domain.StandardOpen
				// Served counts retired methods too: a startup goal whose
				// campfire plan completed and retired is served, not owed.
				var served int
				column, id, epoch := owner.ownerKey()
				if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM goal_methods WHERE "+column+"=? AND epoch=?", id, epoch).Scan(&served); err != nil {
					return policy.DevelopmentState{}, policy.ReadyWorkReport{}, policy.ShadowRank{}, nil, err
				}
				goals[i].Served = served > 0
			}
		}
	}
	state, err := policy.RankDevelopment(policy.DevelopmentRequest{Snapshot: r.Current, Tick: r.Tick, Workers: r.Facts.Workers, Labor: r.Facts.Labor, LaborUse: r.Facts.LaborUse, Stage: stage, Goals: goals, Assessments: needs.All(), Commitments: commitments, Previous: previous, Partial: r.PartialPlanners, Withheld: withheld, Dependencies: dependencies})
	if err != nil {
		return policy.DevelopmentState{}, policy.ReadyWorkReport{}, policy.ShadowRank{}, nil, err
	}
	return state, readyWorkOf(r, plans, goals), policy.ShadowRankOf(state, policy.ProjectForward(policy.ForwardInputsOf(r.Facts, r.Policy)), openPlanActions(plans)), kept, nil
}

// developmentExemptMethod reports a method that is no development project:
// every action is a QuestAccept or a joiner-letter answer (one native write
// with no pawn work behind it; the quest's own parts walk the joiner in, so
// MaintainPopulation answers it without a slot, the way a trade's
// configuration pushes are exempt by need), or an Equip of a weapon the
// colony already owns (one pawn walks to a loose bow and picks it up, a
// minute of forced work that builds nothing; #411: EnsureBasicDefense
// waited three days behind a wood haul and a herbal bill for a slot while
// five bows lay on the ground), or a husbandry settings write (a
// designation cancel, an allowed area, a master or a follow flag: one
// native write, no handler work; #577: takeover/herd-removal parked on
// no_work while MaintainHerd's cancel of a Manual slaughter flag waited
// for a slot at maintenance priority), or a zone deletion (one native
// write dissolving a zone the tidy already re-sited, #611), or a zone
// creation or cell edit (zoning is an instant native write no pawn works:
// a fresh colony's stockpiles waited hours behind the shelter for a slot
// and a hauler while logs and fish lay loose), or an apparel
// policy write (one native settings write per pawn, no pawn work; #660:
// eight colonists waited one slot per review round for their policies and
// MaintainEquipment spent a whole window before its first wear order), or an
// allowed-area move with no work priorities (one native settings write; a
// creepjoiner's isolation, #1740). An exempt
// method is admitted without a slot and, while open, holds none
// (routineCommitments).
func developmentExemptMethod(plan domain.PlanSpec) bool {
	actions := plan.Actions()
	if len(actions) == 0 {
		return false
	}
	for _, action := range actions {
		if building, isBuilding := action.Building(); isBuilding && building.Definition() == "ButcherSpot" {
			// Free and instant: no pawn labor to fund (the spot is the butcher
			// bill's precondition).
			continue
		}
		letter, isDialog := action.DialogAnswer()
		husbandry, isHusbandry := action.Husbandry()
		work, isWork := action.WorkAssignment()
		if isWork && work.HasArea() && len(work.Settings()) == 0 && !work.HasSchedule() {
			// An allowed-area move alone is one native settings write (#1740).
			continue
		}
		if action.Kind() != domain.QuestAcceptAction && action.Kind() != domain.RitualAction && action.Kind() != domain.EquipAction && action.Kind() != domain.DropEquipmentAction && action.Kind() != domain.ZoneDeleteAction && action.Kind() != domain.AutoRefuelAction && action.Kind() != domain.AutoHomeAreaAction && action.Kind() != domain.AreaAction && action.Kind() != domain.PawnSettingsAction && action.Kind() != domain.ReadingPolicyAction && action.Kind() != domain.DrugPolicyAction && action.Kind() != domain.FoodPolicyAction && action.Kind() != domain.ZoneCreateAction && action.Kind() != domain.ZoneCellEditAction && action.Kind() != domain.StockpilePatchAction && action.Kind() != domain.ApparelPolicyAction && !(isDialog && letter.LetterToken() != "") && !(isHusbandry && husbandrySettingsWrite(husbandry.Method())) {
			return false
		}
	}
	return true
}

// husbandrySettingsWrite names the husbandry methods that change a flag on
// the animal and nothing else; tame, slaughter, release and sterilize put a
// handler (or a doctor) to work. Train is the Animals tab tick box (SetWantedRecursive):
// MaintainHerd never holds a development slot, so a slot-gated train was
// refused forever (#697).
func husbandrySettingsWrite(method domain.HusbandryMethod) bool {
	switch method {
	case domain.HusbandryTrain, domain.HusbandryCancelSlaughter, domain.HusbandryCancelRelease, domain.HusbandryAllowedArea, domain.HusbandryMaster, domain.HusbandryFollowDrafted, domain.HusbandryFollowFieldwork:
		return true
	}
	return false
}

// Recheck current commitments inside method admission: a player project accepted
// since the review may already have consumed its last optional slot. A
// method that is a pure settings write (developmentExemptMethod) is
// admitted without a slot.
func admitRoutineDevelopment(ctx context.Context, tx *sql.Tx, g OwnerSummary, plan domain.PlanSpec) error {
	if g.Priority < 3 || !(strings.HasPrefix(g.ID, "routine-") || isProjectID(g.ID)) || developmentExemptMethod(plan) {
		return nil
	}
	review, err := loadRoutine(ctx, tx)
	if err != nil {
		return err
	}
	if review.Snapshot != g.Snapshot {
		return fmt.Errorf("%w: goal %s reviewed under snapshot %+v, current review is %+v", ErrNotAdmitted, g.ID, g.Snapshot, review.Snapshot)
	}
	need, _ := review.needOf(g.ID)
	if err := policy.AdmitDevelopment(review.Development.State(), need); err != nil {
		return fmt.Errorf("%w: %v", ErrNotAdmitted, err)
	}
	return nil
}

// openPlanActions counts, per goal, the actions of its live plans that are
// not finished: the shadow ranker's labor cost (#1913). An action with no
// progress record has not started, so it is open.
func openPlanActions(plans []routinePlan) map[domain.ConcernID]int {
	open := map[domain.ConcernID]int{}
	for _, p := range plans {
		if p.state.Retired {
			continue
		}
		byAction := map[domain.ActionID]domain.Progress{}
		for _, pr := range p.state.Progress {
			byAction[pr.View().Action] = pr
		}
		for _, a := range p.state.Spec.Actions() {
			if pr, started := byAction[a.ID()]; !started || ProgressOpen(p.state, pr) {
				open[p.goal]++
			}
		}
	}
	return open
}

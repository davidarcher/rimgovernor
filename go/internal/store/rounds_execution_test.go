package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoundsExecutionRequiresCurrentReviewedMethod(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"valid", "direction", "native", "load", "revision", "unbound", "disabled", "unknown"} {
		t.Run(change, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, memoryPath(t))
			r := roundsRequest()
			r.Current.Native = 2
			g := roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
			q := methodRequest(t, g, "method", 10)
			d, err := s.AdmitBuildingMethod(ctx, q)
			if err != nil || !d.Admitted {
				t.Fatal(d, err)
			}
			root, target := r.Current, q.Current
			switch change {
			case "direction":
				target.Native++
			case "native":
				target.Native++
			case "load":
				target.Load = "other"
			case "revision":
				target.Revision++
			case "unbound":
				target.Plan = "other"
			case "disabled":
				r.Enabled = false
				reviewRounds(t, s, &r)
			case "unknown":
				r.Facts.Wood = domain.Unknown[int64]()
				reviewRounds(t, s, &r)
			}
			err = s.AuthorizeRoundsPlan(ctx, root, target)
			if (err == nil) != (change == "valid") {
				t.Fatal(change, err)
			}
		})
	}
}

// Guards the narrow FindingMet exception: a bill goal whose setup gate has
// recovered may still authorize its already-dispatched, still-unresolved bill
// output, since that pending pawn time is what the recovered gate reflects.
// It must never permit a fresh setup write once the gate has recovered.
func TestRoundsExecutionRecoveredBillNeedPermitsPendingOutputOnly(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	out := reviewRounds(t, s, &r)
	g := roundsProject(t, out, policy.EnsureCooking)
	if g.Project.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
	bill, err := domain.NewProductionBill("bench", "recipe", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitProjectMethod(ctx, g.Project.ID, g.Revision, "cook", "", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	if _, err = s.Prepare(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	r.Facts.Cooking = domain.Known(true)
	out = reviewRounds(t, s, &r)
	g = roundsProject(t, out, policy.EnsureCooking)
	if g.Project.Finding != domain.FindingMet || g.Project.Status != domain.ProjectOpen {
		t.Fatal(g)
	}
	if err = s.AuthorizeRoundsPlan(ctx, r.Current, target); err != nil {
		t.Fatal("recovered need with unresolved bill output was not authorized", err)
	}
}

// Once the bill intent has applied, the recovered gate
// leaves nothing pending: the goal settles to Satisfied rather than staying
// Active, and authorization must refuse it like any other satisfied goal.
func TestRoundsExecutionRecoveredBillNeedRefusesOnceResolved(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	out := reviewRounds(t, s, &r)
	g := roundsProject(t, out, policy.EnsureCooking)
	bill, err := domain.NewProductionBill("bench", "recipe", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitProjectMethod(ctx, g.Project.ID, g.Revision, "cook", "", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	if _, err = s.Prepare(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "bill-plan", "bill", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	r.Facts.Cooking = domain.Known(true)
	out = reviewRounds(t, s, &r)
	g = roundsProject(t, out, policy.EnsureCooking)
	if g.Project.Finding != domain.FindingMet || g.Project.Status != domain.ProjectCompleted {
		t.Fatal(g)
	}
	if err = s.AuthorizeRoundsPlan(ctx, r.Current, target); err == nil {
		t.Fatal("resolved bill output authorized a satisfied goal")
	}
}

// A recovered gate never authorizes a fresh setup write: if any bill action in
// the bound plan was never dispatched, authorization must refuse the whole
// plan even though a sibling action still has genuinely pending output.
func TestRoundsExecutionRecoveredBillNeedRefusesUndispatchedSibling(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	out := reviewRounds(t, s, &r)
	g := roundsProject(t, out, policy.EnsureCooking)
	bill1, err := domain.NewProductionBill("bench1", "recipe1", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	bill2, err := domain.NewProductionBill("bench2", "recipe2", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a1, err := domain.NewProductionBillAction("bill1", bill1)
	if err != nil {
		t.Fatal(err)
	}
	a2, err := domain.NewProductionBillAction("bill2", bill2)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a1, a2})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitProjectMethod(ctx, g.Project.ID, g.Revision, "cook", "", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	if _, err = s.Prepare(ctx, "bill-plan", "bill1", target, tick); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "bill-plan", "bill1", target, tick); err != nil {
		t.Fatal(err)
	}
	// bill2 is deliberately left Pending: never prepared or dispatched.
	r.Facts.Cooking = domain.Known(true)
	out = reviewRounds(t, s, &r)
	g = roundsProject(t, out, policy.EnsureCooking)
	if g.Project.Finding != domain.FindingMet || g.Project.Status != domain.ProjectOpen {
		t.Fatal(g)
	}
	if err = s.AuthorizeRoundsPlan(ctx, r.Current, target); err == nil {
		t.Fatal("recovered need authorized a plan with an undispatched bill action")
	}
}

// A dialog answer plan committed under the AnswerDialog incident is a supported
// routine method: the worker dispatches it under the root authority like
// any building family.
func TestRoundsExecutionAuthorizesDialogAnswerPlan(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.ChoiceDialog = domain.Known(true)
	out := reviewRounds(t, s, &r)
	b, ok := out.Review.Incident(policy.AnswerDialog)
	if !ok || b.Situation != domain.SituationActive {
		t.Fatal(out.Review.Incidents)
	}
	answer, err := domain.NewDialogAnswer(3, 1, "OK")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewDialogAnswerAction("dialog", answer)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("dialog-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitIncidentMethod(ctx, b.Incident, "dialog", "", plan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "dialog-plan", 1
	if err = s.AuthorizeRoundsPlan(ctx, r.Current, target); err != nil {
		t.Fatal("dialog answer plan was not authorized", err)
	}
}

// An authority toggle bumps the native generation with the world unchanged;
// the reviewed method still authorizes under the bumped root, and a
// different load does not.
func TestRoundsExecutionAuthorizesAfterNativeGenerationBump(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.ChoiceDialog = domain.Known(true)
	out := reviewRounds(t, s, &r)
	b, ok := out.Review.Incident(policy.AnswerDialog)
	if !ok {
		t.Fatal(out.Review.Incidents)
	}
	answer, err := domain.NewDialogAnswer(3, 1, "OK")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewDialogAnswerAction("dialog", answer)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("dialog-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitIncidentMethod(ctx, b.Incident, "dialog", "", plan); err != nil {
		t.Fatal(err)
	}
	bumped := r.Current
	bumped.Native = 3
	target := bumped
	target.Plan, target.Revision = "dialog-plan", 1
	if err = s.AuthorizeRoundsPlan(ctx, bumped, target); err != nil {
		t.Fatal("generation bump refused the reviewed method:", err)
	}
	other := bumped
	other.Load = "other-load"
	target = other
	target.Plan, target.Revision = "dialog-plan", 1
	if err = s.AuthorizeRoundsPlan(ctx, other, target); err == nil {
		t.Fatal("a different load authorized the reviewed method")
	}
}

// A method admitted for a deficit that clears before any step dispatches it
// (vanilla hauled the stack, the player mended the wall) is settled by the
// review that observes the recovery: its never-dispatched actions cancel and
// the goal satisfies instead of staying Active behind a plan the worker
// refuses on every step.
func TestRoundsRecoverySettlesUndispatchedMethod(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	r.Facts.Cooking = domain.Known(false)
	tick := r.Tick
	g := roundsProject(t, reviewRounds(t, s, &r), policy.EnsureCooking)
	bill, err := domain.NewProductionBill("bench", "recipe", domain.FoodTarget, 10)
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewProductionBillAction("bill", bill)
	if err != nil {
		t.Fatal(err)
	}
	billPlan, err := domain.NewPlan("bill-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitProjectMethod(ctx, g.Project.ID, g.Revision, "cook", "", billPlan); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "bill-plan", 1
	if _, err = s.Prepare(ctx, "bill-plan", "bill", target, tick); err != nil {
		t.Fatal(err)
	}
	if p, err := s.LoadPlan(ctx, "bill-plan"); err != nil || p.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(p, err)
	}
	r.Facts.Cooking = domain.Known(true)
	g = roundsProject(t, reviewRounds(t, s, &r), policy.EnsureCooking)
	if g.Project.Finding != domain.FindingMet || g.Project.Status != domain.ProjectCompleted {
		t.Fatal("recovered goal kept its undispatched method open", g.Project)
	}
	p, err := s.LoadPlan(ctx, "bill-plan")
	if err != nil {
		t.Fatal(err)
	}
	if v := p.Progress[0].View(); v.Stage != domain.Cancelled || v.Attempt != 0 {
		t.Fatal("undispatched method was not cancelled", v)
	}
	if err = s.AuthorizeRoundsPlan(ctx, r.Current, target); err == nil {
		t.Fatal("cancelled method authorized")
	}
	// An open fight keeps its settled plan: retired, it would drop
	// out of its goal and the clock would stop admitting ticks mid-fight.
	world := World{Colony: r.Current.Colony, Load: r.Current.Load, Map: r.Current.Map}
	if err = s.OpenCombatFight(ctx, "bill-plan", policy.CombatMemory{}, world, []domain.PawnID{"a"}); err != nil {
		t.Fatal(err)
	}
	reviewRounds(t, s, &r)
	if p, err = s.LoadPlan(ctx, "bill-plan"); err != nil || p.Retired {
		t.Fatal("open fight's plan retired", p.Retired, err)
	}
	if err = s.CloseCombatFight(ctx, "bill-plan"); err != nil {
		t.Fatal(err)
	}
	// The next review retires the settled plan.
	reviewRounds(t, s, &r)
	if p, err = s.LoadPlan(ctx, "bill-plan"); err != nil || !p.Retired {
		t.Fatal("settled plan not retired", p.Retired, err)
	}
}

// A stone-shell bundle carries WallRemovalActions beside its Wall
// builds; the routine allowlist must authorize the whole bundle, or the
// Worker never dispatches the demolition its replacement depends on.
func TestRoundsExecutionAuthorizesWallRemovalBundle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Current.Native = 2
	g := roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
	removal, err := domain.NewWallRemoval("original-wall", "", domain.Cell{X: 0, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	demolish, err := domain.NewWallRemovalAction("demolish", removal)
	if err != nil {
		t.Fatal(err)
	}
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 0, Z: 1}, domain.North, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	replace, err := domain.NewBuildingAction("replace", building, domain.TierExpand)
	if err != nil {
		t.Fatal(err)
	}
	bundle, err := domain.NewPlan("shell-plan", 1, []domain.Action{demolish, replace}, domain.ActionDependency{Action: "replace", Requires: "demolish"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "shell", bundle); err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "shell-plan", 1
	if err = s.AuthorizeRoundsPlan(ctx, r.Current, target); err != nil {
		t.Fatal("a bundle carrying a wall removal was not authorized", err)
	}
}

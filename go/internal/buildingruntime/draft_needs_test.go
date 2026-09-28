package buildingruntime

import (
	"context"
	"path/filepath"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func draftedRow(id, job string) *n.PawnState {
	row := &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(id)}, Colonist: proto.Bool(true), Drafted: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false),
		Issues: []*n.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
	if job != "" {
		row.Job = &n.JobEvidence{DefName: proto.String(job)}
	}
	return row
}

// The sweep undrafts only drafted colonists no plan needs and no custody
// job holds (#939).
func TestUndraftCandidatesSkipNeededAndCustody(t *testing.T) {
	t.Parallel()
	undrafted := draftedRow("idle-undrafted", "Wait")
	undrafted.Drafted = proto.Bool(false)
	rows := []*n.PawnState{draftedRow("stray", "Wait_Combat"), draftedRow("needed", "Wait_Combat"), draftedRow("arrester", "Arrest"),
		draftedRow("capturer", "Capture"), draftedRow("jobless", ""), undrafted, draftedRow("another", "Goto")}
	got := undraftCandidates(rows, map[domain.PawnID]bool{"needed": true})
	if want := []domain.PawnID{"another", "stray"}; !slices.Equal(got, want) {
		t.Fatalf("candidates %v, want %v", got, want)
	}
}

// A draft still in flight and an open fight's roster are needed; a
// settled draft no order holds is not.
func TestPlannedDraftsCoversInFlightAndFights(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db, err := store.Open(ctx, filepath.Join(t.TempDir(), "needs.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	var plan domain.PlanSpec
	for _, pawn := range []domain.PawnID{"pending", "done"} {
		d, _ := domain.NewOwnedDraft(pawn)
		a, _ := domain.NewOwnedDraftAction(domain.ActionID("draft-"+pawn), d)
		if plan, err = domain.NewPlan(domain.PlanID("needs-"+pawn), 1, []domain.Action{a}); err != nil {
			t.Fatal(err)
		}
		if err = db.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
	}
	fight, _ := domain.NewPlan("fight", 1, nil)
	if err = db.CreatePlan(ctx, fight); err != nil {
		t.Fatal(err)
	}
	if err = db.OpenCombatFight(ctx, "fight", policy.CombatMemory{}, store.World{Colony: "colony", Load: "load", Map: 1}, []domain.PawnID{"fighter"}); err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: plan.ID(), Revision: 1, Native: 2}
	if _, err = db.Prepare(ctx, plan.ID(), "draft-done", s, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(ctx, plan.ID(), "draft-done", s, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, plan.ID(), "draft-done", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	needed, err := plannedDrafts(ctx, db)
	if err != nil {
		t.Fatal(err)
	}
	if !needed["pending"] || !needed["fighter"] || needed["done"] {
		t.Fatal(needed)
	}
}

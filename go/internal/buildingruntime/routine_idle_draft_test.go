package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type idleDraftNative struct {
	*routineNative
	emergency policy.EmergencyFacts
}

func (n *idleDraftNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	reading, result, err := n.routineNative.ReadEmergency(ctx, id)
	reading.Facts = n.emergency
	return reading, result, err
}

func TestIdleDraftObservationGuards(t *testing.T) {
	t.Parallel()
	for _, scenario := range []string{"idle", "owned", "unknown-claim", "undrafted", "downed", "mental", "forced", "queued", "unknown-job", "hostile", "unknown-threats", "held", "stale-generation", "stale-world", "stale-tick", "bad-token"} {
		t.Run(scenario, func(t *testing.T) {
			r, db, session, _, native := routineFixture(t)
			ctx := context.Background()
			state := session.State()
			context := proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)
			row := &n.PawnState{Pawn: &n.EntityRef{Id: proto.String("pawn"), Snapshot: &n.SnapshotRef{EntityId: proto.String("pawn"), Token: proto.String("cas"), Context: proto.Clone(context).(*c.ObservationContext)}},
				Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(true),
				DraftClaim: &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}},
				Job:        &n.JobEvidence{PlayerForced: proto.Bool(false), QueuedJobs: proto.Uint32(0)},
				Issues:     []*n.ReadIssue{{Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
			native.pawnReply = &n.ListPawnsReply{Outcome: &n.ListPawnsReply_Observed{Observed: &n.PawnSnapshot{Context: context, Pawns: []*n.PawnState{row}, Completeness: &n.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Unreadable: proto.Uint64(0)}}}}
			emergency := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn"}}}
			var plans []store.PlanState
			wantErr := false
			tick := domain.Tick(7)
			switch scenario {
			case "owned":
				row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Owned{Owned: &n.OwnedDraftClaim{ClaimId: proto.String("other")}}}
			case "unknown-claim":
				row.DraftClaim = nil
			case "undrafted":
				row.Drafted = proto.Bool(false)
			case "downed":
				row.Downed = proto.Bool(true)
			case "mental":
				row.MentalState = proto.String("Berserk")
			case "forced":
				row.Job.PlayerForced = proto.Bool(true)
			case "queued":
				row.Job.QueuedJobs = proto.Uint32(1)
			case "unknown-job":
				row.Job = nil
			case "hostile":
				emergency.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile}}
			case "unknown-threats":
				emergency.ThreatsComplete = domain.Unknown[bool]()
			case "held":
				draft, _ := domain.NewOwnedDraft("pawn")
				action, _ := domain.NewOwnedDraftAction("hold-draft", draft)
				plan, _ := domain.NewPlan("defensive-position", 1, []domain.Action{action})
				if err := db.CreatePlan(ctx, plan); err != nil {
					t.Fatal(err)
				}
				stored, err := db.LoadPlan(ctx, plan.ID())
				if err != nil {
					t.Fatal(err)
				}
				plans = append(plans, stored)
			case "stale-generation":
				context.NativeGeneration = proto.Uint64(context.GetNativeGeneration() + 1)
				wantErr = true
			case "stale-world":
				context.Identity.LoadToken = proto.String("other")
				wantErr = true
			case "stale-tick":
				tick += domain.PlanningTickTolerance + domain.LiveDrift() + 1
				wantErr = true
			case "bad-token":
				row.Pawn.Snapshot.Token = nil
			}
			got, err := r.idleDrafts(ctx, state, tick, emergency, plans)
			if (err != nil) != wantErr {
				t.Fatalf("candidates=%v err=%v", got, err)
			}
			if (len(got) == 1) != (scenario == "idle") {
				t.Fatalf("candidates=%v", got)
			}
			if scenario != "idle" {
				return
			}
			// Drive the real goal admission. A draft-only method has no order
			// holding it once complete, so the existing worker releases it.
			facts := policy.RoutineFacts{Hostiles: domain.Known(int64(0)), CleanupPawns: domain.Known(true), CriticalPatients: domain.Known(int64(0)), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}
			if _, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Current: state.Snapshot, Tick: 7, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts}); err != nil {
				t.Fatal(err)
			}
			// Supply the same peaceful roster to the planner's emergency read.
			r.native = &idleDraftNative{routineNative: native, emergency: emergency}
			call, epoch, done, err := r.player.enter(ctx, false)
			if err != nil {
				t.Fatal(err)
			}
			defer done()
			if err = r.restoreIdleDrafts(call, epoch, newStepArbiter()); err != nil {
				t.Fatal(err)
			}
			plans, err = db.LoadPlans(ctx, 256)
			if err != nil {
				t.Fatal(err)
			}
			found := false
			for _, plan := range plans {
				if len(plan.Spec.Actions()) != 1 {
					continue
				}
				a := plan.Spec.Actions()[0]
				draft, ok := a.OwnedDraft()
				if !ok {
					continue
				}
				found = true
				if !idleDraftWorkOpen(plan) {
					t.Fatal("pending adoption must retain the RestoreWorkers deficit")
				}
				if draft.Pawn() != "pawn" || workerPlanHoldsDraft(plan, domain.ProgressView{Action: a.ID(), Stage: domain.Completed}) {
					t.Fatal(plan)
				}
			}
			if !found {
				t.Fatal("no adopt-and-release method")
			}
		})
	}
}

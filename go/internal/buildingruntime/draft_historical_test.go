package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestLostDraftReplyThenPlayerReplacementRecoversOnlyHistoricalClaim(t *testing.T) {
	for _, kind := range []string{"undrafted", "replacement-owned", "unsuccessful", "no-change"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draftBoundaryFixture(t)
			claim := draftKnownClaim(f)
			if kind == "replacement-owned" {
				f.row.DraftClaim.GetOwned().ClaimId = proto.String("replacement")
				f.row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(2)
			} else {
				f.row.Drafted = proto.Bool(false)
				f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
			}
			if kind == "unsuccessful" {
				f.progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED.Enum()}}
			}
			if kind == "no-change" {
				effect := f.receipt.GetApplied().Observed
				effect.GetJob().Issued = proto.Bool(false)
				f.receipt.Outcome = &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: effect}}
			}
			inspected, err := b.InspectDraftCleanup(context.Background(), f.p, domain.DraftCleanup{Stage: domain.DraftAwaitingClaim})
			if err != nil || inspected.Reconcile == nil {
				t.Fatal(inspected, err)
			}
			evidence := inspected.Reconcile
			got, known := evidence.Claim.Value()
			if !known || got != claim {
				t.Fatal(evidence)
			}
			if kind == "unsuccessful" {
				if evidence.Observation.Effect != domain.EffectUnsuccessful || evidence.Observation.UnsuccessfulReason != domain.NativeInterrupted {
					t.Fatal(evidence)
				}
			} else if evidence.Observation.Effect != domain.EffectUnknown {
				t.Fatal("historical completion invented", evidence)
			}
			next, err := b.InspectDraftCleanup(context.Background(), f.p, domain.DraftCleanup{Stage: domain.DraftCleanupRequired, Claim: evidence.Claim})
			if err != nil || next.Supersession == nil || next.Request != nil {
				t.Fatal(next, err)
			}
			if f.writes != 0 || f.releases != 0 || f.leases != 0 {
				t.Fatal("recovery wrote or acquired")
			}
		})
	}
}

func TestHistoricalClaimRejectsMissingOrContradictoryProof(t *testing.T) {
	for _, kind := range []string{"unavailable", "uncertain", "unverified", "no-token", "wrong-owner", "wrong-attempt", "wrong-generation", "future-receipt", "missing-cas", "incomplete-replacement-owner"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draftBoundaryFixture(t)
			f.row.Drafted = proto.Bool(false)
			f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
			switch kind {
			case "unavailable":
				f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unavailable{Unavailable: &c.Unavailable{}}}
			case "uncertain":
				effect := f.receipt.GetApplied().Observed
				f.receipt.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{LastObserved: effect}}
			case "unverified":
				f.receipt.GetApplied().Observed.GetJob().Verified = proto.Bool(false)
			case "no-token":
				f.receipt.GetApplied().Observed.GetJob().ResultingSnapshotToken = nil
			case "wrong-owner":
				f.receipt.AuthorizingOwner.PlayerDirection = proto.Uint64(2)
			case "wrong-attempt":
				f.receipt.Attempt.AttemptId = proto.Uint64(2)
			case "wrong-generation":
				f.receipt.AdmittedContext.NativeGeneration = proto.Uint64(1)
			case "future-receipt":
				f.receipt.AdmittedContext.Tick = proto.Int64(11)
			case "missing-cas":
				f.row.Pawn.Snapshot = nil
			case "incomplete-replacement-owner":
				f.row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Owned{Owned: &n.OwnedDraftClaim{ClaimId: proto.String("other")}}}
			}
			out, err := b.ObserveDraft(context.Background(), f.p, f.p.Snapshot)
			if err == nil {
				t.Fatal("unproven historical claim accepted", out)
			}
			if _, known := out.Claim.Value(); known {
				t.Fatal("error retained claim", out)
			}
			if f.writes != 0 || f.leases != 0 || f.releases != 0 {
				t.Fatal("read mutated")
			}
		})
	}
}

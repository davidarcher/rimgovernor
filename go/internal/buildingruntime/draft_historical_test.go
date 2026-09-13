package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestLostDraftReplyThenPlayerReplacementRecoversOnlyHistoricalClaim(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"undrafted", "replacement-owned", "unsuccessful", "no-change"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draft.NewFixture(t)
			claim := draft.KnownClaim(f)
			if kind == "replacement-owned" {
				f.Row.DraftClaim.GetOwned().ClaimId = proto.String("replacement")
				f.Row.DraftClaim.GetOwned().Owner.PlayerDirection = proto.Uint64(2)
			} else {
				f.Row.Drafted = proto.Bool(false)
				f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
			}
			if kind == "unsuccessful" {
				f.Progress.Effect = &r.Progress_Unsuccessful{Unsuccessful: &r.UnsuccessfulEffect{Reason: r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED.Enum()}}
			}
			if kind == "no-change" {
				effect := f.Receipt.GetApplied().Observed
				effect.GetJob().Issued = proto.Bool(false)
				f.Receipt.Outcome = &r.Receipt_NoChange{NoChange: &r.NoChange{Observed: effect}}
			}
			inspected, err := b.InspectDraftCleanup(context.Background(), f.P, domain.DraftCleanup{Stage: domain.DraftAwaitingClaim})
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
			next, err := b.InspectDraftCleanup(context.Background(), f.P, domain.DraftCleanup{Stage: domain.DraftCleanupRequired, Claim: evidence.Claim})
			if err != nil || next.Supersession == nil || next.Request != nil {
				t.Fatal(next, err)
			}
			if f.Writes != 0 || f.Releases != 0 || f.Leases != 0 {
				t.Fatal("recovery wrote or acquired")
			}
		})
	}
}

func TestHistoricalClaimRejectsMissingOrContradictoryProof(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"unavailable", "uncertain", "unverified", "no-token", "wrong-owner", "wrong-attempt", "wrong-generation", "future-receipt", "missing-cas", "incomplete-replacement-owner"} {
		t.Run(kind, func(t *testing.T) {
			b, f := draft.NewFixture(t)
			f.Row.Drafted = proto.Bool(false)
			f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
			switch kind {
			case "unavailable":
				f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unavailable{Unavailable: &c.Unavailable{}}}
			case "uncertain":
				effect := f.Receipt.GetApplied().Observed
				f.Receipt.Outcome = &r.Receipt_Uncertain{Uncertain: &r.Uncertain{LastObserved: effect}}
			case "unverified":
				f.Receipt.GetApplied().Observed.GetJob().Verified = proto.Bool(false)
			case "no-token":
				f.Receipt.GetApplied().Observed.GetJob().ResultingSnapshotToken = nil
			case "wrong-owner":
				f.Receipt.AuthorizingOwner.PlayerDirection = proto.Uint64(2)
			case "wrong-attempt":
				f.Receipt.Attempt.AttemptId = proto.Uint64(2)
			case "wrong-generation":
				f.Receipt.AdmittedContext.NativeGeneration = proto.Uint64(1)
			case "future-receipt":
				f.Receipt.AdmittedContext.Tick = proto.Int64(11)
			case "missing-cas":
				f.Row.Pawn.Snapshot = nil
			case "incomplete-replacement-owner":
				f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Owned{Owned: &n.OwnedDraftClaim{ClaimId: proto.String("other")}}}
			}
			out, err := b.ObserveDraft(context.Background(), f.P, f.P.Snapshot)
			if err == nil {
				t.Fatal("unproven historical claim accepted", out)
			}
			if _, known := out.Claim.Value(); known {
				t.Fatal("error retained claim", out)
			}
			if f.Writes != 0 || f.Leases != 0 || f.Releases != 0 {
				t.Fatal("read mutated")
			}
		})
	}
}

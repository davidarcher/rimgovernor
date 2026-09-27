package draft

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// A fight's claim (#910) is released only while its pawn still carries
// that claim in the fight's world; every other state settles it without a
// write, and an uncertain release keeps it.
func TestReleaseFightClaim(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	world := store.World{Colony: "colony", Load: "load", Map: 0}
	for _, tc := range []struct {
		name     string
		world    store.World
		claim    string
		edit     func(*Fixture)
		gone     bool
		releases int
		err      error
	}{
		{name: "owned", world: world, claim: "claim", gone: true, releases: 1},
		{name: "learned", world: world, claim: "", gone: true, releases: 1},
		{name: "already released", world: world, claim: "claim", gone: true, releases: 1, edit: func(f *Fixture) {
			f.MutateRelease = func(v *o.ReleaseOwnedDraftReply) {
				v.Outcome = &o.ReleaseOwnedDraftReply_AlreadyReleased{AlreadyReleased: v.GetReleased()}
			}
		}},
		{name: "uncertain", world: world, claim: "claim", releases: 1, edit: func(f *Fixture) {
			f.MutateRelease = func(v *o.ReleaseOwnedDraftReply) {
				v.Outcome = &o.ReleaseOwnedDraftReply_Uncertain{Uncertain: &o.DraftReleaseUncertain{}}
			}
		}},
		{name: "other world", world: store.World{Colony: "colony", Load: "other", Map: 0}, claim: "claim", gone: true},
		{name: "other claim", world: world, claim: "mine", gone: true},
		{name: "absent", world: world, claim: "claim", gone: true, edit: func(f *Fixture) { f.Row = nil }},
		{name: "unowned", world: world, claim: "claim", gone: true, edit: func(f *Fixture) {
			f.Row.DraftClaim = &n.DraftClaimObservation{State: &n.DraftClaimObservation_Unowned{Unowned: &n.NoOwnedDraftClaim{}}}
		}},
		{name: "claim unread", world: world, claim: "claim", err: executor.ErrHeld, edit: func(f *Fixture) { f.Row.DraftClaim = nil }},
	} {
		b, f := NewFixture(t)
		if tc.edit != nil {
			tc.edit(f)
		}
		gone, err := b.ReleaseFightClaim(ctx, tc.world, "pawn", tc.claim)
		if gone != tc.gone || f.Releases != tc.releases || tc.err == nil && err != nil || tc.err != nil && !errors.Is(err, tc.err) {
			t.Errorf("%s: gone %v releases %d err %v", tc.name, gone, f.Releases, err)
		}
		if f.Releases > 0 && (f.LastRelease.GetExpectedClaimId() != "claim" || f.LastRelease.GetPawn().GetExpectedSnapshotToken() != "cas" || !proto.Equal(f.LastRelease.Identity, f.Ctx.Identity)) {
			t.Errorf("%s: release %v", tc.name, f.LastRelease)
		}
	}
}

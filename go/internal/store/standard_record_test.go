package store

import (
	"context"
	"errors"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestGoalRecordRidesTheGoalBlobAndRebuilds (#1740): a goal's record is saved
// at the goal's revision, replaced atomically with a method, bounded, and
// comes back from the save's goal blob.
func TestGoalRecordRidesTheGoalBlobAndRebuilds(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	recorded, err := s.RecordStandard(ctx, g.Standard.ID, g.Revision, `{"k":1}`)
	if err != nil || recorded.Standard.Record != `{"k":1}` || recorded.Revision != g.Revision+1 {
		t.Fatal(recorded, err)
	}
	if _, err = s.RecordStandard(ctx, g.Standard.ID, g.Revision, "stale"); !errors.Is(err, ErrConflict) {
		t.Fatal("a stale revision wrote a record:", err)
	}
	if _, err = s.RecordStandard(ctx, g.Standard.ID, recorded.Revision, strings.Repeat("x", domain.MaxStandardRecord+1)); err == nil {
		t.Fatal("an oversized record was saved")
	}
	withMethod, err := s.CommitMethodRecord(ctx, g.Standard.ID, recorded.Revision, "m", "why", plan(t, "record-plan", "record-action"), `{"k":2}`)
	if err != nil || withMethod.Standard.Record != `{"k":2}` || len(withMethod.Methods) != 1 {
		t.Fatal(withMethod, err)
	}
	blobs, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	other := open(t, filepath.Join(t.TempDir(), "other.db"))
	if err = other.RebuildStandards(ctx, blobs, nil); err != nil {
		t.Fatal(err)
	}
	back, err := other.LoadStandard(ctx, g.Standard.ID)
	if err != nil || back.Standard.Record != `{"k":2}` {
		t.Fatal(back, err)
	}
}

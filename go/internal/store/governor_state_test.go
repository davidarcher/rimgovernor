package store

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGovernorStateBlobsMirrorGoals(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	blobs, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	key := GovernorGoalKeyPrefix + string(g.Goal.ID)
	var got GovernorGoalBlob
	if len(blobs) != 1 || json.Unmarshal([]byte(blobs[key]), &got) != nil {
		t.Fatal(blobs)
	}
	if got.SchemaVersion != GovernorStateSchemaVersion || got.Revision != g.Revision || got.Goal != g.Goal {
		t.Fatal(got, g)
	}
	var shape map[string]json.RawMessage
	if json.Unmarshal([]byte(blobs[key]), &shape) != nil || len(shape) != 3 || shape["schemaVersion"] == nil || shape["goal"] == nil || shape["revision"] == nil {
		t.Fatal("goal blob is not {schemaVersion, goal, revision}", blobs[key])
	}
	if _, err = s.ReviewGoal(ctx, g.Goal.ID, g.Revision, scope(), 11, domain.NeedRecovered); err != nil {
		t.Fatal(err)
	}
	after, err := s.GovernorStateBlobs(ctx)
	if err != nil || after[key] == blobs[key] {
		t.Fatal("review did not change the goal blob", err)
	}
}

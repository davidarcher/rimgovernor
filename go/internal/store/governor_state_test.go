package store

import (
	"context"
	"encoding/json"
	"reflect"
	"testing"
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
	if json.Unmarshal([]byte(blobs[key]), &shape) != nil || len(shape) != 3 || shape["schemaVersion"] == nil || shape["goal"] == nil || shape["revision"] == nil || GovernorStateSchemaVersion != 2 {
		t.Fatal("goal blob is not v2 {schemaVersion, goal, revision}", blobs[key])
	}
	if drift := GovernorStateDrift(blobs, blobs); drift != nil {
		t.Fatal(drift)
	}
	saved := map[string]string{"family/tidies": "{}", "other": "x"}
	want := []string{"family/tidies: extra", key + ": missing"}
	if drift := GovernorStateDrift(blobs, saved); !reflect.DeepEqual(drift, want) {
		t.Fatal(drift)
	}
	if _, err = s.CancelGoal(ctx, g.Goal.ID, g.Revision); err != nil {
		t.Fatal(err)
	}
	after, err := s.GovernorStateBlobs(ctx)
	if err != nil || after[key] == blobs[key] {
		t.Fatal("cancel did not change the goal blob", err)
	}
}

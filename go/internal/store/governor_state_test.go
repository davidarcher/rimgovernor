package store

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGovernorStateBlobsMirrorStandards(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	blobs, err := s.GovernorStateBlobs(ctx)
	if err != nil {
		t.Fatal(err)
	}
	key := "standard/" + string(g.Standard.ID)
	var got GovernorStandardBlob
	if len(blobs) != 1 || json.Unmarshal([]byte(blobs[key]), &got) != nil {
		t.Fatal(blobs)
	}
	if got.SchemaVersion != GovernorStateSchemaVersion || got.Revision != g.Revision || got.Standard != g.Standard {
		t.Fatal(got, g)
	}
	var shape map[string]json.RawMessage
	if json.Unmarshal([]byte(blobs[key]), &shape) != nil || len(shape) != 3 || shape["schemaVersion"] == nil || shape["standard"] == nil || shape["revision"] == nil {
		t.Fatal("goal blob is not {schemaVersion, standard, revision}", blobs[key])
	}
	if _, err = s.ReviewStandard(ctx, g.Standard.ID, g.Revision, scope(), 11, domain.FindingMet); err != nil {
		t.Fatal(err)
	}
	after, err := s.GovernorStateBlobs(ctx)
	if err != nil || after[key] == blobs[key] {
		t.Fatal("review did not change the goal blob", err)
	}
}

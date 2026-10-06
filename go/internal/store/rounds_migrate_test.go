package store

import (
	"context"
	"encoding/json"
	"testing"
)

// An older row holds the persisted starting-supplies history and a Project
// binding for the removed AllowStartingSupplies concern; both load away.
func TestLoadRoundsMigratesRemovedStartingSupplies(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	out := reviewRounds(t, s, &r)
	data, err := json.Marshal(out.Review)
	if err != nil {
		t.Fatal(err)
	}
	var row map[string]json.RawMessage
	if err = json.Unmarshal(data, &row); err != nil {
		t.Fatal(err)
	}
	row["StartingSupplies"] = json.RawMessage(`{"Initialized":true,"Pending":null}`)
	row["Projects"] = json.RawMessage(`[{"Concern":"AllowStartingSupplies","Project":"project-legacy"}]`)
	if data, err = json.Marshal(row); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("UPDATE rounds SET payload=? WHERE singleton=1", data); err != nil {
		t.Fatal(err)
	}
	loaded, err := s.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range loaded.Projects {
		if binding.Concern == "AllowStartingSupplies" {
			t.Fatal("removed concern still bound", loaded.Projects)
		}
	}
	if loaded.Revision != out.Review.Revision {
		t.Fatal("migration changed the review", loaded.Revision)
	}
}

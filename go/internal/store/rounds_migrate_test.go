package store

import (
	"bytes"
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

// An older row binds the removed MaintainWaste Standard and ranks it; both
// load away, and a plan holding the removed waste action retires.
func TestLoadRoundsMigratesRemovedMaintainWaste(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	out := reviewRounds(t, s, &r)
	data, err := json.Marshal(out.Review)
	if err != nil {
		t.Fatal(err)
	}
	var row map[string]any
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err = decoder.Decode(&row); err != nil {
		t.Fatal(err)
	}
	row["Standards"] = append(row["Standards"].([]any), map[string]any{"Concern": "MaintainWaste", "Standard": "routine-legacy-MaintainWaste"})
	dev := row["Development"].(map[string]any)
	dev["Rows"] = append(dev["Rows"].([]any), map[string]any{"Concern": "MaintainWaste"})
	row["Progress"] = append(asSlice(row["Progress"]), map[string]any{"Concern": "MaintainWaste"})
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
	for _, binding := range loaded.Standards {
		if binding.Concern == "MaintainWaste" {
			t.Fatal("removed concern still bound", loaded.Standards)
		}
	}
	for _, development := range loaded.Development.Rows {
		if development.Concern == "MaintainWaste" {
			t.Fatal("removed concern still ranked", loaded.Development.Rows)
		}
	}
	for _, progress := range loaded.Progress {
		if progress.Concern == "MaintainWaste" {
			t.Fatal("removed concern still tracked", loaded.Progress)
		}
	}
}

func asSlice(v any) []any {
	s, _ := v.([]any)
	return s
}

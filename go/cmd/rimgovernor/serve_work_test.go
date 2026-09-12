package main

import (
	"io"
	"path/filepath"
	"testing"
)

func TestWorkServeRequiresReviewsAndCanOwnRoutineMethods(t *testing.T) {
	dir := t.TempDir()
	base := []string{"--gabs", filepath.Join(dir, "gabs"), "--config", dir, "--game", "game", "--state", filepath.Join(dir, "state.db"), "--clock-control", "--player-control", "--profile", dir, "--routine-work-plans"}
	if _, err := parseServe(base, io.Discard); err == nil {
		t.Fatal("work planner without reviews")
	}
	config, err := parseServe(append(base, "--routine-reviews", "--routine-methods"), io.Discard)
	if err != nil || !config.routineWorkPlans || !config.routineMethods {
		t.Fatal(config, err)
	}
}

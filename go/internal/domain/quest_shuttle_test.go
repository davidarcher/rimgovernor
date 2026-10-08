package domain

import (
	"slices"
	"testing"
)

func TestQuestShuttleClosedLoadingAndCanonicalPawns(t *testing.T) {
	s, err := NewQuestShuttle("Quest_1", ShuttleExplicitPawns, false, []PawnID{"Pawn_2", "Pawn_1"}, false)
	if err != nil || !slices.Equal(s.Pawns(), []PawnID{"Pawn_1", "Pawn_2"}) {
		t.Fatal(s, err)
	}
	a, err := NewQuestShuttleAction("load", s)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = NewPlan("p", 1, []Action{a}); err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		mode   ShuttleLoading
		auto   bool
		pawns  []PawnID
		launch bool
	}{
		{ShuttleLaunchOnly, false, nil, false}, {ShuttleAutoload, true, []PawnID{"Pawn_1"}, false},
		{ShuttleExplicitPawns, false, nil, false}, {ShuttleExplicitPawns, true, []PawnID{"Pawn_1"}, false},
		{ShuttleExplicitPawns, false, []PawnID{"Pawn_1", "Pawn_1"}, false}, {"invalid", false, nil, true},
	} {
		if _, err := NewQuestShuttle("Quest_1", tc.mode, tc.auto, tc.pawns, tc.launch); err == nil {
			t.Fatal("invalid shuttle accepted", tc)
		}
	}
}

package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestQuestShuttleActionRoundTrips(t *testing.T) {
	ctx := context.Background()
	s := open(t, memoryPath(t))
	for i, tc := range []struct {
		mode   domain.ShuttleLoading
		auto   bool
		pawns  []domain.PawnID
		launch bool
	}{
		{domain.ShuttleAutoload, true, nil, false}, {domain.ShuttleAutoload, false, nil, false},
		{domain.ShuttleExplicitPawns, false, []domain.PawnID{"Pawn_2", "Pawn_1"}, false},
		{domain.ShuttleLaunchOnly, false, nil, true},
	} {
		want, err := domain.NewQuestShuttle("Quest_1", tc.mode, tc.auto, tc.pawns, tc.launch)
		if err != nil {
			t.Fatal(err)
		}
		id := domain.PlanID(fmt.Sprintf("shuttle-%d", i))
		a, err := domain.NewQuestShuttleAction(domain.ActionID(string(id)+"-0"), want)
		if err != nil {
			t.Fatal(err)
		}
		p, err := domain.NewPlan(id, 1, []domain.Action{a})
		if err != nil {
			t.Fatal(err)
		}
		if err = s.CreatePlan(ctx, p); err != nil {
			t.Fatal(err)
		}
		loaded, err := s.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		got, ok := loaded.Spec.Actions()[0].QuestShuttle()
		if !ok || got != want {
			t.Fatalf("%+v != %+v", got, want)
		}
	}
}

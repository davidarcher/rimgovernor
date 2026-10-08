package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"testing"
)

func TestQuestShuttleIntentPreservesLoadingModes(t *testing.T) {
	for _, tc := range []struct {
		mode   domain.ShuttleLoading
		auto   bool
		pawns  []domain.PawnID
		launch bool
	}{
		{domain.ShuttleAutoload, true, nil, false}, {domain.ShuttleAutoload, false, nil, false},
		{domain.ShuttleExplicitPawns, false, []domain.PawnID{"Pawn_1"}, false}, {domain.ShuttleLaunchOnly, false, nil, true},
	} {
		value, err := domain.NewQuestShuttle("Quest_1", tc.mode, tc.auto, tc.pawns, tc.launch)
		if err != nil {
			t.Fatal(err)
		}
		a, _ := domain.NewQuestShuttleAction("shuttle", value)
		wire, err := IntentAction("key", a)
		if err != nil {
			t.Fatal(err)
		}
		s := wire.GetQuestShuttle()
		if s.GetQuestId() != "Quest_1" || s.GetLaunch() != tc.launch {
			t.Fatal(s)
		}
		switch tc.mode {
		case domain.ShuttleAutoload:
			v, ok := s.Loading.(*o.QuestShuttleIntent_Autoload)
			if !ok || v.Autoload != tc.auto {
				t.Fatal(s)
			}
		case domain.ShuttleExplicitPawns:
			if s.GetExplicitPawns().GetPawnIds()[0] != "Pawn_1" {
				t.Fatal(s)
			}
		default:
			if s.Loading != nil {
				t.Fatal(s)
			}
		}
	}
}

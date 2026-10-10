package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

type restorationNative struct {
	RoundsDefenseSource
	combat bridge.Combat
}

func (n restorationNative) ReadCombat(context.Context, *c.Identity) (bridge.Combat, error) {
	return n.combat, nil
}

// A door or animal order whose original setting is unreadable is dropped from
// the batch; it must not fail the drafts and attacks sent beside it, or the
// colony stops answering the hostile every round.
func TestCaptureCombatSettingsDropsUnreadableOriginals(t *testing.T) {
	ctx := context.Background()
	journal, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close() })
	door := domain.Cell{X: 4, Z: 5}
	planner := &RoundsDefensePlanner{
		reviewer: &Rounder{player: &Player{journal: journal}},
		native: restorationNative{combat: bridge.Combat{DoorStates: domain.Known([]policy.RoomDoor{
			{Cell: door, HoldOpen: domain.Known(false), Forbidden: domain.Known(false)},
		})}},
	}
	state := ControlState{Snapshot: domain.GenerationSnapshot{Colony: "c", Load: "l", Map: 0}}
	attack := policy.CombatOrder{Kind: policy.OrderAttack, Pawn: "gunner", Target: "muffalo"}
	orders := []policy.CombatOrder{
		{Kind: policy.OrderAnimalArea, Pawn: "pet"},
		{Kind: policy.OrderDoor, Cell: domain.Cell{X: 9, Z: 9}, Door: "close"},
		{Kind: policy.OrderDoor, Cell: door, Door: "close"},
		attack,
	}
	sendable, err := planner.captureCombatSettings(ctx, state, "fight", orders)
	if err != nil {
		t.Fatal("unreadable originals failed the whole batch:", err)
	}
	if len(sendable) != 2 || sendable[0].Cell != door || sendable[1] != attack {
		t.Fatal("sendable orders:", sendable)
	}
	kept, ok, err := journal.LoadCombatRestoration(ctx)
	if err != nil || !ok || len(kept.Doors) != 1 || len(kept.Animals) != 0 {
		t.Fatal("kept originals:", kept, ok, err)
	}
}

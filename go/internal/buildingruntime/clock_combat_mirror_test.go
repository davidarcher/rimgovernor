package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func combatEpoch() *mp.Epoch {
	return &mp.Epoch{Process: proto.String("p"), Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)}}
}

func wm(tick int64, seq uint32) *mp.Watermark {
	return &mp.Watermark{Tick: proto.Int64(tick), Seq: proto.Uint32(seq)}
}

func combatPawn(id string, downed bool) *mp.CombatPawn {
	return &mp.CombatPawn{Id: proto.String(id), Side: mp.CombatSide_COMBAT_SIDE_COLONIST.Enum(), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Downed: proto.Bool(downed)}
}

func combatEvent(tick int64, seq uint32, kind mp.CombatLogKind) *mp.CombatEventRow {
	return &mp.CombatEventRow{At: wm(tick, seq), Kind: kind.Enum()}
}

func heldCombatPawns(t *testing.T, f *clockFacts) map[string]*mp.CombatPawn {
	t.Helper()
	table, ok := mirror.Get[string, *mp.CombatPawn](f.mirror, mirrorScopeOf(combatEpoch()), string(facts.CombatPawns))
	if !ok {
		t.Fatal("combat pawns not held")
	}
	return table.Rows
}

// The combat pawns section keyframes, then a delta lays a downing over it
// and a tombstone removes a despawned pawn (#851); the events section
// appends rows keyed by their own watermark.
func TestCombatSectionsApply(t *testing.T) {
	f := newClockFacts(nil, facts.NewStore())
	ctx := context.Background()
	page := func(sections ...*mp.SectionPage) *mp.MirrorPage {
		return &mp.MirrorPage{Epoch: combatEpoch(), CompleteThroughTick: proto.Int64(0), Sections: sections}
	}
	pawns := mp.Section_SECTION_COMBAT_PAWNS.Enum()
	events := mp.Section_SECTION_COMBAT_EVENTS.Enum()
	applied := f.applyMirrorPage(ctx, page(
		&mp.SectionPage{Section: pawns, Body: &mp.SectionPage_Keyframe{Keyframe: &mp.Keyframe{At: wm(10, 1), CombatPawns: []*mp.CombatPawn{combatPawn("a", false), combatPawn("b", false)}}}},
		&mp.SectionPage{Section: events, Body: &mp.SectionPage_Keyframe{Keyframe: &mp.Keyframe{At: wm(10, 1), CombatEvents: []*mp.CombatEventRow{combatEvent(9, 3, mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED)}}}},
	))
	if len(applied.served) != 2 {
		t.Fatalf("served %v", applied.served)
	}
	if rows := heldCombatPawns(t, f); len(rows) != 2 || rows["a"].GetDowned() {
		t.Fatalf("keyframe rows %v", rows)
	}
	downed := combatPawn("a", true)
	downed.Changed = wm(12, 4)
	down := combatEvent(12, 4, mp.CombatLogKind_COMBAT_LOG_KIND_DOWNED)
	down.Stop = k.CombatEvent_COMBAT_EVENT_DOWNED.Enum()
	f.applyMirrorPage(ctx, page(
		&mp.SectionPage{Section: pawns, Body: &mp.SectionPage_Delta{Delta: &mp.Delta{From: wm(10, 1), To: wm(12, 5), CombatPawns: []*mp.CombatPawn{downed}, Tombstones: []string{"b"}}}},
		&mp.SectionPage{Section: events, Body: &mp.SectionPage_Delta{Delta: &mp.Delta{From: wm(10, 1), To: wm(12, 5), CombatEvents: []*mp.CombatEventRow{down}}}},
	))
	rows := heldCombatPawns(t, f)
	if len(rows) != 1 || !rows["a"].GetDowned() {
		t.Fatalf("delta rows %v", rows)
	}
	held, ok := mirror.Get[string, *mp.CombatEventRow](f.mirror, mirrorScopeOf(combatEpoch()), string(facts.CombatEvents))
	if !ok || len(held.Rows) != 2 || held.Rows["12.4"].GetStop() != k.CombatEvent_COMBAT_EVENT_DOWNED {
		t.Fatalf("events %v", held.Rows)
	}
	if !proto.Equal(rows["a"].GetChanged(), held.Rows["12.4"].GetAt()) {
		t.Fatalf("downing row %v and event %v off one watermark", rows["a"].GetChanged(), held.Rows["12.4"].GetAt())
	}
	if _, ok := facts.Get[EntitySection[*mp.CombatPawn]](f.store, facts.CombatPawns); !ok {
		t.Fatal("combat pawns not filed")
	}
}

// The events table keeps the newest combatEventsHeld rows.
func TestTrimCombatEvents(t *testing.T) {
	rows := map[string]*mp.CombatEventRow{}
	for i := 0; i < combatEventsHeld+10; i++ {
		rows[fmt.Sprintf("%d.1", i)] = combatEvent(int64(i), 1, mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED)
	}
	trimCombatEvents(rows)
	if len(rows) != combatEventsHeld || rows["9.1"] != nil || rows["10.1"] == nil {
		t.Fatalf("trim kept %d rows", len(rows))
	}
}

// The poll loop asks both combat sections, the review poll neither.
func TestCombatSectionsRideTheLoop(t *testing.T) {
	f := newClockFacts(nil, facts.NewStore())
	_, loop := f.mirrorAsks(nil)
	_, review := f.mirrorAsks(map[facts.Section]bool{facts.Buildings: true})
	has := func(asks []*mp.SectionAsk, s mp.Section) bool {
		for _, a := range asks {
			if a.GetSection() == s {
				return true
			}
		}
		return false
	}
	if !has(loop, mp.Section_SECTION_COMBAT_PAWNS) || !has(loop, mp.Section_SECTION_COMBAT_EVENTS) || has(review, mp.Section_SECTION_COMBAT_PAWNS) {
		t.Fatalf("loop %v review %v", loop, review)
	}
}

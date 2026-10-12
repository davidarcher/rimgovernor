package policy

import (
	"reflect"
	"runtime"
	"testing"
)

// builtGroup is the index of the first group of role.
func builtGroup(t *testing.T, groups []RoomGroup, role RoomRole) int {
	t.Helper()
	for i, g := range groups {
		if g.Role == role && plannedRoleOr(g) != PlannedFreezer {
			return i
		}
	}
	t.Fatalf("no %s group", role)
	return -1
}

// plannedRoleOr is the planned role of g, "" when none.
func plannedRoleOr(g RoomGroup) PlannedRole {
	p, _ := PlannedRoleOfGroup(g)
	return p
}

// builtAt is the standing room of group g at r, with the id.
func builtAt(groups []RoomGroup, g int, r Rectangle, id int32) BuiltRegion {
	return BuiltRegion{Room: id, Role: groups[g].Role, Region: RectRegion(r)}
}

func builtDecision(t *testing.T, emb Embedding, group int) EmbedDecision {
	t.Helper()
	for _, d := range emb.Decisions {
		if d.Group == group && (d.Verdict == EmbedKeep || d.Verdict == EmbedMove) {
			return d
		}
	}
	t.Fatalf("no built decision for group %d: %+v", group, emb.Decisions)
	return EmbedDecision{}
}

func movedBlocks(emb Embedding) []EmbeddedBlock {
	var out []EmbeddedBlock
	for _, b := range emb.Blocks {
		if b.Replaces != 0 {
			out = append(out, b)
		}
	}
	return out
}

// A pinned standing room that no group carries is an obstacle: the kitchen
// block, which the free embedding puts on its ground, goes elsewhere (#3019).
func TestEmbedPinnedRoomWithNoGroupIsAnObstacle(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	k := builtGroup(t, in.Groups, RoomRoleKitchen)
	at, _ := free.Block(k)
	in.Standing = []BuiltRegion{{Room: 9, Planned: PlannedBedroom, Region: RectRegion(at), Pinned: true}}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	if len(emb.Held) != 1 || emb.Held[0].Room != 9 || emb.Held[0].Interior != at || emb.Held[0].Role != PlannedBedroom {
		t.Fatalf("held %+v, want room 9 at %+v", emb.Held, at)
	}
	if len(emb.Blocks) != len(in.Groups) {
		t.Fatalf("%d blocks for %d groups", len(emb.Blocks), len(in.Groups))
	}
	for _, b := range emb.Blocks {
		if collides(b.Interior, at) {
			t.Errorf("group %d block %+v touches the pinned room at %+v", b.Group, b.Interior, at)
		}
	}
}

// A built kitchen and storeroom stand where they are and the rest is placed
// around them: no churn, the same result at any thread count.
func TestEmbedBuiltRoomsTileAroundWithNoChurn(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	k, s := builtGroup(t, in.Groups, RoomRoleKitchen), builtGroup(t, in.Groups, RoomRoleWorkshop)
	kb, _ := free.Block(k)
	sb, _ := free.Block(s)
	in.Built = []BuiltRegion{builtAt(in.Groups, k, kb, 1), builtAt(in.Groups, s, sb, 2)}
	run := func(procs int) Embedding {
		defer runtime.GOMAXPROCS(runtime.GOMAXPROCS(procs))
		emb, err := Embed(in)
		if err != nil {
			t.Fatal(err)
		}
		return emb
	}
	emb := run(1)
	if !reflect.DeepEqual(emb, run(8)) {
		t.Fatal("the built embedding differs by thread count")
	}
	checkEmbedding(t, in, emb)
	if len(emb.Blocks) != len(in.Groups) || len(movedBlocks(emb)) != 0 || emb.Score.Switch != 0 {
		t.Fatalf("churn: %d blocks, moved %+v, switch %d", len(emb.Blocks), movedBlocks(emb), emb.Score.Switch)
	}
	for g, id := range map[int]int32{k: 1, s: 2} {
		got, _ := emb.Block(g)
		want := in.Built[id-1].Region.Bounds()
		if got != want {
			t.Errorf("group %d block %+v, built room stands at %+v", g, got, want)
		}
		if d := builtDecision(t, emb, g); d.Verdict != EmbedKeep || d.Room != id || d.Reason == "" {
			t.Errorf("group %d: %+v", g, d)
		}
	}
}

// A room that stands within the switching threshold of its best position stays.
func TestEmbedBuiltRoomBelowThresholdStays(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, _ := Embed(in)
	s := builtGroup(t, in.Groups, RoomRoleWorkshop)
	sb, _ := free.Block(s)
	// Two cells off the best spot: a few points of centre and edge, far under
	// ReplanGain + BuiltSwitch.
	sb.X += 2
	in.Built = []BuiltRegion{builtAt(in.Groups, s, sb, 7)}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	if d := builtDecision(t, emb, s); d.Verdict != EmbedKeep || len(movedBlocks(emb)) != 0 {
		t.Fatalf("a near-optimal room moved: %+v", emb.Decisions)
	}
}

// A score gain past switchThreshold moves exactly one room, however many
// stand on poor ground: one rebuild at a time, the larger margin first.
func TestEmbedBuiltScoreGainMovesExactlyOneRoom(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, _ := Embed(in)
	k, s := builtGroup(t, in.Groups, RoomRoleKitchen), builtGroup(t, in.Groups, RoomRoleWorkshop)
	kb, _ := free.Block(k)
	sb, _ := free.Block(s)
	corner := func(b Rectangle, x, z int32) Rectangle { b.X, b.Z = x, z; return b }
	sFar := corner(sb, 2, 2)
	in.Built = []BuiltRegion{builtAt(in.Groups, s, sFar, 5)}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	moved := movedBlocks(emb)
	if len(moved) != 1 || moved[0].Group != s || moved[0].Replaces != 5 || moved[0].Interior == sFar {
		t.Fatalf("storeroom did not move once: %+v", emb.Blocks)
	}
	if d := builtDecision(t, emb, s); d.Verdict != EmbedMove || d.Reason != ReasonEmbedBuiltMovedGain {
		t.Fatalf("decision %+v", d)
	}
	if want := -switchThreshold(PlannedRoom{Region: RectRegion(sFar)}); emb.Score.Switch != want {
		t.Errorf("switch cost %d, want %d", emb.Score.Switch, want)
	}
	// Both rooms on poor ground: one moves, the other waits.
	kFar := corner(kb, 138-kb.Width, 138-kb.Height)
	in.Built = append(in.Built, builtAt(in.Groups, k, kFar, 6))
	emb, err = Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	if moved := movedBlocks(emb); len(moved) != 1 {
		t.Fatalf("%d rooms moved at once: %+v", len(moved), moved)
	}
	reasons := map[string]int{}
	for _, g := range []int{k, s} {
		reasons[builtDecision(t, emb, g).Reason]++
	}
	if reasons[ReasonEmbedBuiltMovedGain] != 1 || reasons[ReasonEmbedBuiltDeferred] != 1 {
		t.Errorf("reasons %v", reasons)
	}
	defer runtime.GOMAXPROCS(runtime.GOMAXPROCS(1))
	again, _ := Embed(in)
	if !reflect.DeepEqual(emb, again) {
		t.Fatal("the relocation differs by thread count")
	}
}

// A room on rich soil is replaced where there is less, with no switching
// price: the hard tier decides, not the threshold.
func TestEmbedBuiltBadGroundIsReplaced(t *testing.T) {
	s := centreRichSurvey()
	in := embedInput(t, s, 6)
	st := builtGroup(t, in.Groups, RoomRoleWorkshop)
	home := Rectangle{X: 66, Z: 66, Width: 4, Height: 4}
	in.Built = []BuiltRegion{builtAt(in.Groups, st, home, 3)}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	if d := builtDecision(t, emb, st); d.Verdict != EmbedMove || d.Reason != ReasonEmbedBuiltBadGround || emb.Score.Switch != 0 {
		t.Fatalf("decision %+v, switch %d", d, emb.Score.Switch)
	}
	if b, _ := emb.Block(st); collides(b, home) || b.Width != home.Width || b.Height != home.Height {
		t.Errorf("successor %+v does not stand clear of %+v in the same shape", b, home)
	}
}

// Pinned rooms and a rebuild in flight hold every built room where it is.
func TestEmbedBuiltPinnedAndInFlightDoNotMove(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, _ := Embed(in)
	st := builtGroup(t, in.Groups, RoomRoleWorkshop)
	sb, _ := free.Block(st)
	sb.X, sb.Z = 2, 2
	for name, set := range map[string]func(*EmbedInput){
		ReasonEmbedBuiltPinned:   func(in *EmbedInput) { in.Built[0].Pinned = true },
		ReasonEmbedBuiltInFlight: func(in *EmbedInput) { in.InFlight = true },
	} {
		run := in
		run.Built = []BuiltRegion{builtAt(in.Groups, st, sb, 4)}
		set(&run)
		emb, err := Embed(run)
		if err != nil {
			t.Fatal(err)
		}
		if d := builtDecision(t, emb, st); d.Verdict != EmbedKeep || d.Reason != name || len(movedBlocks(emb)) != 0 {
			t.Errorf("%s: %+v", name, d)
		}
	}
}

// Retired ground not yet cleared takes no new block.
func TestEmbedRetiredGroundIsClosed(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, _ := Embed(in)
	b, _ := free.Block(builtGroup(t, in.Groups, RoomRoleKitchen))
	in.RetiredGround = []Region{RectRegion(pad(b, 1))}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	for _, blk := range emb.Blocks {
		if axisGap(blk.Interior.X, blk.Interior.Width, b.X-1, b.Width+2) < 0 && axisGap(blk.Interior.Z, blk.Interior.Height, b.Z-1, b.Height+2) < 0 {
			t.Errorf("block %+v stands on retired ground %+v", blk.Interior, b)
		}
	}
}

// The tiler keeps a standing room's id and region, and a moved room is a
// successor of the one it replaces.
func TestTileKeepsBuiltRoomsAndSitesSuccessors(t *testing.T) {
	in := embedInput(t, plainScoreSurvey(), 6)
	free, _ := Embed(in)
	k, s := builtGroup(t, in.Groups, RoomRoleKitchen), builtGroup(t, in.Groups, RoomRoleWorkshop)
	kb, _ := free.Block(k)
	sb, _ := free.Block(s)
	sFar := sb
	sFar.X, sFar.Z = 2, 2
	in.Built = []BuiltRegion{builtAt(in.Groups, k, kb, 11), builtAt(in.Groups, s, sFar, 12)}
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	res, err := TileEmbedding(TileInput{Groups: in.Groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: in.Survey, RoofDistance: testRoof})
	if err != nil {
		t.Fatal(err)
	}
	byID := map[int32]PlannedRoom{}
	for _, r := range res.Plan.AllRooms() {
		byID[r.ID] = r
	}
	if r, ok := byID[11]; !ok || r.Region.Bounds() != kb || res.Rooms[k] != 11 {
		t.Errorf("kitchen not kept as it stands: %+v", byID[11])
	}
	succ := byID[res.Rooms[s]]
	if succ.Replaces != 12 || succ.Region.Bounds() == sFar {
		t.Errorf("storeroom successor %+v", succ)
	}
	reasons := map[string]bool{}
	for _, d := range res.Decisions {
		reasons[d.Reason] = true
	}
	if !reasons[ReasonTileRoomBuilt] || !reasons[ReasonTileRoomMoved] {
		t.Errorf("tile decisions %v", reasons)
	}
}

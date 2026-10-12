package buildingruntime

import (
	"context"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

var perimeterBounds = policy.Bounds{Width: 200, Height: 200}

func perimeterSurvey(cell func(x, z int32) policy.SurveyCell) policy.MapSurvey {
	s := policy.MapSurvey{Bounds: perimeterBounds}
	for z := int32(0); z < perimeterBounds.Height; z++ {
		for x := int32(0); x < perimeterBounds.Width; x++ {
			c := cell(x, z)
			c.Cell = domain.Cell{X: x, Z: z}
			s.Cells = append(s.Cells, c)
		}
	}
	return s
}

// perimeterRecord is an anchored record on the plan's killbox, with one
// killbox tier.
func perimeterRecord(t *testing.T, plan policy.LayoutPlan) store.DefenseLayoutRecord {
	t.Helper()
	k, _, _, ok := policy.LayoutKillbox(plan, perimeterBounds)
	id, digest, found := policy.KillboxNode(plan)
	if !ok || !found {
		t.Fatal("no killbox")
	}
	return store.DefenseLayoutRecord{World: store.World{Colony: "c", Load: "l"}, Standard: "g", Episode: 1, Killbox: id, KillboxDigest: digest, Firing: []domain.Cell{k.Entry},
		Tiers: []store.DefenseTierRecord{{Name: policy.TierTrapCorridor, Buildings: []store.DefenseBuilding{{Definition: "TrapSpike", Cell: k.Entry, Rotation: domain.North, Stuff: "WoodLog"}}}}}
}

// perimeterCut is a cut over a plan.
func perimeterCut(plan policy.LayoutPlan, bridge string, transmitters []domain.Cell, spare float64) defenseCut {
	return defenseCut{plan: plan, bridge: bridge, transmitters: transmitters, spare: spare}
}

func tierBuildings(record store.DefenseLayoutRecord, keep func(store.DefenseTierRecord) bool) map[string]bool {
	out := map[string]bool{}
	for _, t := range record.Tiers {
		if keep(t) {
			for _, b := range t.Buildings {
				out[defenseBuildingKey(b)] = true
			}
		}
	}
	return out
}

// Marsh across the ring takes a wooden wall and pumps; once the ground has
// dried the replanned ring wants stone there, so the wooden walls are
// removed ahead of the new sections, the pumps left standing.

func TestDefenseRecutPerimeterOnDriedGround(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	survey := func(dried bool) policy.MapSurvey {
		return perimeterSurvey(func(x, z int32) policy.SurveyCell {
			if !dried && z%40 < 4 {
				return policy.SurveyCell{Walkable: true, Footing: policy.FootingLight, Bridgeable: true, Dries: true, Fertility: 1}
			}
			return policy.SurveyCell{Walkable: true, Fertility: 1}
		})
	}
	plan, ok := oldDerive(survey(false), 3, policy.TechTierCamp, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	record := perimeterRecord(t, plan)
	transmitters := []domain.Cell{{X: plan.Rooms[0].Region.Bounds().X, Z: plan.Rooms[0].Region.Bounds().Z}}
	if changed, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, transmitters, 1e6)); err != nil || !changed || record.PerimeterRevision != 0 || record.PerimeterKey == "" {
		t.Fatal("anchor", changed, err)
	}
	wood := tierBuildings(record, func(t store.DefenseTierRecord) bool { return true })
	pumps := 0
	for _, t := range record.Tiers {
		if strings.HasPrefix(string(t.Name), policy.TierPumpPrefix) {
			pumps++
		}
	}
	if pumps == 0 {
		t.Fatal("no pump tier")
	}
	if changed, _ := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, transmitters, 1e6)); changed {
		t.Fatal("an unchanged plan re-cut")
	}
	// No tombs: the plan above was derived with none, and a tomb room the
	// replan adds grows the core and moves the killbox.
	dried := policy.LayPerimeter(plan, plan, survey(true), record.PerimeterHold())
	if !policy.PerimeterChanged(plan, dried) {
		t.Fatal("dried ground kept the plan")
	}
	if changed, err := defenseRecutPerimeter(&record, perimeterCut(dried, policy.PerimeterBridge, transmitters, 1e6)); err != nil || !changed || record.PerimeterRevision != 1 {
		t.Fatal("re-cut", changed, err)
	}
	if record.Tiers[0].Name != policy.TierTrapCorridor {
		t.Fatal("the killbox tier moved", record.Tiers[0].Name)
	}
	removed := tierBuildings(record, func(t store.DefenseTierRecord) bool { return t.Remove })
	fresh := tierBuildings(record, func(t store.DefenseTierRecord) bool { return !t.Remove })
	woodRemoved := false
	for k := range removed {
		woodRemoved = woodRemoved || strings.HasSuffix(k, "/"+policy.PerimeterLightStuff)
	}
	if !woodRemoved {
		t.Fatal("no wooden wall removed")
	}
	freshCells := map[domain.Cell]bool{}
	for _, tier := range record.Tiers[1:] {
		for _, b := range tier.Buildings {
			if !tier.Remove {
				freshCells[b.Cell] = true
			}
		}
	}
	// A removal ahead of the new sections clears a cell they build on; the
	// rest waits behind them. The dropped pumps go too.
	seenFresh, pumpRemoved := false, false
	for _, tier := range record.Tiers[1:] {
		if !strings.HasPrefix(string(tier.Name), policy.TierPerimeterPrefix+"r1-") {
			t.Fatal("tier not renamed", tier.Name)
		}
		seenFresh = seenFresh || !tier.Remove
		for _, b := range tier.Buildings {
			if tier.Remove && freshCells[b.Cell] == seenFresh {
				t.Fatal("removal on the wrong side of the new sections", tier.Name, b)
			}
			pumpRemoved = pumpRemoved || tier.Remove && b.Definition == defenseMoisturePump
			if tier.Remove && (fresh[defenseBuildingKey(b)] || !wood[defenseBuildingKey(b)]) {
				t.Fatal("removes", b)
			}
			// The killbox fence is wood on any ground; it is no wall.
			if !tier.Remove && b.Stuff == policy.PerimeterLightStuff && b.Definition != defenseDefinitions.Fence {
				t.Fatal("wood on dried ground", b)
			}
		}
	}
	if !pumpRemoved {
		t.Fatal("the dropped pump stays")
	}
	// The record holds the old ring's nodes (retired in the plan) and the new
	// ring's together. Stored, it keeps the node ids and drops the resolved
	// buildings; loaded and hydrated, the
	// new tiers stand again; the old stretch demolished, its removals leave and
	// the new tiers stay.
	ctx := context.Background()
	db, err := store.Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	record.World = store.World{Colony: "c", Load: "l"}
	if err = db.SaveDefenseLayout(ctx, record); err != nil {
		t.Fatal(err)
	}
	loaded, ok, err := db.LoadDefenseLayout(ctx, record.World)
	if err != nil || !ok {
		t.Fatal("load", ok, err)
	}
	for _, tier := range loaded.Tiers {
		if tier.NodeBacked() && len(tier.Buildings) > 0 {
			t.Fatal("a node tier stored its buildings", tier.Name)
		}
		if tier.Remove && len(tier.Nodes) == 0 {
			t.Fatal("a removal tier lost its nodes", tier.Name)
		}
	}
	if err = defenseHydrate(&loaded, perimeterCut(dried, policy.PerimeterBridge, transmitters, 1e6)); err != nil {
		t.Fatal(err)
	}
	if got := tierBuildings(loaded, func(t store.DefenseTierRecord) bool { return !t.Remove }); len(got) != len(fresh) {
		t.Fatal("hydrated", len(got), "buildings, cut", len(fresh))
	}
	demolished := &defenseCensus{edifice: map[domain.Cell]string{}, terrain: map[domain.Cell]string{}}
	nodeTiers := 0
	for _, tier := range loaded.Tiers {
		if tier.NodeBacked() && !tier.Remove {
			nodeTiers++
		}
	}
	if !defenseTierCensus(&loaded, demolished) {
		t.Fatal("the demolished removals stayed")
	}
	for _, tier := range loaded.Tiers {
		if tier.Remove {
			t.Fatal("removal left", tier.Name)
		}
	}
	after := 0
	for _, tier := range loaded.Tiers {
		if tier.NodeBacked() {
			after++
		}
	}
	if after != nodeTiers {
		t.Fatal("node tiers lost with the removals", nodeTiers, after)
	}
	// Nothing holds the old ring now, so the next replan drops its nodes.
	if hold := loaded.PerimeterHold(); !policy.PerimeterRetirable(dried, hold.Held) {
		t.Fatal("the old ring was not retired in the plan")
	}
}

// A new pump is planned only while spare watts cover its 150 W; one the
// record already holds costs nothing more.
func TestDefenseRecutPerimeterPumpPower(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	plan, ok := oldDerive(perimeterSurvey(func(x, z int32) policy.SurveyCell {
		if z%40 < 4 {
			return policy.SurveyCell{Walkable: true, Footing: policy.FootingLight, Bridgeable: true, Dries: true, Fertility: 1}
		}
		return policy.SurveyCell{Walkable: true, Fertility: 1}
	}), 3, policy.TechTierCamp, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	transmitters := []domain.Cell{{X: plan.Rooms[0].Region.Bounds().X, Z: plan.Rooms[0].Region.Bounds().Z}}
	pumps := func(r store.DefenseLayoutRecord) (n int) {
		for k := range tierBuildings(r, func(t store.DefenseTierRecord) bool { return !t.Remove }) {
			if strings.HasPrefix(k, defenseMoisturePump+"@") {
				n++
			}
		}
		return n
	}
	for _, c := range []struct {
		spare float64
		want  func(int) bool
	}{{0, func(n int) bool { return n == 0 }}, {149, func(n int) bool { return n == 0 }}, {150, func(n int) bool { return n == 1 }}, {1e6, func(n int) bool { return n >= 1 }}} {
		record := perimeterRecord(t, plan)
		if _, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, transmitters, c.spare)); err != nil {
			t.Fatal(err)
		}
		if n := pumps(record); !c.want(n) {
			t.Fatal("spare", c.spare, "pumps", n)
		}
	}
	record := perimeterRecord(t, plan)
	if _, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, transmitters, 1e6)); err != nil {
		t.Fatal(err)
	}
	built := pumps(record)
	if changed, _ := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, transmitters, 0)); changed || pumps(record) != built {
		t.Fatal("standing pumps re-priced", changed, pumps(record), built)
	}
}

// A replan that moves the killbox opening un-anchors the record, so the
// layout is proposed afresh on the new one.
func TestDefenseRecutPerimeterMovedKillbox(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	plan, ok := oldDerive(perimeterSurvey(func(x, z int32) policy.SurveyCell { return policy.SurveyCell{Walkable: true, Fertility: 1} }), 3, policy.TechTierCamp, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	record := perimeterRecord(t, plan)
	if _, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, nil, 0)); err != nil {
		t.Fatal(err)
	}
	record.KillboxDigest = "moved"
	if changed, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, nil, 0)); err != nil || !changed || record.Killbox != "" {
		t.Fatal("moved killbox kept the anchor", changed, err)
	}
}

// Heavy bridges researched: the wooden wall on each plain bridge is
// deconstructed, then the bridge lifted, then the heavy bridge and its stone
// wall laid.
func TestDefenseRecutPerimeterHeavyBridges(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	plan, ok := oldDerive(perimeterSurvey(func(x, z int32) policy.SurveyCell {
		if z%40 < 4 {
			return policy.SurveyCell{Walkable: true, Footing: policy.FootingNone, Bridgeable: true}
		}
		return policy.SurveyCell{Walkable: true, Fertility: 1}
	}), 3, policy.TechTierCamp, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	record := perimeterRecord(t, plan)
	if _, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, nil, 0)); err != nil {
		t.Fatal(err)
	}
	bridges := tierBuildings(record, func(t store.DefenseTierRecord) bool { return true })
	changed, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterHeavyBridge, nil, 0))
	if err != nil || !changed {
		t.Fatal("heavy bridges re-cut nothing", err)
	}
	stage := 0 // 0 deconstruct, 1 lift, 2 build
	lifted, walls := 0, 0
	for _, tier := range record.Tiers[1:] {
		next := 2
		if tier.Remove && strings.Contains(string(tier.Name), "-x") {
			next = 0
		} else if tier.Remove {
			next = 1
		}
		if next < stage {
			t.Fatal("out of order", tier.Name)
		}
		stage = next
		for _, b := range tier.Buildings {
			switch {
			case stage == 0 && (b.Stuff != policy.PerimeterLightStuff || !bridges[defenseBuildingKey(b)]):
				t.Fatal("deconstructs", b)
			case stage == 1 && b.Definition != policy.PerimeterBridge:
				t.Fatal("lifts", b)
			case stage == 2 && (b.Definition == policy.PerimeterBridge || b.Stuff == policy.PerimeterLightStuff && b.Definition != defenseDefinitions.Fence):
				t.Fatal("builds", b)
			}
			if stage == 0 {
				walls++
			}
			if stage == 1 {
				lifted++
			}
		}
	}
	if lifted == 0 || walls < lifted {
		t.Fatal("lifted", lifted, "walls", walls)
	}
}

// A removal tier is done, and leaves the record, once none of its targets
// stands; each standing target is ordered by its kind, a designated one is
// work in progress and an unidentified one is left out.
func TestDefenseRemovalTierOrders(t *testing.T) {
	t.Parallel()
	wall, door, bridged, lost := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 2, Z: 1}, domain.Cell{X: 3, Z: 1}, domain.Cell{X: 4, Z: 1}
	tier := store.DefenseTierRecord{Name: "perimeter-r1-x00", Remove: true, Buildings: []store.DefenseBuilding{
		{Definition: "Wall", Cell: wall, Stuff: "WoodLog"}, {Definition: "Door", Cell: door, Stuff: "WoodLog"},
		{Definition: policy.PerimeterBridge, Cell: bridged}, {Definition: "Wall", Cell: lost, Stuff: "WoodLog"}}}
	census := &defenseCensus{edifice: map[domain.Cell]string{wall: "Wall", door: "Door", lost: "Wall"}, terrain: map[domain.Cell]string{bridged: policy.PerimeterBridge},
		cover: map[domain.Cell]*bridge.DefenseCover{
			wall: {ThingID: "Wall1", DefName: "Wall", Kind: o.CoverKind_COVER_KIND_BUILDING},
			door: {ThingID: "Door1", DefName: "Door", Kind: o.CoverKind_COVER_KIND_BUILDING, Designated: true}},
		unbridging: map[domain.Cell]bool{}}
	actions, pending, missing, err := defenseRemovalActions("plan", tier, census)
	if err != nil || !pending || len(missing) != 1 || !missing[lost] || len(actions) != 2 {
		t.Fatal(len(actions), pending, missing, err)
	}
	if c, ok := actions[0].CoverClearance(); !ok || c.Thing() != "Wall1" || c.Designation() != domain.CoverClearanceDeconstruct {
		t.Fatal("wall order", actions[0].Kind())
	}
	if f, ok := actions[1].FoundationRemoval(); !ok || f.Cell() != bridged || f.Definition() != policy.PerimeterBridge {
		t.Fatal("bridge order", actions[1].Kind())
	}
	census.unbridging[bridged] = true
	if actions, _, _, _ = defenseRemovalActions("plan", tier, census); len(actions) != 1 {
		t.Fatal("a designated bridge re-ordered")
	}
	record := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{{Name: policy.TierTrapCorridor}, tier}}
	if defenseTierCensus(&record, census) || len(record.Tiers) != 2 {
		t.Fatal("a standing removal left")
	}
	gone := &defenseCensus{edifice: map[domain.Cell]string{wall: "Wall", door: "Door"}, terrain: map[domain.Cell]string{bridged: "Marsh"}}
	gone.edifice[wall], gone.edifice[door] = "", ""
	if !defenseTierCensus(&record, gone) || len(record.Tiers) != 1 {
		t.Fatal("a finished removal stayed", len(record.Tiers))
	}
}

// The ring replaced end to end (#2924, #2237): the old ring stands and defends
// while the replacement is unbuilt; the replacement closes; only then does the
// old one go, and what the new ring wants is never taken down.
func TestDefenseRingReplaceLifecycle(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	survey := func(dried bool) policy.MapSurvey {
		return perimeterSurvey(func(x, z int32) policy.SurveyCell {
			if !dried && z%40 < 4 {
				return policy.SurveyCell{Walkable: true, Footing: policy.FootingLight, Bridgeable: true, Dries: true, Fertility: 1}
			}
			return policy.SurveyCell{Walkable: true, Fertility: 1}
		})
	}
	plan, ok := oldDerive(survey(false), 3, policy.TechTierCamp, 30).Value()
	if !ok {
		t.Fatal("no plan")
	}
	record := perimeterRecord(t, plan)
	transmitters := []domain.Cell{{X: plan.Rooms[0].Region.Bounds().X, Z: plan.Rooms[0].Region.Bounds().Z}}
	if _, err := defenseRecutPerimeter(&record, perimeterCut(plan, policy.PerimeterBridge, transmitters, 1e6)); err != nil {
		t.Fatal(err)
	}
	standing := func(keep func(store.DefenseTierRecord) bool) *defenseCensus {
		c := &defenseCensus{edifice: map[domain.Cell]string{}, terrain: map[domain.Cell]string{}, conduits: map[domain.Cell]bool{}}
		for _, tier := range record.Tiers {
			if !keep(tier) {
				continue
			}
			for _, b := range tier.Buildings {
				switch {
				case b.Definition == defenseConduitDefinition:
					c.conduits[b.Cell] = true
				case defenseTerrain(b.Definition):
					c.terrain[b.Cell] = b.Definition
				default:
					c.edifice[b.Cell] = b.Definition
				}
			}
		}
		return c
	}
	// The old ring is built and defends.
	oldRing := standing(func(store.DefenseTierRecord) bool { return true })
	defenseTierCensus(&record, oldRing)
	oldKeys := tierBuildings(record, func(tier store.DefenseTierRecord) bool { return policy.IsPerimeterTier(tier.Name) })
	if len(oldKeys) == 0 {
		t.Fatal("no old perimeter")
	}
	// The ground dries: the replan stands a replacement beside the old ring.
	dried := policy.LayPerimeter(plan, plan, survey(true), record.PerimeterHold())
	if !policy.PerimeterChanged(plan, dried) {
		t.Fatal("dried ground kept the plan")
	}
	ring, ok := dried.Ring()
	if !ok || len(ring.ReplacesSites) == 0 || ring.Reason != policy.ReasonRingReplaced {
		t.Fatal("no replacement ring", ring.Node, ring.ReplacesSites, ring.Reason)
	}
	for _, id := range ring.ReplacesSites {
		if site, ok := dried.Site(id); !ok || site.Node == ring.Node {
			t.Fatal("the replaced node is not kept", id)
		}
	}
	if changed, err := defenseRecutPerimeter(&record, perimeterCut(dried, policy.PerimeterBridge, transmitters, 1e6)); err != nil || !changed {
		t.Fatal("re-cut", changed, err)
	}
	// Another replan while the old ring is held stands no second replacement:
	// one rebuild at a time.
	if again := policy.LayPerimeter(dried, dried, survey(true), record.PerimeterHold()); policy.PerimeterChanged(dried, again) {
		t.Fatal("a second replacement began over the first")
	}
	// The replacement is unbuilt, the old ring stands: no removal is done and
	// no new tier reads built.
	defenseTierCensus(&record, oldRing)
	removals := 0
	for _, tier := range record.Tiers {
		if tier.Remove {
			removals++
		}
		if tier.Remove || !tier.Built {
			continue
		}
		for _, b := range tier.Buildings {
			if !oldRing.standing(b.Definition, b.Cell) {
				t.Fatal("an unbuilt replacement reads built", tier.Name, b)
			}
		}
	}
	if removals == 0 {
		t.Fatal("no removal queued for the old ring")
	}
	// Nothing is stranded: every old building is wanted by the new ring or
	// removed by a tier.
	wanted := tierBuildings(record, func(tier store.DefenseTierRecord) bool { return !tier.Remove })
	removed := tierBuildings(record, func(tier store.DefenseTierRecord) bool { return tier.Remove })
	for k := range oldKeys {
		if !wanted[k] && !removed[k] {
			t.Fatal("stranded", k)
		}
	}
	// The walls on cells the replacement builds on come down first, the rest of
	// the old ring still standing.
	seenFresh := false
	record.Tiers = slices.DeleteFunc(record.Tiers, func(tier store.DefenseTierRecord) bool {
		seenFresh = seenFresh || (!tier.Remove && policy.IsPerimeterTier(tier.Name))
		if tier.Remove && tier.Before && seenFresh {
			t.Fatal("a removal ahead of the new ring queued behind it", tier.Name)
		}
		return tier.Remove && tier.Before
	})
	// The replacement closes and the old stretch comes down.
	closed := standing(func(tier store.DefenseTierRecord) bool { return !tier.Remove })
	if !defenseTierCensus(&record, closed) {
		t.Fatal("the closed ring changed nothing")
	}
	for _, tier := range record.Tiers {
		if tier.Remove {
			t.Fatal("removal left", tier.Name, len(tier.Buildings), tier.Buildings[0], closed.edifice[tier.Buildings[0].Cell])
		}
		if len(tier.Buildings) > 0 && !tier.Built {
			t.Fatal("an unbuilt tier after the census", tier.Name)
		}
	}
	// Nothing holds the old nodes now; the next replan drops them and the live
	// ring is the replacement.
	hold := record.PerimeterHold()
	settled := policy.LayPerimeter(dried, dried, survey(true), hold)
	if policy.PerimeterRetirable(settled, hold.Held) {
		t.Fatal("the retired nodes stayed")
	}
	if live, ok := settled.Ring(); !ok || live.Node != ring.Node {
		t.Fatal("the live ring changed", live.Node, ring.Node)
	}
}

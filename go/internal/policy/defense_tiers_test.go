package policy

import (
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tierFixture is an OpeningPlan facing north with an open census over the
// killbox's region: every cell walkable, reachable from the edge and bare.
func tierFixture(t *testing.T) (LayoutPlan, DefenseRequest) {
	t.Helper()
	plan := OpeningPlan(domain.North)
	bounds := Bounds{Width: 250, Height: 250}
	_, region, _, ok := LayoutKillbox(plan, bounds)
	if !ok {
		t.Fatal("fixture plan has no killbox")
	}
	census := DefenseRequest{
		Bounds:      bounds,
		Definitions: DefenseDefinitions{Sandbag: "Sandbags", Wall: "Wall", WallStuff: "BlocksGranite", Fence: "Fence", FenceStuff: "WoodLog", Trap: "TrapSpike", TrapStuff: "WoodLog", Door: "Door", DoorStuff: "WoodLog", Floor: "WoodPlankFloor"},
		MinRange:    domain.Known(25.9),
		Defenders:   3,
		UnitCosts: map[string][]Amount{
			"Sandbags": {{Resource: "Cloth", Count: 5}}, "Wall": {{Resource: "BlocksGranite", Count: 5}},
			"Fence": {{Resource: "WoodLog", Count: 2}}, "TrapSpike": {{Resource: "WoodLog", Count: 45}}, "Door": {{Resource: "WoodLog", Count: 25}},
			"WoodPlankFloor": {{Resource: "WoodLog", Count: 3}},
		},
	}
	for x := region.X; x < region.X+region.Width; x++ {
		for z := region.Z; z < region.Z+region.Height; z++ {
			census.Cells = append(census.Cells, DefenseCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Passable: domain.Known(true),
				PlayerOwned: domain.Known(false), NaturalRock: domain.Known(false), EdgeReachable: domain.Known(true), Door: domain.Known(false), CoverFill: domain.Known(0.0)})
		}
	}
	return plan, census
}

func tierNode(nodes []PlannedSite, kind NodeKind) (PlannedSite, bool) {
	for _, n := range nodes {
		if n.Kind == kind {
			return n, true
		}
	}
	return PlannedSite{}, false
}

// The killbox is cut into its tiers: each is a node under the killbox node,
// inside the killbox, carrying its placements, and the nodes draw in the
// overlay.
func TestPlanDefenseTiersCutsTheKillboxIntoNodes(t *testing.T) {
	plan, census := tierFixture(t)
	got, err := PlanDefenseTiers(plan, census, DefenseTierPins{})
	if err != nil {
		t.Fatal(err)
	}
	killbox, _ := plan.Killbox()
	_, window, _, _ := LayoutKillbox(plan, census.Bounds)
	for _, kind := range []NodeKind{NodeDefCorridor, NodeDefLane, NodeDefFunnel, NodeDefFence, NodeDefTraps, NodeDefFiring, NodeDefShooter} {
		n, ok := tierNode(got.Nodes, kind)
		if !ok {
			t.Fatalf("no %s node in %v", kind, got.Nodes)
		}
		if n.Tier == nil || n.Tier.Under != killbox.Node || n.Tier.Pinned {
			t.Fatalf("%s facts %+v", kind, n.Tier)
		}
		for _, c := range n.Footprint.Cells() {
			if !contains(window, c) {
				t.Fatalf("%s cell %v outside the killbox window", kind, c)
			}
		}
	}
	lane, _ := tierNode(got.Nodes, NodeDefLane)
	if !slices.Equal(lane.Tier.Path, got.Layout.TrapLane) || len(lane.Tier.Path) == 0 {
		t.Fatal("the lane node lost the raiders' order")
	}
	firing, _ := tierNode(got.Nodes, NodeDefFiring)
	shooters, _ := tierNode(got.Nodes, NodeDefShooter)
	if len(firing.Tier.Firing) != 3 || len(firing.Tier.Places) != 3 || len(shooters.Tier.Places) != 3 {
		t.Fatalf("firing line %d positions, %d cover, %d floors", len(firing.Tier.Firing), len(firing.Tier.Places), len(shooters.Tier.Places))
	}
	traps, _ := tierNode(got.Nodes, NodeDefTraps)
	for _, p := range traps.Tier.Places {
		if p.Definition != "TrapSpike" {
			t.Fatalf("spike trap node holds %s", p.Definition)
		}
	}
	for _, d := range got.Decisions {
		if d.Verdict != EmbedPlace || d.Reason != ReasonTierSited {
			t.Fatalf("decision %+v", d)
		}
	}
	drawn := plan.WithDefenseTiers(got.Nodes)
	if !drawn.Valid() {
		t.Fatal("the plan with its tiers is not valid")
	}
	labels := ""
	for _, l := range drawn.Overlay(census.Bounds).Layers {
		labels += l.Label + ";"
	}
	for _, want := range []string{"corridor", "trap lane", "funnel", "kill fence", "spike traps", "firing line", "shooter floor"} {
		if !strings.Contains(labels, want) {
			t.Fatalf("overlay lacks %q: %s", want, labels)
		}
	}
	if again := drawn.WithDefenseTiers(got.Nodes); len(again.Sites) != len(drawn.Sites) {
		t.Fatal("tier nodes accumulate on a second cut")
	}
}

func TestPlanDefenseTiersIsDeterministic(t *testing.T) {
	plan, census := tierFixture(t)
	a, err := PlanDefenseTiers(plan, census, DefenseTierPins{})
	if err != nil {
		t.Fatal(err)
	}
	for range 3 {
		b, err := PlanDefenseTiers(plan, census, DefenseTierPins{})
		if err != nil || !reflect.DeepEqual(a.Nodes, b.Nodes) || !reflect.DeepEqual(a.Decisions, b.Decisions) {
			t.Fatal("a census and pins gave two node sets")
		}
	}
}

// builtCensus writes the built tier into the census as the buildings stand: the
// cell carries the edifice, no longer walkable.
func builtCensus(census DefenseRequest, tier DefenseTier) DefenseRequest {
	census.Cells = slices.Clone(census.Cells)
	at := map[domain.Cell]string{}
	for _, b := range tier.Buildings {
		at[b.Cell()] = b.Definition()
	}
	for i, c := range census.Cells {
		if def, ok := at[c.Cell]; ok {
			census.Cells[i].Edifice, census.Cells[i].PlayerOwned = def, domain.Known(true)
			census.Cells[i].Walkable, census.Cells[i].Passable, census.Cells[i].CoverFill = domain.Known(false), domain.Known(false), domain.Known(1.0)
		}
	}
	return census
}

// A built tier is pinned: against a census that holds its own walls the node
// stays where the tier stands and the tiers still to build are sited around it,
// where an unpinned siting finds the walls in the way.
func TestPlanDefenseTiersPinsBuiltTiers(t *testing.T) {
	plan, census := tierFixture(t)
	fresh, err := PlanDefenseTiers(plan, census, DefenseTierPins{})
	if err != nil {
		t.Fatal(err)
	}
	var funnel DefenseTier
	for _, tier := range fresh.Layout.Tiers {
		if tier.Name == TierFunnel {
			funnel = tier
		}
	}
	if len(funnel.Buildings) == 0 {
		t.Fatal("fixture funnel is empty")
	}
	built := builtCensus(census, funnel)
	unpinned, err := PlanDefenseTiers(plan, built, DefenseTierPins{})
	if err == nil {
		if n, ok := tierNode(unpinned.Nodes, NodeDefFunnel); ok && n.Footprint.Equal(mustTier(t, fresh, NodeDefFunnel).Footprint) {
			t.Fatal("an unpinned siting rebuilt the standing funnel")
		}
	}
	pinned, err := PlanDefenseTiers(plan, built, DefenseTierPins{Tiers: map[DefenseTierName]DefenseTier{TierFunnel: funnel}})
	if err != nil {
		t.Fatal(err)
	}
	want := mustTier(t, fresh, NodeDefFunnel)
	got := mustTier(t, pinned, NodeDefFunnel)
	if !got.Footprint.Equal(want.Footprint) || !got.Tier.Pinned {
		t.Fatalf("pinned funnel %+v, want %v", got.Tier, want.Footprint)
	}
	for _, kind := range []NodeKind{NodeDefCorridor, NodeDefLane, NodeDefTraps, NodeDefFiring, NodeDefShooter} {
		if a, b := mustTier(t, fresh, kind), mustTier(t, pinned, kind); !a.Footprint.Equal(b.Footprint) {
			t.Fatalf("%s moved when the funnel was pinned", kind)
		}
		if mustTier(t, pinned, kind).Tier.Pinned {
			t.Fatalf("%s pinned though it is not built", kind)
		}
	}
	var reasons []string
	for _, d := range pinned.Decisions {
		if strings.HasSuffix(d.Node, string(NodeDefFunnel)) {
			reasons = append(reasons, d.Reason)
		}
	}
	if !slices.Equal(reasons, []string{ReasonTierPinned}) {
		t.Fatal("funnel decisions", reasons)
	}
}

func mustTier(t *testing.T, tiers DefenseTiers, kind NodeKind) PlannedSite {
	t.Helper()
	n, ok := tierNode(tiers.Nodes, kind)
	if !ok {
		t.Fatalf("no %s node", kind)
	}
	return n
}

// When the census holds no corridor any more the built tiers still stand as
// nodes, and the refusal is a decision; with nothing built there are no nodes.
func TestPlanDefenseTiersKeepsBuiltTiersWhenTheLayoutFails(t *testing.T) {
	plan, census := tierFixture(t)
	fresh, err := PlanDefenseTiers(plan, census, DefenseTierPins{})
	if err != nil {
		t.Fatal(err)
	}
	var funnel DefenseTier
	for _, tier := range fresh.Layout.Tiers {
		if tier.Name == TierFunnel {
			funnel = tier
		}
	}
	census.Cells = slices.Clone(census.Cells)
	for i := range census.Cells {
		census.Cells[i].Walkable, census.Cells[i].Passable = domain.Known(false), domain.Known(false)
	}
	none, err := PlanDefenseTiers(plan, census, DefenseTierPins{})
	if err != nil || len(none.Nodes) != 0 || len(none.Decisions) != 1 || none.Decisions[0].Verdict != EmbedUnplaceable {
		t.Fatalf("no corridor, nothing built: %v %+v", err, none)
	}
	kept, err := PlanDefenseTiers(plan, census, DefenseTierPins{Tiers: map[DefenseTierName]DefenseTier{TierFunnel: funnel}})
	if err != nil || len(kept.Nodes) != 1 || kept.Nodes[0].Kind != NodeDefFunnel || !kept.Nodes[0].Tier.Pinned {
		t.Fatalf("built funnel: %v %+v", err, kept.Nodes)
	}
	if _, err := PlanDefenseTiers(LayoutPlan{}, census, DefenseTierPins{}); err == nil {
		t.Fatal("a plan without a killbox was cut")
	}
}

// A tier still to build gives up a cell a built tier holds.
func TestDisplaceBuiltDropsBuildingsOnBuiltCells(t *testing.T) {
	wall, _ := domain.NewBuilding("Wall", domain.Cell{X: 5, Z: 5}, domain.North, "")
	trap, _ := domain.NewBuilding("TrapSpike", domain.Cell{X: 5, Z: 5}, domain.North, "")
	other, _ := domain.NewBuilding("TrapSpike", domain.Cell{X: 6, Z: 5}, domain.North, "")
	layout := DefenseLayout{Tiers: []DefenseTier{{Name: TierFunnel, Buildings: []domain.Building{wall}}, {Name: TierTrapCorridor, Buildings: []domain.Building{trap, other}}}}
	lost := displaceBuilt(&layout, map[DefenseTierName]DefenseTier{TierFunnel: layout.Tiers[0]})
	if !slices.Equal(lost, []DefenseTierName{TierTrapCorridor}) || len(layout.Tiers[1].Buildings) != 1 || layout.Tiers[1].Buildings[0].Cell() != (domain.Cell{X: 6, Z: 5}) {
		t.Fatalf("lost %v, traps %v", lost, layout.Tiers[1].Buildings)
	}
}

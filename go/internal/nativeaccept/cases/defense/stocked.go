package defense

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func init() {
	cases.Register(cases.Case{
		Name:        "defense/layout-stocked",
		Scope:       "On the ready Core defense layout, tripling steel, components and silver crosses a native raid-point budget band; the routine projection equals DefaultThreatPointsNow and proposes more turret positions with unchanged armed colonists and firing line (#341, #563). Paused planning only.",
		Start:       cases.Save{Name: checkpointName, From: cases.CommittedSaves()},
		RequiredOps: []string{"test/defense_setup"},
		Quiet:       na.QuietRequired, Budget: 5 * time.Minute, Run: runStockedLayout,
	})
}

type stockedClock struct{}

func (stockedClock) Now() time.Time { return time.Now() }

func runStockedLayout(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	fixture := func(label, op string, args map[string]any) (map[string]any, error) {
		if args == nil {
			args = map[string]any{}
		}
		args["op"] = op
		out, err := h.Call(ctx, label, "test/defense_setup", args)
		if err != nil {
			return nil, err
		}
		if ok, _ := na.AsBool(out["success"]); !ok {
			return nil, fmt.Errorf("%s: fixture refused: %v", label, out)
		}
		return out, nil
	}
	// Restore the ordinary quiet marker even when an assertion fails.
	defer func() {
		cleanup, cancel := context.WithTimeout(context.WithoutCancel(ctx), 30*time.Second)
		defer cancel()
		_, _ = h.Call(cleanup, "restore-quiet", "test/defense_setup", map[string]any{"op": "quiet"})
	}()
	data, err := os.ReadFile(filepath.Join(cases.CommittedSaves(), checkpointName+".checkpoint.json"))
	if err != nil {
		return err
	}
	var cp layoutCheckpoint
	if err = json.Unmarshal(data, &cp); err != nil {
		return err
	}
	g := policy.DefenseGeometry{Entry: cp.Layout.Entry, Toward: cp.Layout.Toward, Approach: cp.Layout.TrapLane,
		Firing: slices.Clone(cp.Layout.Firing), Lanes: append(slices.Clone(cp.Layout.TrapLane), cp.Layout.SafeLane...)}
	for _, tier := range cp.Layout.Tiers {
		if tier.Name == policy.TierTurrets {
			continue
		}
		g.Reserved = append(g.Reserved, tier.Reserved...)
		for _, b := range tier.Buildings {
			g.Reserved = append(g.Reserved, b.Cell)
		}
	}
	anchor := turretGeneratorAnchor(cp.Layout)
	power, err := fixture("power", "power", map[string]any{"x": anchor.X, "z": anchor.Z})
	if err != nil {
		return err
	}
	report["power"] = power
	type comparison struct {
		points  float64
		armed   int64
		turrets []domain.Cell
		stock   map[policy.Resource]int64
		budget  int
		firing  []domain.Cell
		tick    domain.Tick
	}
	var before comparison
	for _, multiplier := range []int{1, 3} {
		label := fmt.Sprintf("stock-%d", multiplier)
		native, err := fixture(label, "scaling", map[string]any{"points": multiplier})
		if err != nil {
			return err
		}
		idReply, _, err := h.Client.Identity(ctx)
		if err != nil {
			return err
		}
		expected, err := observation.DecodeIdentity(idReply)
		if err != nil {
			return err
		}
		id := &c.Identity{ColonyId: proto.String(string(expected.Colony)), LoadToken: proto.String(string(expected.Load)), MapId: proto.Int32(int32(expected.Map))}
		reading, err := observation.ObserveRoutine(ctx, h.Client, stockedClock{}, expected, time.Minute, turretDefinition, "HiddenConduit")
		if err != nil {
			return err
		}
		p := reading.Projection
		threat, _ := na.AsMap(native["threat"])
		// Check the production RoutineFacts field, not just the wire threat block:
		// replacing #562's projection with Unknown must fail this case.
		fields := map[string]domain.Fact[float64]{
			"raidPoints": p.Facts.RaidPoints, "wealthItems": p.Threat.WealthItems,
			"wealthBuildings": p.Threat.WealthBuildings, "wealthPawns": p.Threat.WealthPawns,
			"wealthTotal": p.Threat.WealthTotal, "storytellerWealth": p.Threat.StorytellerWealth,
			"adaptationFactor": p.Threat.AdaptationFactor, "difficultyThreatScale": p.Threat.DifficultyThreatScale,
		}
		projected := map[string]float64{}
		for _, field := range threatFields {
			got, known := fields[field].Value()
			want, exists := threat[field]
			if !known || !exists || math.Abs(got-na.AsNumber(want)) > 1e-3*math.Max(1, math.Abs(na.AsNumber(want))) {
				return fmt.Errorf("%s: %s projected %v (known=%v), native %v", label, field, got, known, want)
			}
			projected[field] = got
		}
		armed, known := p.Facts.Armed.Value()
		if !known || armed < 1 || len(g.Firing) == 0 {
			return fmt.Errorf("missing armed roster or firing line: %v, %v", p.Facts.Armed, g.Firing)
		}
		request, err := stockedTurretRequest(p)
		if err != nil {
			return err
		}
		region := bridge.CellRect{Min: domain.Cell{X: p.Center.X - 22, Z: p.Center.Z - 22}, Max: domain.Cell{X: p.Center.X + 22, Z: p.Center.Z + 22}}
		site, _, err := h.Client.ReadDefenseSite(ctx, id, region)
		if err != nil {
			return err
		}
		request.Bounds, request.Home = p.Bounds, p.Center
		request.Region = policy.Rectangle{X: region.Min.X, Z: region.Min.Z, Width: 45, Height: 45}
		for _, cell := range site.Cells {
			row := policy.DefenseCell{Cell: cell.Cell}
			if !cell.Fogged {
				row.Walkable, row.Passable, row.Door = domain.Known(cell.Walkable), domain.Known(cell.Passable), domain.Known(cell.Door)
				row.NaturalRock = domain.Known(cell.NaturalRock)
				row.PlayerOwned, row.Edifice = domain.Known(cell.PlayerOwned), cell.EdificeDefName
			}
			request.Cells = append(request.Cells, row)
		}
		_, candidates, err := policy.DefenseTurrets(request, g)
		if err != nil {
			return err
		}
		var probe []domain.Cell
		for _, candidate := range candidates {
			probe = append(probe, candidate.Cell)
		}
		if len(probe) < 3 {
			return fmt.Errorf("fixture has only %d available turret candidates; need at least three", len(probe))
		}
		lines, _, err := h.Client.ReadLinesOfFire(ctx, id, probe, g.Approach)
		if err != nil {
			return err
		}
		for _, line := range lines.Lines {
			row := policy.DefenseLine{From: line.From, To: line.To}
			if line.Known {
				row.LineOfSight = domain.Known(line.LineOfSight)
			}
			request.Lines = append(request.Lines, row)
		}
		tier, verified, err := policy.DefenseTurrets(request, g)
		if err != nil {
			return err
		}
		var turrets []domain.Cell
		for _, b := range tier.Buildings {
			if b.Definition() == turretDefinition {
				turrets = append(turrets, b.Cell())
			}
		}
		stock, _ := p.Resources.Value()
		now := comparison{points: projected["raidPoints"], armed: armed, turrets: turrets, stock: stock, budget: request.Turret.Max, firing: slices.Clone(g.Firing), tick: expected.Tick}
		report[label] = map[string]any{"native": native, "projected": projected, "armed": armed, "firing": g.Firing, "turrets": turrets, "budget": now.budget, "gates": request.Turret, "stock": stock, "candidates": verified, "tier": tier}
		if multiplier == 1 {
			if now.points < 280 || now.points >= 300 || len(turrets) != 2 {
				return fmt.Errorf("baseline must propose two turrets below 300 points: points=%v turrets=%v", now.points, turrets)
			}
			before = now
			continue
		}
		for _, resource := range []policy.Resource{"Steel", "ComponentIndustrial", "Silver"} {
			if before.stock[resource] <= 0 || stock[resource] != 3*before.stock[resource] {
				return fmt.Errorf("%s stock did not triple: %d -> %d", resource, before.stock[resource], stock[resource])
			}
		}
		if now.tick != before.tick || armed != before.armed || !slices.Equal(now.firing, before.firing) {
			return fmt.Errorf("comparison changed tick, armed roster or firing line")
		}
		if now.points <= before.points || now.budget <= before.budget || len(turrets) <= len(before.turrets) {
			return fmt.Errorf("tripled stock did not increase turret proposal: points %v -> %v, budgets %d -> %d, turrets %v -> %v", before.points, now.points, before.budget, now.budget, before.turrets, turrets)
		}
	}
	return nil
}

// Only observed native gates feed the policy. The fixture has one power
// network; rejecting any other topology prevents borrowing disconnected watts.
func stockedTurretRequest(p observation.ColonyProjection) (policy.DefenseRequest, error) {
	r := policy.DefenseRequest{Definitions: policy.DefenseDefinitions{Sandbag: "Barricade", Wall: "Wall", Fence: "Fence", Trap: "TrapSpike", Door: "Door", DoorStuff: "WoodLog", Floor: "WoodPlankFloor"}, UnitCosts: map[string][]policy.Amount{}}
	r.Turret = policy.DefenseTurretRequest{Definition: turretDefinition, Conduit: "HiddenConduit", Stock: p.Resources, Max: policy.TurretBudget(p.Facts.RaidPoints)}
	for _, d := range p.Definitions {
		if d.Name != turretDefinition && d.Name != r.Turret.Conduit {
			continue
		}
		if costs, known := d.Costs.Value(); known {
			r.UnitCosts[d.Name] = costs
		}
		if d.Name == turretDefinition {
			r.Turret.Available, r.Turret.DrawW = d.Available, d.PowerW
			r.Turret.Stuff, _ = d.Stuff.Value()
		}
	}
	topology, known := p.PowerPlanning.Value()
	if !known || len(topology.Networks) != 1 {
		return r, fmt.Errorf("fixture requires one observed power network: %v", p.PowerPlanning)
	}
	net := topology.Networks[0]
	generation, gk := net.GenerationW.Value()
	consumption, ck := net.ConsumptionW.Value()
	if !gk || !ck || generation <= 0 {
		return r, fmt.Errorf("generator output unknown or unticked: %+v", net)
	}
	r.Turret.SpareW = domain.Known(generation - consumption)
	r.Turret.Transmitters = topology.Conduits
	draw, dk := r.Turret.DrawW.Value()
	if !dk || generation-consumption < 6*draw {
		return r, fmt.Errorf("power cannot support all six candidate turrets")
	}
	return r, nil
}

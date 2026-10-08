package defense

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	// upgradePoints is the raid points the fixture raises the threat to:
	// past the machining threshold (800), so the armory tier, capped by the
	// research the fixture also finishes, reaches machining and the turret
	// ladder the autocannon.
	upgradePoints = 1000
	// turretsTimeout bounds the mini turrets' build after the layout,
	// replaceTimeout the in-place replacement after the rise (a
	// deconstruct and a 2x2 build), gearRound one served round of the
	// armory's weapon bill and the wear, gearRounds how many.
	turretsTimeout = 30 * time.Minute
	replaceTimeout = 45 * time.Minute
	gearRound      = 10 * time.Minute
	gearRounds     = 3
)

// defense/tier-upgrade raises the threat tier under a built layout (#1204,
// #1210, #1211). On the baseline the fixture opens the turret tier's power
// gates and the perimeter layout is built with the storyteller's raid
// points on the quiet 35-point floor, so its turret tier holds mini
// turrets. The fixture then raises the difficulty threat scale until the
// raid points pass the machining threshold, finishes Smithing, Machining
// and HeavyTurrets, and stands a fuelled smithy with steel, plasteel and
// components. Under the served layout, armory and gear families the
// layout planner replaces a mini turret in place with a higher rung
// (natively standing on its anchor), and a colonist ends up wielding a
// weapon of a higher armory tier than any colonist held before the rise.
func init() {
	families := append(append([]routinefamily.Family{}, perimeterFamilies...), routinefamily.Armory, routinefamily.Gear)
	cases.Register(cases.Case{
		Name: "defense/tier-upgrade",
		Scope: "A threat-tier rise upgrades turrets and gear (#1204, #1210): after the perimeter layout's mini turrets are built at the 35-point floor, the " +
			"fixture raises raid points past 800 and finishes Smithing, Machining and HeavyTurrets beside a fuelled smithy; the layout planner replaces a mini " +
			"turret in place with a higher rung that then stands natively, and a colonist wields a higher armory-tier weapon than any colonist held before. Native: the deconstruct, build, craft and wear outcomes end to end.",
		Start: cases.Save{Name: sustained.BaselineSave}, Serve: &cases.ServeSpec{Families: families, Prefix: "defense-upgrade"},
		NoCheckpoint: true,
		Budget:       3 * time.Hour,
		Crew:         cases.Crew{Size: 3}, Reason: "the turret tier stands on the perimeter layout's killbox, which only the native campaign builds",
		Run: runUpgrade,
	})
}

func runUpgrade(ctx context.Context, s cases.Session) error {
	report, h, identity := s.Report(), s.Harness(), s.Identity()
	for _, want := range []string{"test/defense_setup", "test/guarded_construction_prepare", na.LabSpawnTool} {
		if !na.Contains(s.Names(), want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture DefenseFixture,GuardedConstructionFixture", want)
		}
	}
	world := store.World{Colony: domain.ColonyID(na.AsString(identity["colonyId"])), Load: domain.LoadID(na.AsString(identity["loadToken"])), Map: domain.MapID(na.AsNumber(identity["mapId"]))}
	fixture := func(label string, args map[string]any) (map[string]any, error) {
		out, err := h.Call(ctx, label, "test/defense_setup", args)
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(out["success"]); !success {
			return nil, fmt.Errorf("defense_setup %s refused: %#v", args["op"], out)
		}
		return out, nil
	}
	reopen := func() error {
		var err error
		if h, err = s.Reattach(ctx); err != nil {
			return err
		}
		_, err = h.Call(ctx, "pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false})
		return err
	}
	if _, err := fixture("stock", map[string]any{"op": "stock"}); err != nil {
		return err
	}
	siteX, siteZ, err := prepareSite(ctx, h, identity, report)
	if err != nil {
		return err
	}
	power, err := fixture("power", map[string]any{"op": "power", "x": siteX, "z": siteZ})
	if err != nil {
		return err
	}
	report["power"] = power

	svc, err := launchService(ctx, s, "layout", identity, report)
	if err != nil {
		return err
	}
	defer func() { svc.stop() }()
	if _, err = waitLayoutComplete(ctx, svc.store, world, svc.wait(layoutTimeout), report); err != nil {
		return fmt.Errorf("layout: %w", err)
	}
	minis, err := waitTurretTier(ctx, svc.store, world, svc.wait(turretsTimeout), report, "turrets_built", func(t store.DefenseTierRecord) bool {
		return t.Built && len(t.Buildings) > 0
	})
	if err != nil {
		return fmt.Errorf("mini turrets: %w", err)
	}
	for _, b := range minis.Buildings {
		if b.Definition != policy.TurretMini {
			return fmt.Errorf("turret tier at the 35-point floor holds %s, want only %s: %+v", b.Definition, policy.TurretMini, minis)
		}
	}
	svc.stop()

	if err := reopen(); err != nil {
		return err
	}
	before, err := fixture("inspect-before-rise", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_before_rise"] = before
	armory, err := fixture("armory", map[string]any{"op": "armory", "x": siteX, "z": siteZ, "points": upgradePoints})
	if err != nil {
		return err
	}
	report["armory"] = armory
	topBefore := topArmoryTier(before)

	svc, err = launchService(ctx, s, "upgrade", identity, report)
	if err != nil {
		return err
	}
	upgraded, err := waitTurretTier(ctx, svc.store, world, svc.wait(replaceTimeout), report, "turrets_upgraded", func(t store.DefenseTierRecord) bool {
		return t.Built && higherRung(t) != nil
	})
	if err != nil {
		return fmt.Errorf("turret replacement: %w", err)
	}
	rung := higherRung(upgraded)
	wasMini := false
	for _, b := range minis.Buildings {
		wasMini = wasMini || b.Cell == rung.Cell
	}
	if !wasMini {
		return fmt.Errorf("higher rung %s at %d,%d is not on a former mini turret's anchor %+v", rung.Definition, rung.Cell.X, rung.Cell.Z, minis.Buildings)
	}
	svc.stop()

	for round := 1; ; round++ {
		if err := reopen(); err != nil {
			return err
		}
		after, err := fixture(fmt.Sprintf("inspect-after-rise-%d", round), map[string]any{"op": "inspect"})
		if err != nil {
			return err
		}
		report["inspect_after_rise"] = after
		if round == 1 && !turretStanding(after, rung.Definition, int(rung.Cell.X), int(rung.Cell.Z)) {
			return fmt.Errorf("recorded %s at %d,%d is not standing natively: %v", rung.Definition, rung.Cell.X, rung.Cell.Z, after["turrets"])
		}
		if top := topArmoryTier(after); top > topBefore {
			report["gear"] = map[string]any{"before": topBefore.String(), "after": top.String(), "rounds": round}
			return nil
		}
		if round == gearRounds {
			return fmt.Errorf("no colonist wields a weapon above the %s tier held before the rise after %d rounds: %v", topBefore, gearRounds, after["colonists"])
		}
		svc, err = launchService(ctx, s, fmt.Sprintf("gear-%d", round), identity, report)
		if err != nil {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(gearRound):
		}
		if err := svc.Exited(); err != nil {
			return fmt.Errorf("gear service exited: %w", err)
		}
		svc.stop()
	}
}

// waitTurretTier polls the stored layout until its turret tier satisfies
// done, and returns that tier.
func waitTurretTier(ctx context.Context, s *store.Store, world store.World, w na.Wait, report na.Report, key string, done func(store.DefenseTierRecord) bool) (store.DefenseTierRecord, error) {
	var tier store.DefenseTierRecord
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		record, ok, err := s.LoadDefenseLayout(ctx, world)
		if err != nil || !ok {
			return "", false, err
		}
		var found bool
		if tier, _, found = record.Tier(policy.TierTurrets); found && done(tier) {
			data, _ := json.Marshal(tier)
			report[key] = json.RawMessage(data)
			return "", true, nil
		}
		data, _ := json.Marshal(record.Tiers)
		return string(data), false, nil
	})
	return tier, err
}

// higherRung is the tier's first turret above the mini turret, if any.
func higherRung(t store.DefenseTierRecord) *store.DefenseBuilding {
	for i, b := range t.Buildings {
		if policy.TurretRank(b.Definition) > policy.TurretRank(policy.TurretMini) {
			return &t.Buildings[i]
		}
	}
	return nil
}

func turretStanding(inspect map[string]any, def string, x, z int) bool {
	for _, raw := range na.AsSlice(inspect["turrets"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["def"]) == def && int(na.AsNumber(row["x"])) == x && int(na.AsNumber(row["z"])) == z {
			return true
		}
	}
	return false
}

// topArmoryTier is the highest armory tier among the colonists' primaries.
func topArmoryTier(inspect map[string]any) policy.ArmoryTier {
	top := policy.ArmoryTierUnknown
	for _, raw := range na.AsSlice(inspect["colonists"]) {
		row, _ := na.AsMap(raw)
		if tier, ok := policy.ArmoryWeaponTier(policy.Resource(na.AsString(row["primary"]))); ok && tier > top {
			top = tier
		}
	}
	return top
}

package waste

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// waste/incineration (#1817, epic #1640; #2197 moved it from waste/disposal
// onto MaintainIncineration): corpse disposal end to end on the tribal
// baseline colony. The fixture lays a rotten animal corpse, a rotten and a
// fresh stranger corpse and a worn apparel in the open, plus stone blocks for
// the walls. MaintainStockpiles creates the waste yard's one dump zone from
// the plan (a declared Sanitation store) and MaintainIncineration shells the planned incinerator in the waste yard (first window), whose
// declared Sanitation zone takes burnable waste at Preferred priority; the case
// fills the incinerator with worn apparel and a loose molotov and
// MaintainIncineration burns it and has the ash cleaned (second window).
const (
	disposalStage = "test/disposal_stage"
	disposalSeed  = "test/disposal_seed"
	disposalRead  = "test/disposal_read"

	disposalWindow = 25 * time.Minute
	// seeded worn apparel in the interior: with the two rotten corpses the
	// room reaches policy.BurnStoredCells.
	disposalSeeded = 4
)

func init() {
	cases.Register(cases.Case{
		Name: "waste/incineration",
		Scope: "Issues #1817 and #2197 on the tribal " + sustained.BaselineSave + " colony: rotten animal and rotten stranger corpses are burned in the incinerator " +
			"and a fresh stranger is never put there; the waste yard holds one Low dump zone over the yard interior outside the incinerator outline, " +
			"allowing corpses and apparel (the not-burnable special is refused) and unroofed; a full incinerator (6+ cells) is burned once by a molotov-equipped burner with a standby and its ash cleaned; " +
			"the enclosed unroofed incinerator interior is its own room (cell.GetRoom) walled and doored with non-flammable edifices, " +
			"and its stockpile zone is the declared Preferred store over the whole interior. " +
			"A native end-to-end signal (vanilla fire, rot, rooms and hauling); no Go snapshot covers it.",
		Start:       cases.Fixture{Op: disposalStage, On: cases.Save{Name: sustained.BaselineSave}},
		RequiredOps: []string{disposalStage, disposalSeed, disposalRead},
		Keep:        []string{string(na.NeedFood)},
		Serve: &cases.ServeSpec{
			Families:      []routinefamily.Family{routinefamily.Incineration, routinefamily.Stockpiles, routinefamily.Flooring, routinefamily.Equip, routinefamily.Defense, routinefamily.Fire, routinefamily.Clean, routinefamily.Work, routinefamily.Supply},
			NativeTimeout: 30 * time.Second, Prefix: "waste-incineration",
		},
		Budget: 2*disposalWindow + 10*time.Minute,
		Crew:   cases.Crew{Size: 3}, Reason: "two serve windows: the incinerator is shelled by colonists, then burned and cleaned",
		Run: disposal,
	})
}

func disposal(ctx context.Context, s cases.Session) error {
	report := s.Report()
	prepared := s.Prepared()
	report["fixture"] = prepared
	staged := map[string]string{}
	for _, key := range []string{"animal", "rottenStranger", "freshStranger", "worn"} {
		if staged[key] = na.AsString(prepared[key]); staged[key] == "" {
			return fmt.Errorf("prepare: no %s id: %#v", key, prepared)
		}
	}
	animalDef := na.AsString(prepared["animalDef"])
	ids := []string{staged["animal"], staged["rottenStranger"], staged["freshStranger"], staged["worn"]}

	// Window one: the layout plan reserves the incinerator and
	// MaintainIncineration shells it.
	var room, yard policy.PlannedRoom
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: disposalWindow, Concern: policy.MaintainIncineration, Until: func(sample map[string]any) bool { return methodCompleted(sample, "incinerator-shell-") }},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			var err error
			if room, yard, err = plannedWasteYard(ctx, s); err != nil {
				return err
			}
			report["incinerator"] = fmt.Sprintf("%+v", room)
			if err := checkDump(ctx, s, h, report, animalDef, yard.Interior, room.Interior); err != nil {
				return err
			}
			read, err := readDisposal(ctx, h, ids, room)
			if err != nil {
				return err
			}
			if err := checkIncineratorZone(ctx, s, h, report, room.Interior); err != nil {
				return err
			}
			return checkFreshStranger(read, staged["freshStranger"], report, "shelled")
		},
	})
	if err != nil {
		return fmt.Errorf("incinerator window: %w", err)
	}

	// Window two: a full room with a molotov on the ground and a standby.
	var seeded []string
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: disposalWindow, Concern: policy.MaintainIncineration, Until: func(sample map[string]any) bool { return methodCompleted(sample, "burn-ash-") }},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			cells := policy.RectangleCells(room.Interior)
			if len(cells) < disposalSeeded {
				return fmt.Errorf("incinerator interior %+v holds fewer than %d cells", room.Interior, disposalSeeded)
			}
			var spots []string
			for _, c := range cells[:disposalSeeded] {
				spots = append(spots, fmt.Sprintf("%d,%d", c.X, c.Z))
			}
			reply, err := h.Call(ctx, "disposal-seed", disposalSeed, map[string]any{"cells": strings.Join(spots, ";"), "molotovs": 1})
			if err != nil {
				return err
			}
			for _, raw := range na.AsSlice(reply["ids"]) {
				seeded = append(seeded, na.AsString(raw))
			}
			if len(seeded) != disposalSeeded+1 {
				return fmt.Errorf("disposal seed: want %d things, got %#v", disposalSeeded+1, reply)
			}
			report["seeded"] = seeded
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			read, err := readDisposal(ctx, h, append(append([]string{}, ids...), seeded[:disposalSeeded]...), room)
			if err != nil {
				return err
			}
			report["disposal_read"] = read
			return checkBurned(ctx, s, read, staged, seeded[:disposalSeeded], room, report)
		},
	})
	if err != nil {
		return fmt.Errorf("burn window: %w", err)
	}
	return nil
}

// methodCompleted reports a plan of the sampled goal, active or retired,
// whose method has the prefix and whose actions all completed.
func methodCompleted(sample map[string]any, prefix string) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		method, _ := plan["method"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if strings.HasPrefix(method, prefix) && actions > 0 && stages["completed"] == actions {
			return true
		}
	}
	return false
}

// plannedWasteYard is the layout plan's incinerator room and the waste yard
// that holds it.
func plannedWasteYard(ctx context.Context, s cases.Session) (policy.PlannedRoom, policy.PlannedRoom, error) {
	journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
	if err != nil {
		return policy.PlannedRoom{}, policy.PlannedRoom{}, fmt.Errorf("reopen journal: %w", err)
	}
	defer journal.Close()
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		return policy.PlannedRoom{}, policy.PlannedRoom{}, fmt.Errorf("load rounds: %w", err)
	}
	record, ok, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick)
	if err != nil || !ok {
		return policy.PlannedRoom{}, policy.PlannedRoom{}, fmt.Errorf("no layout plan recorded by tick %d: %v", review.Tick, err)
	}
	rooms, yards := record.Plan.IncineratorRooms(), record.Plan.WasteYardRooms()
	if len(rooms) != 1 || len(yards) != 1 {
		return policy.PlannedRoom{}, policy.PlannedRoom{}, fmt.Errorf("the layout plan holds %d incinerators and %d waste yards, want one each", len(rooms), len(yards))
	}
	return rooms[0], yards[0], nil
}

// checkDump proves the one dump zone: a Low stockpile over exactly the yard
// interior outside the incinerator's 5x5 outline, unroofed, allowing humanlike
// and animal corpses and apparel (everything but the not-burnable special),
// and the only Low zone outside the incinerator.
func checkDump(ctx context.Context, s cases.Session, h *na.Harness, report na.Report, animalDef string, yard, incinerator policy.Rectangle) error {
	const humanCorpse, pants = "Corpse_Human", "Apparel_Pants"
	reply, err := h.Wire(ctx, "dump-zones", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}, "includeFilter": true})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	grid, err := h.MapCells(ctx, "dump-cells", s.Identity())
	if err != nil {
		return err
	}
	roofed := map[domain.Cell]bool{}
	for _, c := range grid.Cells() {
		if r, known := c.Roofed.Value(); known {
			roofed[c.Cell] = r
		}
	}
	zoneCells := na.ZoneCells(grid)
	want := map[domain.Cell]bool{}
	outline := map[domain.Cell]bool{}
	for _, c := range policy.RectangleCells(policy.Rectangle{X: incinerator.X - 1, Z: incinerator.Z - 1, Width: incinerator.Width + 2, Height: incinerator.Height + 2}) {
		outline[c] = true
	}
	for _, c := range policy.RectangleCells(yard) {
		if !outline[c] {
			want[c] = true
		}
	}
	inside := map[domain.Cell]bool{}
	for _, c := range policy.RectangleCells(incinerator) {
		inside[c] = true
	}
	var dumps []string
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if !strings.EqualFold(na.AsString(row["type"]), "stockpile") || !strings.Contains(strings.ToLower(na.AsString(row["priority"])), "low") {
			continue
		}
		cells := zoneCells[na.AsString(row["id"])]
		if len(cells) == 0 || inside[cells[0]] {
			continue
		}
		filter, _ := na.AsMap(row["filter"])
		allowed := map[string]bool{}
		for _, d := range na.AsSlice(filter["allowedDefNames"]) {
			allowed[na.AsString(d)] = true
		}
		dumps = append(dumps, fmt.Sprintf("%v cells=%d human=%v animal=%v apparel=%v", row["id"], len(cells), allowed[humanCorpse], allowed[animalDef], allowed[pants]))
		if len(cells) != len(want) {
			return fmt.Errorf("dump zone %v holds %d cells, want the %d of the yard outside the incinerator", row["id"], len(cells), len(want))
		}
		for _, c := range cells {
			if !want[c] {
				return fmt.Errorf("dump zone %v has cell %v outside the yard interior or inside the incinerator outline", row["id"], c)
			}
			if roofed[c] {
				return fmt.Errorf("dump zone %v has a roofed cell %v", row["id"], c)
			}
		}
		if !allowed[humanCorpse] || !allowed[animalDef] || !allowed[pants] {
			return fmt.Errorf("dump zone %v refuses corpses or apparel: %v", row["id"], dumps)
		}
	}
	report["dump_zones"] = dumps
	if len(dumps) != 1 {
		return fmt.Errorf("want exactly one Low dump zone, got %d: %v", len(dumps), dumps)
	}
	return nil
}

// checkIncineratorZone proves the declared Sanitation store: a stockpile zone
// over the whole incinerator interior at Preferred priority, above the Low
// dump so waste hauls in from it.
func checkIncineratorZone(ctx context.Context, s cases.Session, h *na.Harness, report na.Report, interior policy.Rectangle) error {
	reply, err := h.Wire(ctx, "incinerator-zone", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	grid, err := h.MapCells(ctx, "incinerator-zone-cells", s.Identity())
	if err != nil {
		return err
	}
	zoneCells := na.ZoneCells(grid)
	want := map[domain.Cell]bool{}
	for _, c := range policy.RectangleCells(interior) {
		want[c] = true
	}
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if !strings.EqualFold(na.AsString(row["type"]), "stockpile") {
			continue
		}
		cells := zoneCells[na.AsString(row["id"])]
		if len(cells) != len(want) {
			continue
		}
		covers := true
		for _, c := range cells {
			covers = covers && want[c]
		}
		if !covers {
			continue
		}
		report["incinerator_zone"] = row
		if !strings.Contains(strings.ToLower(na.AsString(row["priority"])), "preferred") {
			return fmt.Errorf("incinerator zone %v is not Preferred: %v", row["id"], row["priority"])
		}
		return nil
	}
	return fmt.Errorf("no stockpile zone covers exactly the incinerator interior %+v", interior)
}

// disposalRead is the native read of the staged things and the incinerator.
type disposalReading struct {
	things   map[string]map[string]any
	interior []map[string]any
	ring     []map[string]any
	room     map[string]any
	ash      int
	fires    int
	beyond   int
	door     int
}

func readDisposal(ctx context.Context, h *na.Harness, ids []string, room policy.PlannedRoom) (disposalReading, error) {
	in := room.Interior
	reply, err := h.Call(ctx, "disposal-read", disposalRead, map[string]any{
		"ids": strings.Join(ids, ","), "minX": in.X, "minZ": in.Z, "maxX": in.X + in.Width - 1, "maxZ": in.Z + in.Height - 1,
		"doorX": room.Door.X, "doorZ": room.Door.Z,
	})
	if err != nil {
		return disposalReading{}, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return disposalReading{}, fmt.Errorf("%s refused: %#v", disposalRead, reply)
	}
	read := disposalReading{things: map[string]map[string]any{}, ash: int(na.AsNumber(reply["ash"])), fires: int(na.AsNumber(reply["fires"])),
		beyond: int(na.AsNumber(reply["beyondDoorRoom"])), door: int(na.AsNumber(reply["doorRoom"]))}
	read.room, _ = na.AsMap(reply["room"])
	for _, raw := range na.AsSlice(reply["things"]) {
		row, _ := na.AsMap(raw)
		read.things[na.AsString(row["id"])] = row
	}
	for _, raw := range na.AsSlice(reply["interior"]) {
		row, _ := na.AsMap(raw)
		read.interior = append(read.interior, row)
	}
	for _, raw := range na.AsSlice(reply["ring"]) {
		row, _ := na.AsMap(raw)
		read.ring = append(read.ring, row)
	}
	return read, nil
}

// gone is a thing that is neither standing on the map nor carried: burned.
func (r disposalReading) gone(id string) bool {
	row := r.things[id]
	spawned, _ := na.AsBool(row["spawned"])
	carried, _ := na.AsBool(row["carried"])
	return row != nil && !spawned && !carried
}

// checkFreshStranger proves a stranger corpse that is still fresh is not in
// the incinerator: it waits for the butcher or the morgue (#1811, #1820).
func checkFreshStranger(read disposalReading, id string, report na.Report, when string) error {
	row := read.things[id]
	inside, _ := na.AsBool(row["inside"])
	report["fresh_stranger_"+when] = row
	if inside && na.AsString(row["rot"]) == "Fresh" {
		return fmt.Errorf("the fresh stranger corpse %s lies in the incinerator: %#v", id, row)
	}
	return nil
}

// checkBurned proves the burn window: the rotten corpses and the seeded
// apparel burned, the journal's burn was one ignite by a drafted burner, the
// ash is cleaned, and the room is its own walled, doored, unroofed room.
func checkBurned(ctx context.Context, s cases.Session, read disposalReading, staged map[string]string, seeded []string, room policy.PlannedRoom, report na.Report) error {
	for _, id := range append([]string{staged["animal"], staged["rottenStranger"]}, seeded...) {
		if !read.gone(id) {
			return fmt.Errorf("thing %s survived the burn: %#v", id, read.things[id])
		}
	}
	if err := checkFreshStranger(read, staged["freshStranger"], report, "burned"); err != nil {
		return err
	}
	if read.ash != 0 || read.fires != 0 {
		return fmt.Errorf("the burn left %d ash and %d fires", read.ash, read.fires)
	}
	burn, err := burnPlans(ctx, s)
	if err != nil {
		return err
	}
	report["burn_plans"] = burn
	if burn.ignites != 1 || burn.drafts < 1 {
		return fmt.Errorf("want one completed ignite behind a draft, got %+v", burn)
	}
	if burn.cleans < 1 {
		return fmt.Errorf("no completed ash cleaning: %+v", burn)
	}
	return checkRoom(read, room)
}

// checkRoom proves the incinerator interior resolves to one unroofed room of
// its own (cell.GetRoom) and every wall and the door are non-flammable.
func checkRoom(read disposalReading, room policy.PlannedRoom) error {
	id := int(na.AsNumber(read.room["id"]))
	if id < 0 || int(na.AsNumber(read.room["cellCount"])) != len(read.interior) {
		return fmt.Errorf("the incinerator room is not its 3x3 interior: %#v over %d cells", read.room, len(read.interior))
	}
	for _, cell := range read.interior {
		if int(na.AsNumber(cell["room"])) != id {
			return fmt.Errorf("interior cell %v is in room %v, not %d", cell, cell["room"], id)
		}
		if roofed, _ := na.AsBool(cell["roofed"]); roofed {
			return fmt.Errorf("interior cell %v is roofed", cell)
		}
	}
	if read.beyond == id || read.door == id {
		return fmt.Errorf("the room %d continues past the door: door %d, beyond %d", id, read.door, read.beyond)
	}
	in := room.Interior
	var doors int
	for _, e := range read.ring {
		x, z := int32(na.AsNumber(e["x"])), int32(na.AsNumber(e["z"]))
		corner := (x < in.X || x >= in.X+in.Width) && (z < in.Z || z >= in.Z+in.Height)
		def := na.AsString(e["def"])
		if def == "" {
			if !corner {
				return fmt.Errorf("ring cell %d,%d has no wall or door", x, z)
			}
			continue
		}
		if f := na.AsNumber(e["flammability"]); f > 0 {
			return fmt.Errorf("ring %s at %d,%d (%v) is flammable: %v", def, x, z, e["stuff"], f)
		}
		if isDoor, _ := na.AsBool(e["door"]); isDoor {
			doors++
			if (domain.Cell{X: x, Z: z}) != room.Door {
				return fmt.Errorf("door at %d,%d, planned at %v", x, z, room.Door)
			}
		}
	}
	if doors != 1 {
		return fmt.Errorf("the ring holds %d doors, want one", doors)
	}
	return nil
}

// burnCounts are the completed action kinds of the burn and ash plans.
type burnCounts struct {
	ignites, drafts, cleans, equips int
}

// burnPlans counts the completed actions of the journal's MaintainIncineration
// burn-wave and burn-ash plans by kind.
func burnPlans(ctx context.Context, s cases.Session) (burnCounts, error) {
	var out burnCounts
	journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
	if err != nil {
		return out, fmt.Errorf("reopen journal: %w", err)
	}
	defer journal.Close()
	goal, err := sustainedfood.SampleStandard(ctx, journal, policy.MaintainIncineration)
	if err != nil {
		return out, err
	}
	plans, _ := goal["plans"].([]map[string]any)
	retired, _ := goal["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		method, _ := plan["method"].(string)
		kinds, _ := plan["kinds"].(map[string]int)
		stages, _ := plan["stages"].(map[string]int)
		actions, _ := plan["actions"].(int)
		if !strings.HasPrefix(method, "burn-") || actions == 0 || stages["completed"] != actions {
			continue
		}
		out.ignites += kinds[string(domain.IgniteAction)]
		out.drafts += kinds[string(domain.OwnedDraftAction)]
		out.cleans += kinds[string(domain.CleanAction)]
		out.equips += kinds[string(domain.EquipAction)]
	}
	return out, nil
}

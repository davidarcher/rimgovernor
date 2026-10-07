package combatlab

import (
	"context"
	"fmt"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The squad hunt cases (#1621, epic #1614). Both start on the blank lab
// with four riflemen (lab-ranged's line) and no raiders, a group of calm
// wild animals north of them, and the planners a hunt and its aftermath
// compose: the hunt origin of ActiveCombat (defense), the butcher and haul
// flow that takes the corpses (bill, haul, work, stockpiles).
const (
	// huntTicks bounds the served window of the kill-and-butcher case: the
	// squad walks into range, shoots three animals, and the corpses are hauled
	// to the butcher spot and butchered. Fast speed keeps it minute-scale.
	huntTicks = 12000
	// huntDoorLeadStep and huntDoorLeadMax bound the lead: the hunt runs in
	// steps until the squad is drafted, then the manhunter is staged.
	huntDoorLeadStep = 300
	huntDoorLeadMax  = 6000
	// huntDoorTicks bounds the door fight after the manhunter appears.
	huntDoorTicks = 6000
	huntBudget    = 8 * time.Minute

	observeChannelsTool = "test/food_channels_observe"
)

var huntFamilies = []string{"defense", "tend", "rescue", "acquisition", "work", "bill", "stockpiles"}

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/hunt-squad",
		Scope: "Squad hunt (#1621): four drafted riflemen on the blank lab and three calm wild animals of two species (two deer, an elk) 12 cells north, " +
			"a butcher spot behind the line, served by the routine planners. Within the tick budget every animal is dead, no colonist is downed, " +
			"and no corpse is left unbutchered on the map while butchered meat stands spawned. Native because no Go snapshot over recorded colony facts can " +
			"cover the end-to-end signal: the real draft, real shooting at fleeing animals, real hauling to the butcher spot and vanilla butchery.",
		Start:       cases.Lab{Colonists: 4},
		RequiredOps: []string{na.LabStartTool, StageTool, pauseModeTool, observeChannelsTool},
		QuietWorld:  true,
		// A checkpoint capture pauses the served game mid-hunt (#890).
		NoCheckpoint: true,
		Serve:        &cases.ServeSpec{Families: huntFamilies, PlayerSpeed: metricsSpeed, Prefix: "combatlab-hunt"},
		Budget:       huntBudget,
		Run:          runHuntSquad,
	})
	cases.Register(cases.Case{
		Name: "combatlab/hunt-door",
		Scope: "Hunt falls back to the door loop (#1621, #1618): the squad hunts three calm wild animals from a roofed room with one door; once drafted and hunting, " +
			"a permanent-manhunter bear (the quiet storyteller zeroes manhunter-on-damage, so a prey cannot be provoked natively) is staged 25 cells out and the controller resumed. " +
			"Within the tick budget the bear is dead and no colonist is downed or dead. Native because no Go snapshot over recorded colony facts can cover the end-to-end signal: " +
			"the real draft kept across the restart, the real door closing and opening, shooting and vanilla bear physics.",
		Start:        cases.Lab{Colonists: 4},
		RequiredOps:  []string{na.LabStartTool, StageTool, pauseModeTool},
		QuietWorld:   true,
		NoCheckpoint: true,
		Serve:        &cases.ServeSpec{Families: huntFamilies, PlayerSpeed: metricsSpeed, Prefix: "combatlab-hunt-door"},
		Budget:       huntBudget,
		Run:          runHuntDoor,
	})
}

// huntKit turns lab-ranged into a hunt: the raiders go, three wild animals
// of two species stand 12 cells north of the centre (inside the squad's
// 12-cell group radius), and a butcher spot waits behind the line.
func huntKit(f *Fixture, cx, cz int) {
	f.Name = "lab-hunt"
	f.Layout = nil
	f.Pawns = deleteHostiles(f.Pawns)
	f.Pawns = append(f.Pawns,
		Pawn{Side: Wild, Kind: "Deer", X: cx - 3, Z: cz + 12},
		Pawn{Side: Wild, Kind: "Deer", X: cx + 3, Z: cz + 12},
		Pawn{Side: Wild, Kind: "Elk", X: cx, Z: cz + 14})
	f.Things = append(f.Things, Thing{Def: "ButcherSpot", X: cx, Z: cz - 14})
}

func deleteHostiles(pawns []Pawn) []Pawn {
	out := pawns[:0:0]
	for _, p := range pawns {
		if p.Side != Hostile {
			out = append(out, p)
		}
	}
	return out
}

// huntDoorRoom is the squad's room: a roofed 11x11 ring behind the sandbags
// whose one door faces the prey. It replaces the sandbag line, which the room
// stands in for.
const huntDoorHalf = 5

func huntDoorKit(f *Fixture, cx, cz int) {
	huntKit(f, cx, cz)
	f.Things = nil
	rz := cz - 12
	f.Things = append(f.Things, walledRoom(cx, rz, huntDoorHalf, Cell{cx, rz + huntDoorHalf})...)
	f.Things = append(f.Things, Thing{Def: "ButcherSpot", X: cx, Z: rz})
	f.Roof = &Roof{Def: "RoofConstructed", MinX: cx - huntDoorHalf, MinZ: rz - huntDoorHalf, MaxX: cx + huntDoorHalf, MaxZ: rz + huntDoorHalf}
	for i := range f.Pawns {
		if f.Pawns[i].Side == Colonist {
			f.Pawns[i].Z = rz
		}
	}
}

// serveTicks lets the served game run until its tick reaches until, failing
// when the service exits or the clock sits still for the stall budget.
func serveTicks(ctx context.Context, service *na.ServiceProcess, until int) (int, error) {
	tick := -1
	err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(context.Context) (string, bool, error) {
		if st, status, err := service.API("GET", "/api/state", nil, ""); err == nil && status == 200 {
			game, _ := na.AsMap(st["game"])
			if t, ok := game["tick"]; ok && t != nil {
				tick = int(na.AsNumber(t))
			}
		}
		return fmt.Sprint(tick), tick >= until, nil
	})
	return tick, err
}

func neverPause(ctx context.Context, h *na.Harness) error {
	reply, err := h.Call(ctx, "pause-mode-never", pauseModeTool, map[string]any{"mode": "Never"})
	if err != nil {
		return err
	}
	if na.AsString(reply["mode"]) != "Never" {
		return fmt.Errorf("%s did not take: %#v", pauseModeTool, reply)
	}
	return nil
}

// servedWindow serves the planners for ticks game ticks past the current
// tick, then stops the service and reattaches the harness. resume starts
// the service on an already running fight.
func servedWindow(ctx context.Context, s cases.Session, ticks int, resume bool) (*na.Harness, error) {
	_, start, err := Read(ctx, s.Harness())
	if err != nil {
		return nil, err
	}
	spec := s.Spec()
	spec.Resume = resume
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return nil, err
	}
	defer service.Stop()
	if !resume {
		if _, err := service.Acquire(); err != nil {
			return nil, err
		}
	}
	service.KeepAuthority(ctx)
	tick, err := serveTicks(ctx, service, start+ticks)
	s.Report()[fmt.Sprintf("servedTick+%d", ticks)] = tick
	if err != nil {
		return nil, err
	}
	service.Stop()
	h, err := s.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause-after", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	return h, nil
}

func runHuntSquad(ctx context.Context, s cases.Session) error {
	staged, err := Stage(ctx, s.Harness(), "lab-ranged", huntKit)
	if err != nil {
		return err
	}
	prey, colonists := staged.ids(Wild), staged.Colonists()
	if len(prey) != 3 || len(colonists) != 4 {
		return fmt.Errorf("staged %d prey and %d colonists, want 3 and 4", len(prey), len(colonists))
	}
	if err := neverPause(ctx, s.Harness()); err != nil {
		return err
	}
	h, err := servedWindow(ctx, s, huntTicks, false)
	if err != nil {
		return err
	}
	pawns, tick, err := Read(ctx, h)
	if err != nil {
		return err
	}
	s.Report()["tick"] = tick
	for _, id := range prey {
		if p, ok := pawns[id]; ok && !p.Dead {
			return fmt.Errorf("prey %s still alive at %d,%d after %d ticks", id, p.X, p.Z, huntTicks)
		}
	}
	for _, id := range colonists {
		if p := pawns[id]; p.Downed || p.Dead {
			return fmt.Errorf("colonist %s downed or dead in a hunt of calm animals: %+v", id, p)
		}
	}
	channels, err := h.Call(ctx, "hunt-channels", observeChannelsTool, nil)
	if err != nil {
		return err
	}
	s.Report()["channels"] = channels
	if n := int(na.AsNumber(channels["corpses"])); n != 0 {
		return fmt.Errorf("%d corpses left on the map after %d ticks: the kills were not hauled and butchered", n, huntTicks)
	}
	meat := 0
	for _, raw := range na.AsSlice(channels["stock"]) {
		row, _ := na.AsMap(raw)
		if spawned, _ := na.AsBool(row["spawned"]); spawned && strings.Contains(na.AsString(row["defName"]), "Meat") {
			meat += int(na.AsNumber(row["units"]))
		}
	}
	if meat == 0 {
		return fmt.Errorf("no meat on the map: the corpses vanished without being butchered: %v", channels["stock"])
	}
	s.Report()["meat"] = meat
	return nil
}

func runHuntDoor(ctx context.Context, s cases.Session) error {
	staged, err := Stage(ctx, s.Harness(), "lab-ranged", huntDoorKit)
	if err != nil {
		return err
	}
	colonists := staged.Colonists()
	if len(colonists) != 4 {
		return fmt.Errorf("staged %d colonists, want 4", len(colonists))
	}
	var cx, cz int
	for _, p := range staged.Fixture.Pawns {
		if p.Side == Wild && p.Kind == "Elk" {
			cx, cz = p.X, p.Z-14
		}
	}
	if err := neverPause(ctx, s.Harness()); err != nil {
		return err
	}
	var h *na.Harness
	drafted := 0
	for lead := 0; drafted < 3 && lead < huntDoorLeadMax; lead += huntDoorLeadStep {
		if h, err = servedWindow(ctx, s, huntDoorLeadStep, lead > 0); err != nil {
			return err
		}
		first, err := rawRead(ctx, h, map[string]any{"action": "read"})
		if err != nil {
			return err
		}
		drafted = 0
		for _, raw := range na.AsSlice(first["pawns"]) {
			row, _ := na.AsMap(raw)
			if on, _ := na.AsBool(row["drafted"]); on && na.AsString(row["side"]) == Colonist {
				drafted++
			}
		}
	}
	s.Report()["draftedBeforeBear"] = drafted
	if drafted < 3 {
		return fmt.Errorf("%d colonists drafted after %d ticks, want the hunt's squad of 3 or 4", drafted, huntDoorLeadMax)
	}
	// The bear charges from 25 cells beyond the prey; the squad is mid-hunt.
	spec := fmt.Sprintf(`{"pawns":[{"side":"manhunter","kind":"Bear_Grizzly","x":%d,"z":%d}]}`, cx, cz+25)
	reply, err := h.Call(ctx, "combatlab-stage-bear", StageTool, map[string]any{"spec": spec})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return fmt.Errorf("%s bear refused: %#v", StageTool, reply)
	}
	rows := na.AsSlice(reply["pawns"])
	if len(rows) != 1 {
		return fmt.Errorf("staged %d pawns for the bear, want 1", len(rows))
	}
	row, _ := na.AsMap(rows[0])
	bear := na.AsString(row["id"])
	if h, err = servedWindow(ctx, s, huntDoorTicks, true); err != nil {
		return err
	}
	pawns, tick, err := Read(ctx, h)
	if err != nil {
		return err
	}
	s.Report()["tick"] = tick
	if p, ok := pawns[bear]; ok && !p.Dead {
		return fmt.Errorf("manhunter bear %s alive at %d,%d after %d ticks", bear, p.X, p.Z, huntDoorTicks)
	}
	for _, id := range colonists {
		if p := pawns[id]; p.Downed || p.Dead {
			return fmt.Errorf("colonist %s downed or dead: the door loop did not hold the bear: %+v", id, p)
		}
	}
	return nil
}

package husbandry

// husbandry/plan proves the herd plan end to end on the blank lab (epic
// #1622, #1638). A lone cow is a founder and is kept; an old milk race
// (superseded while the cow has no mate) loses one male to sterilize in the
// vet room, read back sterilized; a wild bull is then tamed as the cow's mate
// and the old race retires one animal per review, never all at once. The race
// catalog read is asserted first: cows with a milk product, wild and tame
// races, minimum handling skill.
//
// A Go snapshot test cannot cover it (#738): the assertion is native bed and
// room use (an animal bed flagged medical in a standing vet room), a surgery
// bill the game's own doctors perform, and the tame and designation ops
// landing on live animals; the planner decisions themselves are snapshot
// tests (policy.PlanHerd, SterilizeChoice, buildingruntime vet room tests).
//
// The case stages the vet room ring finished from the controller's own
// LayoutPlan (LayoutPlan.VetRoomCells, test/hut_shell_fixture stage, as the
// shelter cases did) so the beds, the medical flag, the VetRoom allowed area
// and the sterilize writes still run the real controller path. The
// controller holds the GABP slot while it runs (#676), so staging and native
// readbacks happen between service runs.
//
// Native semantics this case asserts and does not confirm (each failure
// below names the one it hit):
//   - the Wildness stat orders the catalog's races (the wild race is
//     wilder than a cow, and a cow is below 1);
//   - combat power and the trainables approximation are not read here;
//   - a PawnKindDef shares its race's defName, so lab_spawn can place
//     the old race by the catalog's name;
//   - an AnimalBed can be flagged medical (the sterilize writes only start
//     once the sleeping census reads a standing vet room bed medical);
//   - a doctor performs the sterilize bill on an animal in the vet room
//     (the animal reads sterilized afterwards);
//   - the VetRoom allowed area exists and the sterilize flow moves the
//     animal into it;
//   - native master ids equal pawn ids (the husbandry plan's animal ids are
//     the lab_spawn load ids);
//   - the lab's colonists, whose Animals skill is DebugStartFixture
//     SkillLevel, clear a cow's minimum handling skill;
//   - a stack of Hay per animal keeps the feed review from gating taming.

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"slices"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

const (
	// labAnimalsSkill is DebugStartFixture.SkillLevel, every lab colonist's
	// Animals skill.
	labAnimalsSkill = 8
	hutShellOp      = "test/hut_shell_fixture"
	// planWait is the wall-clock ceiling of each service phase; the stall
	// budget is what ends a broken one.
	planWait = 10 * time.Minute
	// surgeryTicks is the game time a sterilize bill gets once the service
	// has stopped (the harness runs the game for it).
	surgeryTicks = 3 * na.TicksPerDay
	// stageWood is the WoodLog dropped beside the vet room: pen fences and
	// beds are built from it.
	stageWood = 400
	// oldAge is the old race's age in years: adult, so each adds capacity.
	// The old race is two males and two females, so one male can be
	// sterilized while a breeding pair stays (herd_sterilize.go).
	oldAge = 8
	// cowAge is an adult cow's age in years (HusbandryFixture's animals).
	cowAge = 4
)

func init() {
	cases.Register(cases.Case{
		Name: "husbandry/plan",
		Scope: "Herd plan end to end on the lab (#1638): the catalog race rows carry cows with a milk product, wild and tame " +
			"races and a minimum handling skill; a lone cow founder is kept (never culled or sterilized) beside an old milk race; " +
			"the old race's surplus male is sterilized in the controller-built vet room (VetRoom area, medical animal bed, " +
			"surgery bill) and reads back sterilized; a wild bull is tamed as the cow's mate and the old race then retires one " +
			"animal per review. A snapshot test cannot cover it: native bed and room use, surgery, and the tame and designation " +
			"ops are the signal. Unconfirmed native semantics: wildness stat, combat power and trainables approximation, animal " +
			"bed medical flag, surgery in the vet bed, bedroom role with a sleeping spot, the VetRoom area, native master ids " +
			"equal pawn ids.",
		Start:       cases.LabStart(),
		RequiredOps: []string{na.LabSpawnTool, hutShellOp},
		Serve: &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Husbandry, routinefamily.AnimalContainment, routinefamily.Sheltering},
			NativeTimeout: 60 * time.Second, Prefix: "husbandry-plan"},
		Budget: 28 * time.Minute,
		Reason: "one unstaged run of three service phases: the controller builds the pen, the vet room beds and the medical flag " +
			"(construction), the game's doctors then sterilize, and a handler tames the mate before the retirement shows; " +
			"the pen and beds cannot be staged without leaving the controller path the case exists to prove",
		Run: runPlan,
	})
}

// raceRow is the catalog facts the case reads.
type raceRow struct {
	def      string
	wildness *float64
	body     *float64
	minSkill *int
	milk     *struct{ amount, interval float64 }
}

// readCatalog reads the definition catalog once and returns its animal race
// rows by defName (#1722: the race rows of the catalog, not a separate read).
func readCatalog(ctx context.Context, h *na.Harness, identity map[string]any) (map[string]raceRow, error) {
	data, err := json.Marshal(identity)
	if err != nil {
		return nil, err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return nil, err
	}
	catalog, err := h.Client.DefinitionCatalog(ctx, id)
	if err != nil {
		return nil, err
	}
	races, err := catalog.AnimalRaces()
	if err != nil {
		return nil, err
	}
	rows := map[string]raceRow{}
	for def, race := range races.Races {
		row := raceRow{def: string(def)}
		if v, ok := race.Wildness.Value(); ok {
			row.wildness = &v
		}
		if v, ok := race.BodySize.Value(); ok {
			row.body = &v
		}
		if v, ok := race.MinimumHandlingSkill.Value(); ok {
			row.minSkill = &v
		}
		for _, product := range race.Products {
			amount, aok := product.Amount.Value()
			interval, iok := product.IntervalDays.Value()
			if product.Kind == "milk" && aok && iok && interval > 0 {
				row.milk = &struct{ amount, interval float64 }{amount, interval}
			}
		}
		rows[row.def] = row
	}
	return rows, nil
}

// perAdult is a milk race's yield per adult per day.
func (r raceRow) perAdult() float64 { return r.milk.amount / r.milk.interval }

// score is the plan's ranking for a milk race: yield per body size.
func (r raceRow) score() float64 { return r.perAdult() / *r.body }

// checkCatalog asserts the catalog read and picks the old milk race: the
// first (by name) a cow outranks for milk (yield per body size, the cow
// first on a tie) and two adult cows (the founder and her mate) out-yield
// four of, so the plan can retire it.
func checkCatalog(rows map[string]raceRow, report na.Report) (old string, err error) {
	cow, ok := rows["Cow"]
	if !ok || cow.milk == nil {
		return "", fmt.Errorf("race catalog: Cow missing or without a milk product: %#v", cow)
	}
	if cow.minSkill == nil || cow.wildness == nil || cow.body == nil || *cow.body <= 0 {
		return "", fmt.Errorf("race catalog: Cow facts unread (minimum handling skill, wildness or body size): %#v", cow)
	}
	if *cow.minSkill > labAnimalsSkill {
		return "", fmt.Errorf("race catalog: Cow needs handling skill %d, the lab's colonists have %d", *cow.minSkill, labAnimalsSkill)
	}
	// Wildness stat (unconfirmed): a cow is tameable below 1 and the catalog
	// holds wilder races.
	wilder, skills := 0, map[int]bool{}
	for _, r := range rows {
		if r.wildness != nil && *r.wildness > *cow.wildness {
			wilder++
		}
		if r.minSkill != nil {
			skills[*r.minSkill] = true
		}
	}
	if *cow.wildness >= 1 || wilder == 0 {
		return "", fmt.Errorf("race catalog: wildness does not separate tame from wild races (Cow %v, %d wilder races)", *cow.wildness, wilder)
	}
	if len(skills) < 2 {
		return "", fmt.Errorf("race catalog: minimum handling skill is the same %v for every race", skills)
	}
	var names []string
	for def, r := range rows {
		if def == "Cow" || r.milk == nil || r.body == nil || *r.body <= 0 {
			continue
		}
		names = append(names, def)
	}
	sort.Strings(names)
	for _, def := range names {
		r := rows[def]
		if cow.score() >= r.score()*(1-1e-9) && 2*cow.perAdult() >= 4*r.perAdult() {
			old = def
			break
		}
	}
	report["catalog"] = map[string]any{"races": len(rows), "cow": map[string]any{"milk_per_day": cow.perAdult(), "wildness": *cow.wildness,
		"minimum_handling_skill": *cow.minSkill}, "wilder_races": wilder, "milk_races": names, "old_race": old}
	if old == "" {
		return "", fmt.Errorf("race catalog: no other milk race that a cow outranks and two cows out-yield four of (milk races %v)", names)
	}
	return old, nil
}

// animalRow is one animal of the husbandry census.
type animalRow struct {
	id                    string
	gender                string
	sterilized, slaughter bool
	release, queued       bool
	area                  string
}

// census reads the player animals by id.
func census(ctx context.Context, h *na.Harness, label string, identity map[string]any) (map[string]animalRow, error) {
	reply, err := h.Wire(ctx, label, "observations_read_husbandry", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	out := map[string]animalRow{}
	for _, raw := range na.AsSlice(observed["animals"]) {
		row, _ := na.AsMap(raw)
		state, _ := na.AsMap(row["animal"])
		flag := func(key string) bool { b, _ := na.AsBool(state[key]); return b }
		id := na.PawnRef(row)
		out[id] = animalRow{id: id, gender: na.AsString(state["gender"]), sterilized: flag("sterilized"), queued: flag("sterilizeQueued"),
			slaughter: flag("slaughter"), release: flag("release"), area: na.AsString(state["allowedAreaId"])}
	}
	return out, nil
}

// write is one husbandry write the controller journaled.
type write struct {
	method   domain.HusbandryMethod
	animal   string
	argument string
	tick     domain.Tick
	done     bool
}

var writeMethods = []string{"tame-*", "slaughter-*", "release-*", "sterilize-*", "allowed_area-*"}

// writes lists the journal's husbandry writes, oldest first.
func writes(ctx context.Context, st *store.Store) ([]write, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, writeMethods...)
	if err != nil {
		return nil, err
	}
	var out []write
	for i := len(plans) - 1; i >= 0; i-- {
		for j, a := range plans[i].Spec.Actions() {
			h, ok := a.Husbandry()
			if !ok {
				continue
			}
			v := plans[i].Progress[j].View()
			out = append(out, write{h.Method(), string(h.Animal()), h.Argument(), v.Tick, v.Stage == domain.Completed})
		}
	}
	return out, nil
}

// phase is one service run: the service, its journal and the report slot.
type phase struct {
	s    cases.Session
	svc  *na.ServiceProcess
	st   *store.Store
	wait na.Wait
}

func begin(ctx context.Context, s cases.Session, previous *na.ServiceProcess) (*phase, error) {
	var svc *na.ServiceProcess
	var err error
	if previous == nil {
		svc, err = s.Serve(ctx, s.Spec())
	} else {
		svc, err = previous.Restart(ctx)
	}
	if err != nil {
		return nil, err
	}
	if _, err = svc.Acquire(); err != nil {
		svc.Stop()
		return nil, err
	}
	svc.KeepAuthority(ctx)
	st, err := na.OpenStoreWithRetry(ctx, svc.StatePath)
	if err != nil {
		svc.Stop()
		return nil, err
	}
	return &phase{s: s, svc: svc, st: st, wait: na.Wait{Ceiling: planWait, Stall: na.StallBudget(), Terminal: svc.Exited}}, nil
}

// until polls the journal until done; the signature is the write counts and
// the game day, so a broken phase stalls and a long build does not.
func (p *phase) until(ctx context.Context, what string, done func(review store.Rounds, w []write) (bool, error)) error {
	err := na.WaitProgress(ctx, p.wait, func(ctx context.Context) (string, bool, error) {
		review, err := p.st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review"), false, nil
		}
		w, err := writes(ctx, p.st)
		if err != nil {
			return "", false, err
		}
		finished := 0
		for _, x := range w {
			if x.done {
				finished++
			}
		}
		ok, err := done(review, w)
		return na.Signature(len(w), finished, review.Tick/na.TicksPerDay), ok, err
	})
	if err != nil {
		return fmt.Errorf("%s: %w", what, err)
	}
	return nil
}

// stop ends the phase and takes the bridge slot back, paused.
func (p *phase) stop(ctx context.Context, label string) (*na.Harness, error) {
	p.st.Close()
	p.s.Report()[label] = p.svc.Stop()
	h, err := p.s.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause-"+label, "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	return h, nil
}

// vetRoom is the plan's one vet room and its interior cells.
func vetRoom(plan policy.LayoutPlan) (policy.PlannedRoom, []domain.Cell, error) {
	rooms := plan.HerdRooms(policy.PlannedVetRoom)
	if len(rooms) != 1 {
		return policy.PlannedRoom{}, nil, fmt.Errorf("layout plan has %d vet rooms, want 1", len(rooms))
	}
	cells := plan.VetRoomCells()
	if len(cells) == 0 {
		return policy.PlannedRoom{}, nil, fmt.Errorf("layout plan vet room has no interior cells")
	}
	return rooms[0], cells, nil
}

// stageRing spawns the vet room's wall ring and door finished and drops the
// build wood beside it.
func stageRing(ctx context.Context, h *na.Harness, room policy.PlannedRoom, cells []domain.Cell, report na.Report) error {
	footprint, err := domain.NewRoomFootprint(cells, room.Door, room.DoorRot)
	if err != nil {
		return fmt.Errorf("vet room is not a room footprint: %w", err)
	}
	var walls []string
	for _, c := range footprint.Walls() {
		if c != room.Door {
			walls = append(walls, fmt.Sprintf("%d,%d", c.X, c.Z))
		}
	}
	reply, err := h.Call(ctx, "stage-vet-ring", hutShellOp, map[string]any{"action": "stage", "walls": strings.Join(walls, ";"),
		"door": fmt.Sprintf("%d,%d", room.Door.X, room.Door.Z), "doorRotation": string(room.DoorRot), "wood": stageWood})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return fmt.Errorf("%s stage refused: %#v", hutShellOp, reply)
	}
	report["vet_ring"] = map[string]any{"interior_cells": len(cells), "walls": len(walls), "door": room.Door, "rotation": string(room.DoorRot)}
	return nil
}

func runPlan(ctx context.Context, s cases.Session) error {
	report, h := s.Report(), s.Harness()
	prepared := s.Prepared()
	center, _ := na.AsMap(prepared["center"])
	cx, cz := int(na.AsNumber(center["x"])), int(na.AsNumber(center["z"]))
	if cx == 0 && cz == 0 {
		return fmt.Errorf("lab start: no centre in %#v", prepared)
	}

	// 1. The race catalog, then the old race it names.
	rows, err := readCatalog(ctx, h, s.Identity())
	if err != nil {
		return err
	}
	oldRace, err := checkCatalog(rows, report)
	if err != nil {
		return err
	}

	// 2. The herd: a lone cow, four old animals of the old race, and feed
	// and wood in the open. (Unconfirmed: a PawnKindDef shares its race's
	// defName, so the catalog's name spawns the race.)
	spawn := func(t na.LabThing) (string, error) {
		id, _, err := na.LabSpawn(ctx, h, t)
		return id, err
	}
	cow, err := spawn(na.LabThing{Def: "Cow", X: cx + 6, Z: cz + 6, Gender: "female", Age: cowAge})
	if err != nil {
		return err
	}
	var oldMales, oldAll []string
	for i, gender := range []string{"male", "male", "female", "female"} {
		id, err := spawn(na.LabThing{Def: oldRace, X: cx + 8 + i, Z: cz + 6, Gender: gender, Age: oldAge})
		if err != nil {
			return err
		}
		oldAll = append(oldAll, id)
		if gender == "male" {
			oldMales = append(oldMales, id)
		}
	}
	for i := 0; i < 10; i++ {
		for _, item := range []string{"Hay", "WoodLog"} {
			z := cz - 8
			if item == "WoodLog" {
				z = cz - 10
			}
			if _, err := spawn(na.LabThing{Def: item, X: cx - 10 + i, Z: z, Count: 75}); err != nil {
				return err
			}
		}
	}
	report["herd"] = map[string]any{"cow": cow, "old_race": oldRace, "old_animals": oldAll, "old_males": oldMales}

	// 3. Phase one: the controller plans the colony and its vet room.
	one, err := begin(ctx, s, nil)
	if err != nil {
		return err
	}
	var room policy.PlannedRoom
	var cells []domain.Cell
	err = one.until(ctx, "layout plan with a vet room", func(review store.Rounds, _ []write) (bool, error) {
		record, ok, err := one.st.LayoutPlan(ctx, review.Snapshot, review.Tick)
		if err != nil || !ok || len(record.Plan.HerdRooms(policy.PlannedVetRoom)) == 0 {
			return false, err
		}
		room, cells, err = vetRoom(record.Plan)
		return err == nil, err
	})
	if err != nil {
		one.st.Close()
		one.svc.Stop()
		return err
	}
	if h, err = one.stop(ctx, "plan_service"); err != nil {
		return err
	}
	if err := stageRing(ctx, h, room, cells, report); err != nil {
		return err
	}

	// 4. Phase two: the vet room is furnished, flagged medical (the sleeping
	// census reads it, or the sterilize flow would never start) and one old
	// male is sterilized; the cow founder is untouched.
	two, err := begin(ctx, s, one.svc)
	if err != nil {
		return err
	}
	var sterilized string
	err = two.until(ctx, "sterilize write on an old male", func(_ store.Rounds, w []write) (bool, error) {
		for _, x := range w {
			if x.animal == cow {
				return false, fmt.Errorf("the lone cow founder got a %s write", x.method)
			}
			if x.method == domain.HusbandrySterilize && x.done {
				if !slices.Contains(oldMales, x.animal) {
					return false, fmt.Errorf("sterilize wrote %s, not one of the old males %v", x.animal, oldMales)
				}
				sterilized = x.animal
			}
			if x.method == domain.HusbandrySlaughter || x.method == domain.HusbandryRelease {
				return false, fmt.Errorf("%s of %s while the cow has no mate: the old race retired early", x.method, x.animal)
			}
		}
		return sterilized != "", nil
	})
	if err != nil {
		two.st.Close()
		two.svc.Stop()
		return err
	}
	if h, err = two.stop(ctx, "sterilize_service"); err != nil {
		return err
	}
	identity, err := na.ReadIdentity(ctx, h, "identity-surgery")
	if err != nil {
		return err
	}
	// The bill is queued; the game's doctors perform it while the game runs.
	if _, err := na.RunUntil(ctx, h, "surgery", surgeryTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		herd, err := census(ctx, h, "surgery-census", identity)
		if err != nil {
			return "", false, err
		}
		a, ok := herd[sterilized]
		if !ok {
			return "", false, fmt.Errorf("sterilized animal %s left the census", sterilized)
		}
		return na.Signature(a.sterilized, a.queued, a.area), a.sterilized, nil
	}); err != nil {
		return fmt.Errorf("sterilized animal %s never read sterilized: %w", sterilized, err)
	}
	report["sterilized"] = sterilized

	// 5. Phase three: a wild bull appears; it is tamed as the cow's mate and
	// the old race then retires gradually.
	bull, err := spawn(na.LabThing{Def: "Cow", X: cx + 14, Z: cz + 2, Unowned: true, Gender: "male", Age: cowAge})
	if err != nil {
		return err
	}
	report["wild_bull"] = bull
	three, err := begin(ctx, s, two.svc)
	if err != nil {
		return err
	}
	var removals []write
	err = three.until(ctx, "bull tamed and two old animals removed", func(_ store.Rounds, w []write) (bool, error) {
		tamed := false
		removals = removals[:0]
		for _, x := range w {
			if x.animal == cow || x.animal == bull && x.method != domain.HusbandryTame {
				return false, fmt.Errorf("the cow pair got a %s write on %s", x.method, x.animal)
			}
			switch {
			case x.method == domain.HusbandryTame && x.animal == bull && x.done:
				tamed = true
			case (x.method == domain.HusbandrySlaughter || x.method == domain.HusbandryRelease) && x.done:
				if !slices.Contains(oldAll, x.animal) {
					return false, fmt.Errorf("%s wrote %s, not an old animal %v", x.method, x.animal, oldAll)
				}
				removals = append(removals, x)
			}
		}
		return tamed && len(removals) >= 2, nil
	})
	if err != nil {
		three.st.Close()
		three.svc.Stop()
		return err
	}
	if h, err = three.stop(ctx, "retire_service"); err != nil {
		return err
	}
	// Gradual: one removal per review, so the first two land at different
	// ticks and the old race is not gone at once.
	if removals[0].animal == removals[1].animal || removals[0].tick >= removals[1].tick {
		return fmt.Errorf("old race retired in one review, not gradually: %+v", removals[:2])
	}
	report["removals"] = fmt.Sprintf("%v", removals)

	// 6. Native readback: the bull is a player animal (tamed), the cow pair
	// is whole and unmarked, the old race is going and the male stays sterile.
	if identity, err = na.ReadIdentity(ctx, h, "identity-after"); err != nil {
		return err
	}
	herd, err := census(ctx, h, "final-census", identity)
	if err != nil {
		return err
	}
	for _, id := range []string{cow, bull} {
		a, ok := herd[id]
		if !ok {
			return fmt.Errorf("cow %s is not a player animal after the run (the bull was not tamed, or the cow was lost)", id)
		}
		if a.slaughter || a.release || a.sterilized {
			return fmt.Errorf("cow %s is designated or sterilized: %+v", id, a)
		}
	}
	gone := 0
	for _, id := range oldAll {
		if a, ok := herd[id]; !ok || a.slaughter || a.release {
			gone++
		}
	}
	if gone < 2 {
		return fmt.Errorf("only %d of the old race %v read as removed or designated", gone, oldAll)
	}
	if a, ok := herd[sterilized]; ok && !a.sterilized {
		return fmt.Errorf("old male %s no longer reads sterilized: %+v", sterilized, a)
	}
	report["old_race_going"] = gone
	return nil
}

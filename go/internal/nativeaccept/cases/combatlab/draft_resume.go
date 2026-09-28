package combatlab

import (
	"context"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// repairDoorHitPoints is the staged gap door's hit points: well under any
// door's maximum, so it reads damaged.
const repairDoorHitPoints = 10

// resumeWait bounds each wait of the resume case: the fight drafting its
// defenders, and the restarted controller reaching automate.
const resumeWait = 120 * time.Second

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/repair",
		Scope: "Drafted Repair (#900), a native op contract no snapshot can prove: on lab-choke with a damaged wooden door in the gap, " +
			"the brawlers are drafted and one combat.orders repair order on the door applies with job Repair; one tick later that brawler's job is Repair on the door and it is still drafted.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run:         runRepair,
	})
	cases.Register(cases.Case{
		Name: "combatlab/resume-drafted",
		Scope: "Defenders stay drafted through a --resume restart (#916): lab-choke served by the routine defense planner under --resume until its fight drafts a roster, " +
			"the controller killed and restarted on the same journal; once the restarted controller is back in automate every standing defender is still on the fight's roster, and each reads drafted in the game.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool, pauseModeTool},
		QuietWorld:  true,
		// A checkpoint capture pauses the served game mid-fight (#890).
		NoCheckpoint: true,
		// Normal speed: the fight must outlast the kill-and-restart window.
		Serve:  &cases.ServeSpec{Families: metricsFamilies, Resume: true, PlayerSpeed: "Normal", Prefix: "combatlab-resume"},
		Budget: cases.LabBudget,
		Run:    runResumeDrafted,
	})
}

// repairKit closes lab-choke's gap with a damaged wooden door.
func repairKit(f *Fixture, cx, cz int) {
	f.Things = append(f.Things, Thing{Def: "Door", Stuff: "WoodLog", X: cx, Z: cz + chokeHalf, HitPoints: repairDoorHitPoints})
}

func runRepair(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	var cx, cz int
	staged, err := Stage(ctx, h, "lab-choke", func(f *Fixture, x, z int) { repairKit(f, x, z); cx, cz = x, z })
	if err != nil {
		return err
	}
	door := staged.Things[len(staged.Things)-1]
	colonists := staged.Colonists()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "combat-repair-acquire", identity); err != nil {
		return err
	}
	// The three brawlers stand two cells inside the gap.
	brawlers := colonists[:3]
	if err := draftAll(ctx, h, identity, "repair-draft", brawlers); err != nil {
		return err
	}
	// A lab colonist may be incapable of construction (cannot_repair): the
	// first brawler whose order applies is the repairer.
	repairer, tried := "", []map[string]any{}
	for i, id := range brawlers {
		results, err := issue(ctx, h, identity, fmt.Sprintf("combat-repair-%d", i), []any{
			map[string]any{"pawn": map[string]any{"entityId": id}, "repair": map[string]any{"cell": cell(cx, cz+chokeHalf)}},
		})
		if err != nil {
			return err
		}
		tried = append(tried, results[0])
		if applied, _ := na.AsBool(results[0]["applied"]); applied {
			if na.AsString(results[0]["jobDef"]) != "Repair" {
				return fmt.Errorf("brawler %d repair applied with job %v, want Repair: %v", i, results[0]["jobDef"], results[0])
			}
			repairer = id
			break
		}
	}
	report["results"] = tried
	if repairer == "" {
		return fmt.Errorf("no brawler took the repair order on the door at %d,%d: %v", cx, cz+chokeHalf, tried)
	}
	after, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	p := after.pawns[repairer]
	report["repairer"] = p
	if na.AsString(p["job"]) != "Repair" || na.AsString(p["jobThing"]) != door {
		return fmt.Errorf("repairer %s after one tick: job %v on %v, want Repair on %s: %v", repairer, p["job"], p["jobThing"], door, p)
	}
	if drafted, _ := na.AsBool(p["drafted"]); !drafted {
		return fmt.Errorf("repairer %s undrafted by the Repair job: %v", repairer, p)
	}
	return nil
}

func runResumeDrafted(ctx context.Context, s cases.Session) error {
	report := s.Report()
	staged, err := Stage(ctx, s.Harness(), "lab-choke")
	if err != nil {
		return err
	}
	if layout := staged.Fixture.Layout; layout != nil {
		if _, err := storeLayout(ctx, s.Harness(), s.Identity(), *layout); err != nil {
			return err
		}
	}
	// A MajorThreat letter would pause the game as a player (#890).
	if reply, err := s.Harness().Call(ctx, "pause-mode-never", pauseModeTool, map[string]any{"mode": "Never"}); err != nil {
		return err
	} else if na.AsString(reply["mode"]) != "Never" {
		return fmt.Errorf("%s did not take: %#v", pauseModeTool, reply)
	}
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	service.KeepAuthority(ctx)
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	plan, before, err := waitDrafted(ctx, journal, service)
	if err != nil {
		return err
	}
	report["before"] = map[string]any{"plan": plan, "roster": before.Roster}

	// Kill mid-fight and restart on the same journal. The game frees the
	// GABP slot shortly after the kill, so the restart may retry.
	service.Stop()
	deadline := time.Now().Add(90 * time.Second)
	var restarted *na.ServiceProcess
	for {
		if restarted, err = service.Restart(ctx); err == nil {
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller never attached: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
	defer restarted.Stop()
	restarted.KeepAuthority(ctx)
	if err := waitAutomate(ctx, restarted); err != nil {
		return err
	}
	journal, err = restarted.Store(ctx)
	if err != nil {
		return err
	}
	after, ok, err := journal.LoadCombatFight(ctx, plan)
	if err != nil {
		return err
	}
	report["after"] = map[string]any{"open": after.Open, "roster": after.Roster}
	restarted.Stop()
	if !ok || !after.Open {
		return fmt.Errorf("fight %s closed across the restart (row %v): the fight ended inside the restart window, nothing proven", plan, ok)
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	read, err := tickRead(ctx, h)
	if err != nil {
		return err
	}
	var checked []string
	for _, pawn := range sortedPawns(before.Roster) {
		row, found := read.pawns[string(pawn)]
		if downed, _ := na.AsBool(row["downed"]); !found || downed {
			continue // a downed or gone defender is undrafted on purpose
		}
		if dead, _ := na.AsBool(row["dead"]); dead {
			continue
		}
		if !after.Roster[pawn] {
			return fmt.Errorf("defender %s left the fight's roster across the restart: before %v, after %v", pawn, before.Roster, after.Roster)
		}
		if drafted, _ := na.AsBool(row["drafted"]); !drafted {
			return fmt.Errorf("defender %s undrafted across the restart: %v", pawn, row)
		}
		checked = append(checked, string(pawn))
	}
	report["stayedDrafted"] = checked
	if len(checked) == 0 {
		return fmt.Errorf("no standing defender left to check after the restart: roster before %v", before.Roster)
	}
	return nil
}

// waitDrafted waits for an open fight with a drafted roster (#939: the
// combat_fights row keyed by plan_id is the fight's draft record).
func waitDrafted(ctx context.Context, journal *store.Store, service *na.ServiceProcess) (domain.PlanID, store.CombatFight, error) {
	deadline := time.Now().Add(resumeWait)
	for {
		if err := service.Exited(); err != nil {
			return "", store.CombatFight{}, fmt.Errorf("service exited before the fight drafted: %w", err)
		}
		fights, err := journal.OpenCombatFights(ctx)
		if err != nil {
			return "", store.CombatFight{}, err
		}
		for plan, fight := range fights {
			if fight.Open && len(fight.Roster) > 0 {
				return plan, fight, nil
			}
		}
		if time.Now().After(deadline) {
			return "", store.CombatFight{}, fmt.Errorf("no open fight drafted a roster within %s: %v", resumeWait, fights)
		}
		select {
		case <-ctx.Done():
			return "", store.CombatFight{}, ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
	}
}

// waitAutomate waits for the restarted controller's state to read
// automate: its resume (ManualForResume, then Acquire) is done.
func waitAutomate(ctx context.Context, service *na.ServiceProcess) error {
	deadline := time.Now().Add(resumeWait)
	for {
		if err := service.Exited(); err != nil {
			return fmt.Errorf("restarted service exited: %w", err)
		}
		if st, status, err := service.API("GET", "/api/state", nil, ""); err == nil && status == 200 && na.AsString(st["mode"]) == "automate" {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller never reached automate within %s", resumeWait)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
	}
}

func sortedPawns(roster map[domain.PawnID]bool) []domain.PawnID {
	out := make([]domain.PawnID, 0, len(roster))
	for p := range roster {
		out = append(out, p)
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

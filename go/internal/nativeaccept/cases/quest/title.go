// The quest/first-title case (#1613, epic #1598) proves the Royalty path from
// an Empire quest to a first royal title, end to end in vanilla: the colony
// accepts the Empire favor quest through QuestAccept, native grants the favor
// and generates the bestowing-ceremony quest, the title claim gate (#1605)
// lets the colony accept it, the bestower waits and the Ritual intent (#1639)
// starts the ceremony, and the colonist ends up holding the Empire title.
package quest

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	titlePrepareTool = "test/royal_title_prepare"
	pawnsReadWire    = "observations_list_pawns"
	// titleCeiling bounds the journal wait; the stall budget ends it earlier
	// when nothing moves.
	titleCeiling = 8 * time.Minute
	// titleTicks bounds the native run after the ceremony command: the
	// bestowing ritual runs a few thousand ticks before the title is set.
	titleTicks = 20000
)

func init() {
	cases.Register(cases.Case{
		Name: "quest/first-title",
		Scope: "Issue #1613: a colony takes an Empire favor quest, accepts the bestowing-ceremony quest native generates " +
			"once the favor suffices, starts the ceremony and the colonist holds the Empire title natively. A Go snapshot over " +
			"recorded facts cannot cover it: the chain is vanilla's own (quest reward, favor, ceremony quest generation, " +
			"bestower lord, ritual and title assignment), asserted on the live royalty read.",
		Start:       cases.Fixture{Op: titlePrepareTool, On: cases.LabStart()},
		Expansions:  []string{"ludeon.rimworld.royalty"},
		NoKeep:      true,
		Quiet:       na.QuietRequired,
		RequiredOps: []string{titlePrepareTool},
		// The shelter family keeps supervised windows running (the population
		// planner alone never advances the clock); dialog answers the letters.
		Serve: &cases.ServeSpec{
			Families: []routinefamily.Family{routinefamily.PopulationJoiner, routinefamily.Shelter, routinefamily.Dialog}, NativeTimeout: 15 * time.Second, Prefix: "quest-first-title",
		},
		Budget: 12 * time.Minute,
		Run:    runFirstTitle,
	})
}

// ritualStarted reports whether the journal holds a completed bestowing
// ritual start for pawn.
func ritualStarted(ctx context.Context, st *store.Store, pawn domain.PawnID) (bool, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, "ritual-start-*")
	if err != nil {
		return false, err
	}
	for _, p := range plans {
		for _, progress := range p.Progress {
			ritual, ok := progress.Action().Ritual()
			if ok && ritual.Pawn() == pawn && ritual.Ritual() == domain.RitualBestowing && progress.View().Stage == domain.Completed {
				return true, nil
			}
		}
	}
	return false, nil
}

// empireTitle is the title pawn holds from any faction in a pawn list's
// royalty holdings (PawnState.royalty), with the favor held; "" when none.
func empireTitle(observed map[string]any, pawn string) (title string, favor float64) {
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		if na.AsString(ref["id"]) != pawn {
			continue
		}
		royalty, _ := na.AsMap(row["royalty"])
		for _, h := range na.AsSlice(royalty["holdings"]) {
			holding, _ := na.AsMap(h)
			if t := na.AsString(holding["title"]); t != "" {
				return t, na.AsNumber(holding["favor"])
			}
			favor = na.AsNumber(holding["favor"])
		}
	}
	return "", favor
}

func runFirstTitle(ctx context.Context, s cases.Session) error {
	report, identity, prepared := s.Report(), s.Identity(), s.Prepared()
	var h *na.Harness
	offer := domain.QuestID(na.AsString(prepared["questId"]))
	pawn, title := na.AsString(prepared["pawnId"]), na.AsString(prepared["title"])
	if offer == "" || pawn == "" || title == "" || na.AsNumber(prepared["favor"]) <= 0 {
		return fmt.Errorf("fixture: unexpected first-title staging: %#v", prepared)
	}
	if needsThrone, _ := na.AsBool(prepared["needsThrone"]); needsThrone {
		return fmt.Errorf("fixture: the first title %s asks for a throne room, which this case does not stage: %#v", title, prepared)
	}
	report["fixture_quest"], report["pawn"], report["title"] = string(offer), pawn, title

	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer st.Close()

	// The colony's own answers: the Empire offer, then the bestowing quest
	// native generated for the favor (a second quest accept), then the
	// ceremony command once the bestower waits.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: titleCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		answers, _, err := joinerAnswers(ctx, st)
		if err != nil {
			return "", false, err
		}
		for quest, answer := range answers {
			if stage := domain.Stage(answer.Stage); stage == domain.Unsuccessful || stage == domain.Cancelled {
				return "", false, fmt.Errorf("the acceptance of quest %s ended %s", quest, stage)
			}
		}
		started, err := ritualStarted(ctx, st, domain.PawnID(pawn))
		if err != nil {
			return "", false, err
		}
		report["answers"] = answers
		return na.Signature(len(answers), fmt.Sprint(answers[offer].Stage), started), started, nil
	})
	if err != nil {
		return fmt.Errorf("ceremony command: %w", err)
	}
	if answers, _ := report["answers"].(map[domain.QuestID]joinerAnswer); len(answers) < 2 || domain.Stage(answers[offer].Stage) != domain.Completed {
		return fmt.Errorf("the ceremony started without the Empire offer and a bestowing quest both accepted: %#v", answers)
	}
	report["run_keepalive"] = service.Stop()

	// Native postcondition after the service releases the game slot: running
	// the game on lets the bestowing ritual finish, and the colonist holds the
	// Empire title the fixture's favor was costed for.
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	var held string
	var favor float64
	elapsed, err := na.RunUntil(ctx, h, "title-granted", titleTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Wire(ctx, "pawn-royalty-read", pawnsReadWire, map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{pawn}}, "details": map[string]any{}})
		if err != nil {
			return "", false, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", false, err
		}
		held, favor = empireTitle(observed, pawn)
		return na.Signature(held, favor), held != "", nil
	})
	report["title_ticks"] = elapsed
	if err != nil {
		return err
	}
	report["held_title"], report["held_favor"] = held, favor
	if held != title {
		return fmt.Errorf("colonist %s holds %q, want the first title %q", pawn, held, title)
	}
	return nil
}

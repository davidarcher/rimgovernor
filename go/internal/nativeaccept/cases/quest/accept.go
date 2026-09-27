// The quest/accept case exercises the AcceptQuestIntent arm of
// Actions/Apply (#942) end to end against a live game: a real
// not-yet-accepted quest carrying a two-option native reward-choice part,
// native acceptance (an actual Quest.Accept settings write and its
// QuestPart_Choice.Choose call) through the same rimgovernor/operations_apply
// wire contract Go's quest accept boundary drives, and its replay semantics.
// Uses a private disposable fixture (test/quest_accept_prepare) since a
// minimal quest with an exact reward-choice shape cannot be produced
// deterministically through native random quest generation. The fixture
// quest carries no native QuestPart_RequirementsToAccept part (the vanilla
// mechanism that needs an accepter,
// QuestPart_RequirementsToAcceptColonistWithTitle, requires a held Royalty
// title), so this harness instead exercises the "this quest does not accept
// an accepter" refusal branch by supplying one anyway.
package quest

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "quest/accept",
		Scope: "Native AcceptQuestIntent: an actual Quest.Accept settings write and " +
			"QuestPart_Choice reward selection, out-of-range reward and accepter-not-required " +
			"refusals, replay idempotency and an already-accepted quest applied again.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/quest_accept_prepare"},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    runAccept,
	})
}

func runAccept(ctx context.Context, s cases.Session) error {
	h, identity, names, prepared := s.Harness(), s.Identity(), s.Names(), s.Prepared()
	if !na.Contains(names, "rimgovernor/observations_read_world_progression") {
		return fmt.Errorf("missing rimgovernor/observations_read_world_progression in discovery")
	}

	questID := na.AsString(prepared["questId"])
	var pawnIDs []string
	for _, raw := range na.AsSlice(prepared["pawnIds"]) {
		pawnIDs = append(pawnIDs, fmt.Sprint(raw))
	}
	if questID == "" || len(pawnIDs) < 1 {
		return fmt.Errorf("quest_accept_prepare: unexpected fixture identifiers: %#v", prepared)
	}

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	// target reads the world-progression census for the fixture's exact
	// quest (NativeWorldProgressionObservation.cs's Quests()), the census
	// the joiner planner chooses offers from.
	target := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_read_world_progression", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "includeStorage": false,
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, err
		}
		var questRow map[string]any
		for _, raw := range na.AsSlice(observed["quests"]) {
			row, _ := na.AsMap(raw)
			if na.AsString(row["id"]) == questID {
				questRow = row
			}
		}
		if questRow == nil {
			return nil, fmt.Errorf("%s: fixture quest not found in world progression: %#v", label, observed)
		}
		return questRow, nil
	}

	questRow, err := target("target-before")
	if err != nil {
		return err
	}
	if na.AsString(questRow["state"]) != "QUEST_STATE_NOT_YET_ACCEPTED" && na.AsString(questRow["state"]) != "NotYetAccepted" {
		return fmt.Errorf("target-before: expected a not-yet-accepted quest, got %#v", questRow)
	}
	if requiresAccepter, _ := na.AsBool(questRow["requiresAccepter"]); requiresAccepter {
		return fmt.Errorf("target-before: expected the fixture quest to not require an accepter, got %#v", questRow)
	}
	if canAccept, _ := na.AsBool(questRow["canAccept"]); !canAccept {
		return fmt.Errorf("target-before: expected the fixture quest to be acceptable, got %#v", questRow)
	}
	apply := func(label, key, accepterPawn string, rewardChoice int) (map[string]any, error) {
		intent := map[string]any{"questId": questID, "rewardChoice": rewardChoice}
		if accepterPawn != "" {
			intent["accepterPawnId"] = accepterPawn
		}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{
			map[string]any{"key": key, "acceptQuest": intent},
		}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	refused := func(label, key, accepterPawn string, rewardChoice int, code string) error {
		result, err := apply(label, key, accepterPawn, rewardChoice)
		if err != nil {
			return err
		}
		refusal, _ := na.AsMap(result["refused"])
		if refusal == nil || na.AsString(refusal["code"]) != code || na.AsString(refusal["reason"]) == "" {
			return fmt.Errorf("%s: expected a %s refusal with a reason, got %#v", label, code, result)
		}
		return nil
	}
	acceptedQuest := func(label string, result map[string]any) error {
		receipt, _ := na.AsMap(result["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		quest, _ := na.AsMap(observed["quest"])
		if na.AsString(quest["questId"]) != questID {
			return fmt.Errorf("%s: expected an applied receipt for the quest, got %#v", label, result)
		}
		if accepted, _ := na.AsBool(quest["accepted"]); !accepted {
			return fmt.Errorf("%s: expected the quest to be accepted, got %#v", label, quest)
		}
		return nil
	}

	// Refusal 1: a reward option outside the quest's single choice part.
	if err := refused("reward-out-of-range", "quest-accept-reward", "", 5, "FAILURE_CODE_INVALID_REQUEST"); err != nil {
		return err
	}
	// Refusal 2: this quest does not require an accepter; supplying one
	// anyway is refused rather than silently ignored.
	if err := refused("unwanted-accepter", "quest-accept-unwanted-accepter", pawnIDs[0], 0, "FAILURE_CODE_INVALID_REQUEST"); err != nil {
		return err
	}
	if row, err := target("target-after-refusals"); err != nil {
		return err
	} else if na.AsString(row["state"]) != "QUEST_STATE_NOT_YET_ACCEPTED" && na.AsString(row["state"]) != "NotYetAccepted" {
		return fmt.Errorf("target-after-refusals: a refused intent changed the quest: %#v", row)
	}

	// Apply: the real native Quest.Accept settings write and its
	// QuestPart_Choice.Choose call.
	result, err := apply("apply", "quest-accept", "", 0)
	if err != nil {
		return err
	}
	if err = acceptedQuest("apply", result); err != nil {
		return err
	}
	questRowAccepted, err := target("target-after-apply")
	if err != nil {
		return err
	}
	if na.AsString(questRowAccepted["state"]) == "QUEST_STATE_NOT_YET_ACCEPTED" || na.AsString(questRowAccepted["state"]) == "NotYetAccepted" {
		return fmt.Errorf("target-after-apply: expected the quest to have left NotYetAccepted, got %#v", questRowAccepted)
	}
	if canAccept, _ := na.AsBool(questRowAccepted["canAccept"]); canAccept {
		return fmt.Errorf("target-after-apply: expected canAccept=false for an already-accepted quest, got %#v", questRowAccepted)
	}

	// Replay: the same key returns the identical result without a second
	// native write.
	replay, err := apply("replay", "quest-accept", "", 0)
	if err != nil {
		return err
	}
	if !na.DeepEqual(replay, result) {
		return fmt.Errorf("replay: the same key returned a different result: %#v", replay)
	}

	// A fresh key against the accepted quest is applied again: the intent
	// already holds, so a resend after a lost reply cannot fail it.
	again, err := apply("apply-again", "quest-accept-again", "", 0)
	if err != nil {
		return err
	}
	if err = acceptedQuest("apply-again", again); err != nil {
		return err
	}

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// The lifecycle/load case is the native-acceptance run for G01.09's async
// native load slice: a setup checkpoint via
// the already-landed rimgovernor/lifecycle_save, then a rimgovernor/
// lifecycle_load of that save polled through rimgovernor/lifecycle_read_load
// to LoadCompleted with map_ready true and a freshly issued load token, a
// second load of the same save requesting VISUAL readiness and reaching a
// completed VISUAL outcome, plus a rejection case (ReadLoad for an unknown
// request id). It never exercises reconnect-after-disconnect or
// competing-viewer arbitration, which remain separate, unimplemented
// capability, and permanently out of scope for this item.
package lifecycle

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
		Name:   "lifecycle/load",
		Scope:  "Trusted native rimgovernor/lifecycle_load: a setup checkpoint save, an async MAP-readiness load of that save polled via rimgovernor/lifecycle_read_load to LoadCompleted with map_ready and a fresh load token, a second VISUAL-readiness load of the same save reaching a completed VISUAL outcome, and an unknown-request-id rejection. No reconnect/competing-viewer capability exercised.",
		Start:  cases.DebugStart{},
		Quiet:  na.QuietIfAvailable,
		Reason: "lifecycle_load is a production tool; the case also runs on a production build, which carries no quiet fixture",
		Budget: 5 * time.Minute,
		Run:    runLoad,
	})
}

func runLoad(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, names := s.Harness(), s.Names()
	if !na.Contains(names, "rimgovernor/lifecycle_load") {
		return fmt.Errorf("missing rimgovernor/lifecycle_load in discovery")
	}
	if !na.Contains(names, "rimgovernor/lifecycle_read_load") {
		return fmt.Errorf("missing rimgovernor/lifecycle_read_load in discovery")
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(loaded["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	loadedContext, _ := loaded["context"].(map[string]any)
	identity, _ := loadedContext["identity"].(map[string]any)
	originalColonyID := na.AsString(identity["colonyId"])
	originalLoadToken := na.AsString(identity["loadToken"])
	if originalColonyID == "" || originalLoadToken == "" {
		return fmt.Errorf("missing colony id or load token before setup save")
	}

	// Setup: a trusted checkpoint save this run then loads back.
	setupSaveName := fmt.Sprintf("loadaccept-checkpoint-%d", time.Now().UnixNano())
	setupReply, err := h.Wire(ctx, "setup-save", "lifecycle_save", map[string]any{
		"player": map[string]any{
			"identity":        identity,
			"playerDirection": 1,
			"requestId":       fmt.Sprintf("loadaccept-setup-%d", time.Now().UnixNano()),
		},
		"saveName": setupSaveName,
	})
	if err != nil {
		return fmt.Errorf("setup-save: %w", err)
	}
	if _, _, err := na.Outcome(setupReply, "completed"); err != nil {
		return fmt.Errorf("setup-save: expected a completed save: %w", err)
	}
	report["setup_save"] = setupSaveName

	// Case 1: happy path load. Start the load; native may complete synchronously
	// (unlikely, but the reply shape allows it) or return LoadPending, in which
	// case poll ReadLoad until a definite outcome.
	loadRequestID := fmt.Sprintf("loadaccept-happy-%d", time.Now().UnixNano())
	loadReply, err := h.Wire(ctx, "load-start", "lifecycle_load", map[string]any{
		"requestId": loadRequestID,
		"saveName":  setupSaveName,
		"readiness": "READINESS_MAP",
		"expectedPlayer": map[string]any{
			"identity":        identity,
			"playerDirection": 1,
			"requestId":       loadRequestID,
		},
		"playerDirection": 1,
	})
	if err != nil {
		return fmt.Errorf("load-start: %w", err)
	}
	completed, err := na.PollLoad(ctx, h, loadReply, loadRequestID, "load-poll")
	if err != nil {
		return fmt.Errorf("load-happy: %w", err)
	}
	loadedIdentity, _ := na.AsMap(completed["loaded"])
	loadedContextAfter, _ := na.AsMap(loadedIdentity["context"])
	afterIdentity, _ := na.AsMap(loadedContextAfter["identity"])
	if na.AsString(afterIdentity["colonyId"]) != originalColonyID {
		return fmt.Errorf("load-happy: loaded colony id does not match the saved colony")
	}
	if na.AsString(afterIdentity["loadToken"]) == originalLoadToken || na.AsString(afterIdentity["loadToken"]) == "" {
		return fmt.Errorf("load-happy: loaded map did not receive a fresh load token")
	}
	if na.AsString(completed["saveName"]) != setupSaveName {
		return fmt.Errorf("load-happy: completed save name mismatch")
	}
	if na.AsString(completed["requestId"]) != loadRequestID {
		return fmt.Errorf("load-happy: completed request id mismatch")
	}
	if na.AsString(completed["readiness"]) != "READINESS_MAP" {
		return fmt.Errorf("load-happy: expected READINESS_MAP, got %q", completed["readiness"])
	}
	report["case_happy"] = map[string]any{"saveName": setupSaveName, "colonyId": originalColonyID, "newLoadToken": afterIdentity["loadToken"]}

	// Case 1b: a VISUAL-readiness load of the same save must not complete
	// until the map has actually been drawn -- not merely MAP-ready with data
	// in memory. The debug game was started with readiness "visual" above (so
	// it is already rendering), so this load should still reach a completed
	// VISUAL outcome within the polling budget.
	visualRequestID := fmt.Sprintf("loadaccept-visual-%d", time.Now().UnixNano())
	visualLoadReply, err := h.Wire(ctx, "load-visual-start", "lifecycle_load", map[string]any{
		"requestId": visualRequestID,
		"saveName":  setupSaveName,
		"readiness": "READINESS_VISUAL",
		"expectedPlayer": map[string]any{
			"identity":        afterIdentity,
			"playerDirection": 1,
			"requestId":       visualRequestID,
		},
		"playerDirection": 1,
	})
	if err != nil {
		return fmt.Errorf("load-visual-start: %w", err)
	}
	visualCompleted, err := na.PollLoad(ctx, h, visualLoadReply, visualRequestID, "load-visual-poll")
	if err != nil {
		return fmt.Errorf("load-visual: %w", err)
	}
	if na.AsString(visualCompleted["readiness"]) != "READINESS_VISUAL" {
		return fmt.Errorf("load-visual: expected READINESS_VISUAL, got %q", visualCompleted["readiness"])
	}
	report["case_visual_readiness"] = true

	// Case 2: ReadLoad for an unknown/expired request id must be refused, not
	// silently treated as pending forever or completed.
	unknownReply, err := h.Wire(ctx, "read-load-unknown", "lifecycle_read_load", map[string]any{
		"requestId": fmt.Sprintf("loadaccept-unknown-%d", time.Now().UnixNano()),
	})
	if err != nil {
		return fmt.Errorf("read-load-unknown: %w", err)
	}
	if code, ok := na.FailureCode(unknownReply); !ok || code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("read-load-unknown: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}
	report["case_unknown_request_id"] = true

	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), s.Config().Headless); err != nil {
		return err
	}
	return nil
}

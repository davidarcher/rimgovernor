// Command draftfaultaccept live-proves NativeDraftOperations.Apply's setter-fault
// path (integrations/rimgovernor-native/src/Bridge/Protocol/NativeDraftOperations.cs):
// an admitted SetDrafted attempt whose live Pawn_DraftController.Drafted setter itself
// throws (as opposed to being refused before any native effect runs) must report an
// Uncertain receipt, leave the pawn's real drafted state untouched, and resolve to an
// Unknown observation until a later legitimate attempt succeeds normally. Before this,
// no fixture in this repo could force the native setter/transport path itself to fail;
// the fault is injected by scripts/fixtures/DraftFaultFixture.cs, a disposable
// dev-only Harmony patch (a second, independently owned prefix on the very same
// Pawn_DraftController.Drafted setter NativeAuthorityHooks already patches, following
// DraftOwnership.cs's own established pattern) that throws once for one armed pawn and
// self-clears the instant it fires.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-draft-fault-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 10*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-draft-fault-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("A fixture-armed live Pawn_DraftController.Drafted setter fault during an admitted "+
		"SetDrafted attempt reports an Uncertain receipt, leaves the pawn's real drafted state untouched, and "+
		"resolves to an Unknown observation, after which a fresh legitimate attempt succeeds normally.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
	if err != nil {
		return err
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
	h := na.NewHarness(client, output)

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, initial, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(initial["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	initialContext, _ := na.AsMap(initial["context"])
	identity, _ := na.AsMap(initialContext["identity"])

	read := func(label, pawnID string) (map[string]any, error) {
		filter := map[string]any{"colonist": true, "downed": false}
		if pawnID != "" {
			filter["ids"] = []any{pawnID}
		}
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "filter": filter,
		})
		if err != nil {
			return nil, err
		}
		row, err := na.PawnRow(reply, identity, pawnID)
		if err != nil {
			return nil, fmt.Errorf("%s: %w", label, err)
		}
		pawn, _ := na.AsMap(row["pawn"])
		snapshot, _ := na.AsMap(pawn["snapshot"])
		snapshotContext, _ := na.AsMap(snapshot["context"])
		if !na.DeepEqual(snapshotContext["tick"], initialContext["tick"]) {
			return nil, fmt.Errorf("%s: pawn snapshot tick drifted from the initial paused tick", label)
		}
		return row, nil
	}

	before, err := read("initial-pawn", "")
	if err != nil {
		return err
	}
	if drafted, _ := before["drafted"].(bool); drafted {
		return fmt.Errorf("initial pawn is unexpectedly already drafted")
	}
	pawn, _ := na.AsMap(before["pawn"])
	pawnID := na.AsString(pawn["id"])

	idle, err := h.Call(ctx, "idle-fixture", "test/b04f_setup", map[string]any{"op": "idle-pawn", "pawn": pawnID})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(idle["success"]); !success {
		return fmt.Errorf("idle-pawn fixture refused")
	}
	before, err = read("idle-pawn", pawnID)
	if err != nil {
		return err
	}

	statusReply, err := h.Wire(ctx, "status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return err
	}
	statusContext, _ := na.AsMap(status["context"])
	grantReply, err := h.Wire(ctx, "acquire", "authority_control", map[string]any{"acquire": map[string]any{
		"identity": identity, "expectedGeneration": statusContext["nativeGeneration"], "owner": na.Owner, "leaseMs": 30000,
	}})
	if err != nil {
		return err
	}
	_, grant, err := na.Outcome(grantReply, "granted")
	if err != nil {
		return err
	}

	// Arm the fixture-only setter fault for this exact pawn, then attempt an admitted
	// SetDrafted(true): the live Pawn_DraftController.Drafted setter itself must throw
	// before any field write, distinct from every refusal this family already covers
	// (which all refuse before admission/before the setter is ever reached).
	armed, err := h.Call(ctx, "arm-fault", "test/draft_fault_arm", map[string]any{"pawn": pawnID})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(armed["armed"]); !success {
		return fmt.Errorf("draft-fault arm refused: %#v", armed)
	}

	request := na.ExecuteRequest(identity, grant, before, 1)
	faultReply, err := h.Wire(ctx, "fault-attempt", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(faultReply, "receipt")
	if err != nil {
		return err
	}
	// Receipt carries attempt/admittedContext/authorizingOwner alongside its own
	// outcome oneof (applied/noChange/uncertain/...), so this checks the "uncertain"
	// field directly rather than via Outcome (which expects a single-field oneof).
	if _, isApplied := receipt["applied"]; isApplied {
		return fmt.Errorf("fault-attempt: expected an uncertain receipt, got applied: %#v", receipt)
	}
	if _, isNoChange := receipt["noChange"]; isNoChange {
		return fmt.Errorf("fault-attempt: expected an uncertain receipt, got noChange: %#v", receipt)
	}
	uncertainBody, ok := na.AsMap(receipt["uncertain"])
	if !ok {
		return fmt.Errorf("fault-attempt: expected an uncertain receipt: %#v", receipt)
	}
	detail := na.AsString(uncertainBody["detail"])
	if !strings.Contains(detail, "DraftFaultInjectedException") {
		return fmt.Errorf("fault-attempt: uncertain detail does not attribute the injected setter fault: %q", detail)
	}
	lastObserved, _ := na.AsMap(uncertainBody["lastObserved"])
	lastJob, _ := na.AsMap(lastObserved["job"])
	if issued, _ := lastJob["issued"].(bool); !issued {
		return fmt.Errorf("fault-attempt: expected the setter to have been issued before it faulted: %#v", lastJob)
	}
	if verified, _ := lastJob["verified"].(bool); verified {
		return fmt.Errorf("fault-attempt: expected the faulted setter to be unverified: %#v", lastJob)
	}
	if drafted, _ := lastJob["drafted"].(bool); drafted {
		return fmt.Errorf("fault-attempt: expected the faulted setter's last observed job to still show undrafted: %#v", lastJob)
	}

	// The injected fault must have prevented the real setter from ever running: the
	// pawn is still genuinely undrafted with no owned claim. (Its CAS snapshot token
	// legitimately still rotates: any attempted native draft interaction advances the
	// pawn's own draft revision whether or not the attempt itself later fails, exactly
	// as an uncertain lease-expiry outcome elsewhere in this family also rotates the
	// token without changing drafted/ownership facts.)
	afterFault, err := read("after-fault", pawnID)
	if err != nil {
		return err
	}
	if drafted, _ := afterFault["drafted"].(bool); drafted {
		return fmt.Errorf("after-fault: pawn is unexpectedly drafted despite the injected setter fault")
	}
	if !na.DeepEqual(afterFault["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("after-fault: pawn unexpectedly has an owned draft claim despite the injected setter fault: %#v", afterFault["draftClaim"])
	}

	// The one-shot fault must have self-cleared: a disarm attempt now reports nothing
	// was armed, and it did not leak into arming any other pawn/attempt.
	disarmed, err := h.Call(ctx, "disarm-after-fault", "test/draft_fault_disarm", map[string]any{})
	if err != nil {
		return err
	}
	if wasArmed, _ := na.AsBool(disarmed["wasArmed"]); wasArmed {
		return fmt.Errorf("draft-fault fixture did not self-clear after firing")
	}

	precondition, _ := na.AsMap(request["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	progressReply, err := h.Wire(ctx, "progress-after-fault", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, progress, err := na.Outcome(progressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(progress["completeInspection"]); complete {
		return fmt.Errorf("progress-after-fault: expected an incomplete inspection (no verified outcome), got a complete one: %#v", progress)
	}
	unknown, ok := na.AsMap(progress["unknown"])
	if !ok {
		return fmt.Errorf("progress-after-fault: expected an unknown progress outcome, got %#v", progress)
	}
	if reason := na.AsString(unknown["reason"]); !strings.Contains(reason, "No causally verified draft outcome") {
		return fmt.Errorf("progress-after-fault: unexpected unknown reason: %q", reason)
	}

	// Recovery: a fresh legitimate attempt against the same (still unowned, still
	// untouched) pawn must succeed normally, proving the injected fault left no
	// lingering damage to the operation, the ledger, or the pawn.
	recoveryGrant := grant
	recoveryReply, err := h.Wire(ctx, "recovery-attempt", "operations_execute", na.ExecuteRequest(identity, recoveryGrant, afterFault, 2))
	if err != nil {
		return err
	}
	_, recoveryReceipt, err := na.Outcome(recoveryReply, "receipt")
	if err != nil {
		return err
	}
	drafted, err := read("recovered", pawnID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(recoveryReceipt, drafted, "applied", true); err != nil {
		return fmt.Errorf("recovery-attempt: %w", err)
	}

	// Clean up: revoke authority (Manual) and release the owned draft, mirroring
	// draftaccept's own teardown, so the run leaves no owned claim behind.
	revokeGrantContext, _ := na.AsMap(recoveryGrant["context"])
	revokeReply, err := h.Wire(ctx, "manual-revoke", "authority_control", map[string]any{"revoke": map[string]any{
		"identity": identity, "expectedGeneration": revokeGrantContext["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(revokeReply, "revoked"); err != nil {
		return err
	}
	fresh, err := read("after-manual", pawnID)
	if err != nil {
		return err
	}
	cleanup, err := na.ReleaseRequest(identity, fresh)
	if err != nil {
		return err
	}
	releasedReply, err := h.Wire(ctx, "cleanup", "operations_release_owned_draft", cleanup)
	if err != nil {
		return err
	}
	_, released, err := na.Outcome(releasedReply, "released")
	if err != nil {
		return err
	}
	releasedObserved, _ := na.AsMap(released["observed"])
	if verified, _ := releasedObserved["verified"].(bool); !verified {
		return fmt.Errorf("cleanup was not verified")
	}
	if drafted, _ := releasedObserved["drafted"].(bool); drafted {
		return fmt.Errorf("cleanup did not clear drafted")
	}
	undrafted, err := read("after-cleanup", pawnID)
	if err != nil {
		return err
	}
	if drafted, _ := undrafted["drafted"].(bool); drafted {
		return fmt.Errorf("pawn is still drafted after cleanup")
	}

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, final, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(final["paused"]); !paused {
		return fmt.Errorf("game unexpectedly resumed")
	}
	finalContext, _ := na.AsMap(final["context"])
	if !na.DeepEqual(finalContext["identity"], identity) {
		return fmt.Errorf("identity changed during the run")
	}
	if !na.DeepEqual(finalContext["tick"], initialContext["tick"]) {
		return fmt.Errorf("tick advanced during a paused run")
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	report["initial_context"] = initialContext
	report["final_context"] = finalContext
	report["pawn_id"] = pawnID
	return nil
}

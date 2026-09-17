// Command draftaccept proves the full disposable-
// worker lifecycle plus paused real native draft CAS, owned claims, replay/no-op,
// Manual cleanup, player override refusal, and real ordinary-ledger exhaustion with
// owned-cleanup-beyond-exhaustion. Capacity refusal's own boundary remains covered by
// compiled ledger tests (contracts/tests/native-attempt-ledger), not injected native
// outcomes; this only proves cleanup remains possible after real exhaustion.
//
// Also proves the general "lost reply" recovery mechanism live: after Manual
// revocation and owned-cleanup have already run (zero active authority anywhere),
// rimgovernor/receipts_lookup keyed only by the original attempt still recovers
// the exact original committed receipt, matching its own documented contract
// ("without requiring current authority") and docs/developers/architecture/
// plans-and-hands.md's recovery guidance for a lost reply ("retain intent and
// inspect the game before retrying"). No new fixture is needed: the ledger already
// commits the receipt keyed by attempt independent of any later authority state,
// so a caller whose original reply never arrived can always recover the true
// outcome by attempt key alone, without redispatching the operation.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const ledgerCapacity = 4096

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-draft-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 1800*time.Second, "overall run timeout (ledger exhaustion adds ~4k bounded round trips)")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-draft-acceptance"
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
	report := na.NewReport("Paused real native draft CAS, owned claims, replay/no-op, Manual cleanup and player "+
		"override refusal, plus real ordinary-ledger exhaustion and owned-cleanup-beyond-exhaustion. Capacity "+
		"refusal's own boundary remains covered by compiled ledger tests, not injected native outcomes.", !*rendered)
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
	held, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer held.Close(report)
	client := held.Client
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

	// Authority is SetMode(Auto|Manual)/Revoke plus generation continuity
	// (#52): grant returns the "granted" body whose context.nativeGeneration
	// every later WritePrecondition carries; there is no lease to renew.
	grantAuto := func(label string) (map[string]any, error) {
		return na.GrantAuto(ctx, h.WireFunc(), label, identity)
	}
	revoke := func(label string, grant map[string]any) error {
		_, err := na.RevokeManual(ctx, h.WireFunc(), label, identity, grant)
		return err
	}

	before, err := read("initial-pawn", "")
	if err != nil {
		return err
	}
	if drafted, _ := before["drafted"].(bool); drafted {
		return fmt.Errorf("initial pawn is unexpectedly already drafted")
	}
	if !na.DeepEqual(before["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("initial pawn does not have an unowned draft claim")
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
	if absent, _ := na.AsBool(idle["currentJobAbsent"]); !absent {
		return fmt.Errorf("idle-pawn fixture did not clear the current job")
	}
	if queued := na.AsNumber(idle["queuedJobs"]); queued != 0 {
		return fmt.Errorf("idle-pawn fixture left queued jobs")
	}
	before, err = read("idle-pawn", pawnID)
	if err != nil {
		return err
	}
	beforeJob, _ := na.AsMap(before["job"])
	if playerForced, _ := na.AsBool(beforeJob["playerForced"]); playerForced {
		return fmt.Errorf("idle pawn unexpectedly has playerForced set")
	}
	if queued := na.AsNumber(beforeJob["queuedJobs"]); queued != 0 {
		return fmt.Errorf("idle pawn unexpectedly has queued jobs")
	}
	if _, present := beforeJob["defName"]; present {
		return fmt.Errorf("idle pawn unexpectedly has a job defName")
	}
	if _, present := beforeJob["loadId"]; present {
		return fmt.Errorf("idle pawn unexpectedly has a job loadId")
	}

	grant, err := grantAuto("grant")
	if err != nil {
		return err
	}
	request := na.ExecuteRequest(identity, grant, before, 1)
	receiptReply, err := h.Wire(ctx, "draft", "operations_execute", request)
	if err != nil {
		return err
	}
	_, receipt, err := na.Outcome(receiptReply, "receipt")
	if err != nil {
		return err
	}
	drafted, err := read("drafted", pawnID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(receipt, drafted, "applied", true); err != nil {
		return err
	}
	if na.DeepEqual(na.Target(before), na.Target(drafted)) {
		return fmt.Errorf("draft did not rotate the pawn's snapshot token")
	}
	precondition, _ := na.AsMap(request["precondition"])
	attempt := map[string]any{"identity": identity, "attempt": precondition["attempt"]}
	progressReply, err := h.Wire(ctx, "progress", "receipts_observe_progress", attempt)
	if err != nil {
		return err
	}
	_, progress, err := na.Outcome(progressReply, "progress")
	if err != nil {
		return err
	}
	if complete, _ := na.AsBool(progress["completeInspection"]); !complete {
		return fmt.Errorf("progress read is not a complete inspection")
	}
	completed, ok := na.AsMap(progress["completed"])
	if !ok {
		return fmt.Errorf("expected a completed draft progress")
	}
	completedEvidence, _ := na.AsMap(completed["evidence"])
	completedJob, _ := na.AsMap(completedEvidence["job"])
	draftedClaim, _ := na.AsMap(drafted["draftClaim"])
	draftedOwned, _ := na.AsMap(draftedClaim["owned"])
	if na.AsString(completedJob["draftClaimId"]) != na.AsString(draftedOwned["claimId"]) {
		return fmt.Errorf("completed progress evidence does not match the drafted claim")
	}

	replayReply, err := h.Wire(ctx, "replay", "operations_execute", request)
	if err != nil {
		return err
	}
	_, replayReceipt, err := na.Outcome(replayReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replayReceipt, receipt) {
		return fmt.Errorf("replay of the same attempt returned a different receipt")
	}
	replayRow, err := read("replay-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(drafted, replayRow); err != nil {
		return fmt.Errorf("replay: %w", err)
	}
	lookupReply, err := h.Wire(ctx, "lookup", "receipts_lookup", attempt)
	if err != nil {
		return err
	}
	_, lookupReceipt, err := na.Outcome(lookupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookupReceipt, receipt) {
		return fmt.Errorf("receipts_lookup returned a different receipt than the original execute")
	}

	conflict := deepCopyMap(request)
	conflictOperation, _ := na.AsMap(conflict["operation"])
	conflictSetDrafted, _ := na.AsMap(conflictOperation["setDrafted"])
	conflictSetDrafted["drafted"] = false
	if code, err := failureCode(ctx, h, "attempt-conflict", conflict); err != nil {
		return err
	} else if code != "FAILURE_CODE_ATTEMPT_CONFLICT" {
		return fmt.Errorf("attempt-conflict: expected FAILURE_CODE_ATTEMPT_CONFLICT, got %q", code)
	}

	malformed := na.ExecuteRequest(identity, grant, drafted, 99)
	malformedOperation, _ := na.AsMap(malformed["operation"])
	malformedSetDrafted, _ := na.AsMap(malformedOperation["setDrafted"])
	delete(malformedSetDrafted, "drafted")
	if code, err := failureCode(ctx, h, "missing-drafted-presence", malformed); err != nil {
		return err
	} else if code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("missing-drafted-presence: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}
	refusalsUnchanged, err := read("refusals-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(drafted, refusalsUnchanged); err != nil {
		return fmt.Errorf("refusals-unchanged: %w", err)
	}

	noOpReply, err := h.Wire(ctx, "owned-no-op", "operations_execute", na.ExecuteRequest(identity, grant, drafted, 2))
	if err != nil {
		return err
	}
	_, noOp, err := na.Outcome(noOpReply, "receipt")
	if err != nil {
		return err
	}
	unchanged, err := read("no-op-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(noOp, unchanged, "noChange", false); err != nil {
		return err
	}
	if err := na.SameControl(drafted, unchanged); err != nil {
		return fmt.Errorf("no-op: %w", err)
	}

	if code, err := failureCode(ctx, h, "stale-snapshot", na.ExecuteRequest(identity, grant, before, 3)); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" {
		return fmt.Errorf("stale-snapshot: expected FAILURE_CODE_OWNER_CONFLICT, got %q", code)
	}
	staleUnchanged, err := read("stale-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(drafted, staleUnchanged); err != nil {
		return fmt.Errorf("stale-unchanged: %w", err)
	}

	if err := revoke("manual", grant); err != nil {
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
	if !na.DeepEqual(released["request"], cleanup) {
		return fmt.Errorf("release reply did not echo the cleanup request")
	}
	releasedObserved, _ := na.AsMap(released["observed"])
	if drafted, _ := releasedObserved["drafted"].(bool); drafted {
		return fmt.Errorf("release did not clear drafted")
	}
	if verified, _ := releasedObserved["verified"].(bool); !verified {
		return fmt.Errorf("release was not verified")
	}
	if issued, _ := releasedObserved["issued"].(bool); !issued {
		return fmt.Errorf("release was not issued")
	}
	undrafted, err := read("after-cleanup", pawnID)
	if err != nil {
		return err
	}
	if drafted, _ := undrafted["drafted"].(bool); drafted {
		return fmt.Errorf("pawn is still drafted after cleanup")
	}
	if !na.DeepEqual(undrafted["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("pawn does not have an unowned draft claim after cleanup")
	}
	if na.AsString(releasedObserved["resultingSnapshotToken"]) != na.AsString(na.Target(undrafted)["expectedSnapshotToken"]) {
		return fmt.Errorf("release resultingSnapshotToken does not match the post-cleanup snapshot")
	}
	retryReply, err := h.Wire(ctx, "cleanup-retry", "operations_release_owned_draft", cleanup)
	if err != nil {
		return err
	}
	_, retry, err := na.Outcome(retryReply, "alreadyReleased")
	if err != nil {
		return err
	}
	if !na.DeepEqual(retry["request"], cleanup) {
		return fmt.Errorf("cleanup-retry did not echo the cleanup request")
	}
	retryObserved, _ := na.AsMap(retry["observed"])
	if issued, _ := retryObserved["issued"].(bool); issued {
		return fmt.Errorf("cleanup-retry unexpectedly issued")
	}
	if verified, _ := retryObserved["verified"].(bool); !verified {
		return fmt.Errorf("cleanup-retry was not verified")
	}
	cleanupRetryUnchanged, err := read("cleanup-retry-unchanged", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(undrafted, cleanupRetryUnchanged); err != nil {
		return fmt.Errorf("cleanup-retry-unchanged: %w", err)
	}
	replayAfterCleanupReply, err := h.Wire(ctx, "replay-after-cleanup", "operations_execute", request)
	if err != nil {
		return err
	}
	_, replayAfterCleanup, err := na.Outcome(replayAfterCleanupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(replayAfterCleanup, receipt) {
		return fmt.Errorf("replay-after-cleanup returned a different receipt than the original execute")
	}

	// Lost-reply recovery: rimgovernor/receipts_lookup is documented ("Read
	// original admitted receipt without requiring current authority") and
	// plans-and-hands.md names exactly this pattern for a lost reply ("A lost
	// reply after dispatch may conceal an accepted order: retain intent and
	// inspect the game before retrying"). The earlier "lookup" step above only
	// proved receipts_lookup echoes the original receipt while the authorizing
	// Auto grant was still active. Here authority has since been fully revoked
	// (Manual, above) and the owned claim already released and cleaned up --
	// so a lookup keyed only by the original attempt, with zero live authority
	// anywhere in hand, still recovers the exact original committed receipt.
	// This proves a caller whose original reply never arrived can recover the
	// true outcome from the attempt key alone, without redispatching the
	// operation and without needing any authority at all -- the general,
	// family-agnostic "lost reply" recovery mechanism, live and beyond the
	// shared envelope gate's own compiled-only coverage.
	lookupAfterCleanupReply, err := h.Wire(ctx, "lookup-after-cleanup", "receipts_lookup", attempt)
	if err != nil {
		return err
	}
	_, lookupAfterCleanup, err := na.Outcome(lookupAfterCleanupReply, "receipt")
	if err != nil {
		return err
	}
	if !na.DeepEqual(lookupAfterCleanup, receipt) {
		return fmt.Errorf("lookup-after-cleanup returned a different receipt than the original execute")
	}

	// There is no authority-lease expiry case any more: #52 removed the timed
	// lease (REVOCATION_REASON_LEASE_EXPIRED no longer exists). The only
	// native-observable lapse is a typed clock lease running out, which needs
	// the simulation to tick and is covered by cmd/disconnectaccept and
	// cmd/movementaccept; every read here asserts the paused tick is unchanged.
	// Owned cleanup after authority is gone is already proven by the Manual
	// section above.

	// Player-order override: an owned draft must be dropped when the (simulated)
	// player issues a direct order, and the stale owned cleanup for it must be refused.
	grant, err = grantAuto("override-grant")
	if err != nil {
		return err
	}
	secondReceiptReply, err := h.Wire(ctx, "second-draft", "operations_execute", na.ExecuteRequest(identity, grant, undrafted, 4))
	if err != nil {
		return err
	}
	_, second, err := na.Outcome(secondReceiptReply, "receipt")
	if err != nil {
		return err
	}
	owned, err := read("before-player-order", pawnID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(second, owned, "applied", true); err != nil {
		return err
	}
	obsoleteCleanup, err := na.ReleaseRequest(identity, owned)
	if err != nil {
		return err
	}
	external, err := h.Call(ctx, "external-order", "test/b04f_setup", map[string]any{"op": "external-order", "pawn": pawnID})
	if err != nil {
		return err
	}
	overridden, err := read("after-player-order", pawnID)
	if err != nil {
		return err
	}
	if err := na.ActualOrder(external, overridden); err != nil {
		return err
	}
	if !na.DeepEqual(overridden["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("player order did not clear the draft claim")
	}
	if na.DeepEqual(na.Target(overridden), na.Target(owned)) {
		return fmt.Errorf("player order did not rotate the snapshot token")
	}
	refusalReply, err := h.Wire(ctx, "refuse-old-cleanup", "operations_release_owned_draft", obsoleteCleanup)
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(refusalReply, "failure"); err != nil {
		return fmt.Errorf("refuse-old-cleanup: %w", err)
	}
	preserved, err := read("player-order-preserved", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(overridden, preserved); err != nil {
		return fmt.Errorf("player-order-preserved: %w", err)
	}
	if err := na.ActualOrder(external, preserved); err != nil {
		return err
	}
	playerEffect, err := h.Call(ctx, "player-draft", "test/b04f_setup", map[string]any{"op": "external-draft", "pawn": pawnID, "drafted": true})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(playerEffect["success"]); !success {
		return fmt.Errorf("player-draft fixture refused")
	}
	if after, _ := playerEffect["after"].(bool); !after {
		return fmt.Errorf("player-draft fixture did not draft the pawn")
	}
	player, err := read("player-drafted", pawnID)
	if err != nil {
		return err
	}
	if drafted, _ := player["drafted"].(bool); !drafted {
		return fmt.Errorf("player is not drafted after the player-draft fixture")
	}
	if !na.DeepEqual(player["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("player draft is unexpectedly owned")
	}
	grant, err = grantAuto("unowned-grant")
	if err != nil {
		return err
	}
	if code, err := failureCode(ctx, h, "refuse-adoption", na.ExecuteRequest(identity, grant, player, 5)); err != nil {
		return err
	} else if code != "FAILURE_CODE_OWNER_CONFLICT" {
		return fmt.Errorf("refuse-adoption: expected FAILURE_CODE_OWNER_CONFLICT, got %q", code)
	}
	unownedPreserved, err := read("unowned-preserved", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(player, unownedPreserved); err != nil {
		return fmt.Errorf("unowned-preserved: %w", err)
	}

	// Ordinary ledger exhaustion. Capacity refusal itself is proven by compiled
	// reflection tests; this proves the design invariant that exact owned draft
	// cleanup remains possible after real admission capacity is exhausted, not
	// merely simulated at the ledger's own boundary.
	reclaim, err := h.Call(ctx, "ledger-undraft", "test/b04f_setup", map[string]any{"op": "external-draft", "pawn": pawnID, "drafted": false})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(reclaim["success"]); !success {
		return fmt.Errorf("ledger-undraft fixture refused")
	}
	if after, _ := reclaim["after"].(bool); after {
		return fmt.Errorf("ledger-undraft fixture did not clear drafted")
	}
	ledgerBefore, err := read("ledger-before", pawnID)
	if err != nil {
		return err
	}
	ledgerGrant, err := grantAuto("ledger-grant")
	if err != nil {
		return err
	}
	ledgerRequest := na.ExecuteRequest(identity, ledgerGrant, ledgerBefore, 7)
	ledgerReceiptReply, err := h.Wire(ctx, "ledger-draft", "operations_execute", ledgerRequest)
	if err != nil {
		return err
	}
	_, ledgerReceipt, err := na.Outcome(ledgerReceiptReply, "receipt")
	if err != nil {
		return err
	}
	ledgerOwned, err := read("ledger-owned", pawnID)
	if err != nil {
		return err
	}
	if err := na.OwnedEffect(ledgerReceipt, ledgerOwned, "applied", true); err != nil {
		return err
	}

	fillRequest := func(n int) map[string]any {
		// Each fill reuses the post-draft (already-owned) snapshot token, matching
		// the same-pawn no-change path exercised once above by "owned-no-op": a
		// stale pre-draft token would be refused as owner conflict, not admitted.
		return na.ExecuteRequest(identity, ledgerGrant, ledgerOwned, 1000+n)
	}

	exhaustedAt := -1
	// Authority no longer lapses on wall-clock time (#52), so the ~4k fill
	// round trips need no periodic renewal; the generation stays put throughout.
	for n := 1; n < ledgerCapacity+16; n++ {
		reply, err := rawWire(ctx, client, "operations_execute", fillRequest(n))
		if err != nil {
			return fmt.Errorf("ledger fill %d: %w", n, err)
		}
		if len(reply) == 1 {
			if failure, ok := na.AsMap(reply["failure"]); ok {
				code := na.AsString(failure["code"])
				if code != "FAILURE_CODE_CAPACITY_EXHAUSTED" {
					return fmt.Errorf("ledger fill %d: unexpected failure %q", n, code)
				}
				exhaustedAt = n
				break
			}
			if _, ok := reply["receipt"]; !ok {
				return fmt.Errorf("ledger fill %d: unexpected reply shape %#v", n, reply)
			}
			continue
		}
		return fmt.Errorf("ledger fill %d: unexpected reply shape %#v", n, reply)
	}
	if exhaustedAt < 0 {
		return fmt.Errorf("ordinary ledger never reported capacity exhaustion")
	}
	if exhaustedAt < 4000 || exhaustedAt >= ledgerCapacity {
		return fmt.Errorf("unexpected real exhaustion point %d", exhaustedAt)
	}
	stillFull, err := rawWire(ctx, client, "operations_execute", fillRequest(exhaustedAt+1))
	if err != nil {
		return err
	}
	if code, ok := na.FailureCode(stillFull); !ok || code != "FAILURE_CODE_CAPACITY_EXHAUSTED" {
		return fmt.Errorf("still-full: expected FAILURE_CODE_CAPACITY_EXHAUSTED, got %q", code)
	}
	ledgerFresh, err := read("ledger-owned-after-fill", pawnID)
	if err != nil {
		return err
	}
	if err := na.SameControl(ledgerOwned, ledgerFresh); err != nil {
		return fmt.Errorf("ledger-owned-after-fill: %w", err)
	}
	ledgerCleanup, err := na.ReleaseRequest(identity, ledgerFresh)
	if err != nil {
		return err
	}
	ledgerReleasedReply, err := h.Wire(ctx, "ledger-cleanup", "operations_release_owned_draft", ledgerCleanup)
	if err != nil {
		return err
	}
	_, ledgerReleased, err := na.Outcome(ledgerReleasedReply, "released")
	if err != nil {
		return err
	}
	if !na.DeepEqual(ledgerReleased["request"], ledgerCleanup) {
		return fmt.Errorf("ledger-cleanup did not echo the cleanup request")
	}
	ledgerReleasedObserved, _ := na.AsMap(ledgerReleased["observed"])
	if drafted, _ := ledgerReleasedObserved["drafted"].(bool); drafted {
		return fmt.Errorf("ledger-cleanup did not clear drafted")
	}
	if verified, _ := ledgerReleasedObserved["verified"].(bool); !verified {
		return fmt.Errorf("ledger-cleanup was not verified")
	}
	if issued, _ := ledgerReleasedObserved["issued"].(bool); !issued {
		return fmt.Errorf("ledger-cleanup was not issued")
	}
	ledgerUndrafted, err := read("ledger-after-cleanup", pawnID)
	if err != nil {
		return err
	}
	if drafted, _ := ledgerUndrafted["drafted"].(bool); drafted {
		return fmt.Errorf("pawn is still drafted after ledger cleanup")
	}
	if !na.DeepEqual(ledgerUndrafted["draftClaim"], map[string]any{"unowned": map[string]any{}}) {
		return fmt.Errorf("pawn does not have an unowned draft claim after ledger cleanup")
	}
	stillExhausted, err := rawWire(ctx, client, "operations_execute", fillRequest(exhaustedAt+2))
	if err != nil {
		return err
	}
	if code, ok := na.FailureCode(stillExhausted); !ok || code != "FAILURE_CODE_CAPACITY_EXHAUSTED" {
		return fmt.Errorf("still-exhausted: expected FAILURE_CODE_CAPACITY_EXHAUSTED, got %q", code)
	}
	report["ledger_exhausted_at"] = exhaustedAt

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

// failureCode wires request through operations_execute and returns the failure code
// of a refused reply, asserting the reply is in fact a failure.
func failureCode(ctx context.Context, h *na.Harness, label string, request map[string]any) (string, error) {
	reply, err := h.Wire(ctx, label, "operations_execute", request)
	if err != nil {
		return "", err
	}
	_, failure, err := na.Outcome(reply, "failure")
	if err != nil {
		return "", fmt.Errorf("%s: %w", label, err)
	}
	return na.AsString(failure["code"]), nil
}

// rawWire calls a rimgovernor/* Protobuf-JSON tool directly against client, bypassing
// Harness evidence recording. Used only for the ~4k-call ledger exhaustion loop, to
// avoid writing one evidence file per fill attempt.
func rawWire(ctx context.Context, client *bridge.Client, method string, request map[string]any) (map[string]any, error) {
	encoded, err := json.Marshal(request)
	if err != nil {
		return nil, err
	}
	args, err := json.Marshal(map[string]any{"request": string(encoded)})
	if err != nil {
		return nil, err
	}
	result, err := client.NativeCall(ctx, "rimgovernor/"+method, args)
	if err != nil {
		return nil, err
	}
	var reply map[string]any
	if err := json.Unmarshal(result.Structured, &reply); err != nil {
		return nil, fmt.Errorf("%s: structuredContent must be an object: %w", method, err)
	}
	payloadValue, ok := reply["payload"].(string)
	if !ok {
		return nil, fmt.Errorf("%s: reply did not preserve the ProtoJSON string envelope", method)
	}
	var message map[string]any
	if err := json.Unmarshal([]byte(payloadValue), &message); err != nil {
		return nil, fmt.Errorf("%s: invalid ProtoJSON reply: %w", method, err)
	}
	return message, nil
}

func deepCopyMap(m map[string]any) map[string]any {
	data, _ := json.Marshal(m)
	var out map[string]any
	_ = json.Unmarshal(data, &out)
	return out
}

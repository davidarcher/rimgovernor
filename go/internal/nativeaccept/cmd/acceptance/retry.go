package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// No captured failure currently establishes an independently transient native
// read. In particular a read timeout in a failed gameplay report is not proof
// that the read caused the failure. Adding a rule requires retained evidence
// and a new policy version. Never classify diagnostic error text for retries.
const retryPolicy = "native-read-v1-empty"

type attemptEvidence struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
}

type caseAttempt struct {
	Case           string           `json:"case"`
	Number         int              `json:"number"`
	Status         string           `json:"status"`
	Classification string           `json:"classification"`
	RetryOf        *int             `json:"retry_of"`
	StartedAt      time.Time        `json:"started_at"`
	FinishedAt     time.Time        `json:"finished_at"`
	Exit           *int             `json:"exit"`
	Error          *string          `json:"error"`
	Evidence       *attemptEvidence `json:"evidence"`
	Log            *attemptEvidence `json:"log"`
}

type retryDecision struct {
	classification string
	eligible       bool
}

func classifyAttempt(row map[string]any) retryDecision {
	if passed, _ := row["passed"].(bool); passed {
		return retryDecision{classification: "none"}
	}
	return retryDecision{classification: "unknown"}
}

type attemptRunner func(context.Context, entry, suiteOptions, string, string, int, io.Writer) map[string]any
type retryClassifier func(map[string]any) retryDecision
type retryCleanup func(context.Context, string, string) error

func runEntry(ctx context.Context, e entry, opts suiteOptions, self, root string, worker int, stderr io.Writer) map[string]any {
	return runEntryAttempts(ctx, e, opts, self, root, worker, stderr, runEntryOnce, classifyAttempt, stopRetryGame)
}

// StopGame's best-effort status wait does not return polling failures. Retry
// requires a positive stopped status, not merely a successful stop request.
func stopRetryGame(ctx context.Context, root, gameID string) error {
	if err := na.StopGame(ctx, root, gameID); err != nil {
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	config := filepath.Join(root, "config-headless")
	if _, err := os.Stat(config); err != nil {
		config = filepath.Join(root, "config")
	}
	gabs, err := na.GABSExecutable(root, config)
	if err != nil {
		return err
	}
	client, err := na.OpenBridgeSession(ctx, gabs, config, gameID, time.Minute)
	if err != nil {
		return err
	}
	defer client.Close()
	reply, err := client.GameStatus(ctx)
	if err != nil {
		return err
	}
	var state struct {
		Status string `json:"status"`
	}
	if err := json.Unmarshal(reply.Structured, &state); err != nil {
		return err
	}
	if state.Status != "stopped" && state.Status != "stale-runtime-cleaned" {
		return fmt.Errorf("retry cleanup did not confirm stopped game: %q", state.Status)
	}
	return ctx.Err()
}

// runEntryAttempts keeps the original row shape as the final projection while
// retaining immutable evidence references for every attempt. Cleanup failure or
// cancellation cannot launch a retry. Both attempts share the suite deadline.
func runEntryAttempts(ctx context.Context, e entry, opts suiteOptions, self, root string, worker int, stderr io.Writer, run attemptRunner, classify retryClassifier, cleanup retryCleanup) map[string]any {
	attempts := make([]caseAttempt, 0, 2)
	row := map[string]any{"name": e.Name, "passed": false}
	var total, boot int64
	for number := 1; number <= 2; number++ {
		if err := ctx.Err(); err != nil {
			row["passed"], row["error"] = false, err.Error()
			break
		}
		current := opts
		if number == 2 {
			current.Resume = false
			current.Output = filepath.Join(opts.Output, "attempts", "2")
		}
		started := time.Now().UTC()
		row = run(ctx, e, current, self, root, worker, stderr)
		exit, hasExit := row["exit"].(int)
		passed, _ := row["passed"].(bool)
		passed = passed && hasExit && exit == 0 && ctx.Err() == nil
		row["passed"] = passed
		decision := classify(row)
		a := caseAttempt{Case: e.Name, Number: number, Status: "failed", Classification: decision.classification, StartedAt: started, FinishedAt: time.Now().UTC()}
		if hasExit {
			a.Exit = &exit
		}
		if number > 1 {
			previous := number - 1
			a.RetryOf = &previous
		}
		if passed {
			a.Status, a.Classification = "passed", "none"
		}
		if err := ctx.Err(); err != nil {
			a.Status, a.Classification = "cancelled", "cancelled"
			if err == context.DeadlineExceeded {
				a.Status, a.Classification = "timed_out", "timeout"
			}
			row["error"] = err.Error()
		}
		if message, ok := row["error"].(string); ok {
			a.Error = &message
		}
		output, _ := row["output"].(string)
		log, _ := row["log"].(string)
		a.Evidence = evidenceReference(opts.Output, filepath.Join(output, "result.json"))
		a.Log = evidenceReference(opts.Output, log)
		attempts = append(attempts, a)
		wall, _ := row["wall_ms"].(int64)
		total += wall
		ms, _ := row["boot_ms"].(int64)
		boot += ms
		if passed || number == 2 || opts.Resume || !decision.eligible || decision.classification != "infrastructure" || ctx.Err() != nil || a.Evidence == nil || a.Log == nil {
			break
		}
		stopCtx, cancel := context.WithTimeout(ctx, 2*time.Minute)
		err := cleanup(stopCtx, root, opts.GameID)
		cancel()
		if err != nil {
			row["retry_cleanup_error"] = err.Error()
			break
		}
		fmt.Fprintf(stderr, "[worker %d] %s retrying fresh after classified infrastructure failure (attempt 2 of 2)\n", worker, e.Name)
	}
	row["retry_policy"], row["attempts"] = retryPolicy, attempts
	row["attempt_count"] = len(attempts)
	row["final_attempt"] = nil
	if len(attempts) > 0 {
		row["final_attempt"] = len(attempts)
	}
	row["wall_ms"], row["boot_ms"] = total, boot
	disposition := "failed"
	if passed, _ := row["passed"].(bool); passed {
		disposition = "passed"
	}
	if len(attempts) > 1 {
		disposition = "retried_" + disposition
	}
	if ctx.Err() != nil {
		disposition = "cancelled"
		if ctx.Err() == context.DeadlineExceeded {
			disposition = "timed_out"
		}
	}
	row["disposition"] = disposition
	return row
}

func evidenceReference(root, path string) *attemptEvidence {
	if path == "" {
		return nil
	}
	rel, err := filepath.Rel(root, path)
	if err != nil || !filepath.IsLocal(rel) {
		return nil
	}
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()
	digest := sha256.New()
	if _, err := io.Copy(digest, f); err != nil {
		return nil
	}
	return &attemptEvidence{Path: filepath.ToSlash(rel), SHA256: hex.EncodeToString(digest.Sum(nil))}
}

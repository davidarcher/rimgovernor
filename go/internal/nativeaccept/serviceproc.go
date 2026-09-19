package nativeaccept

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"path/filepath"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// API issues one HTTP request against the service, recording every exchange
// as a capped evidence row under output/service/http-NNNN.json: NNNN is
// the run's evidence sequence (shared with the harness's native rows, so
// the two streams interleave by number) and the row carries the same
// observed_at and elapsed_ms stamps.
func (p *ServiceProcess) API(method, path string, body map[string]any, token string) (map[string]any, int, error) {
	var reqBody io.Reader
	if body != nil {
		data, err := json.Marshal(body)
		if err != nil {
			return nil, 0, err
		}
		reqBody = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(p.ctx, method, p.URL+path, reqBody)
	if err != nil {
		return nil, 0, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if token != "" {
		req.Header.Set("X-RimGovernor-Player", token)
	}
	req.Header.Set("Origin", p.URL)
	sent := time.Now()
	resp, err := p.client.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, 0, err
	}
	n := nextEvidenceSequence(filepath.Dir(p.dir))
	writeEvidence(filepath.Join(p.dir, fmt.Sprintf("http-%04d.json", n)), map[string]any{
		"sequence": n, "observed_at": sent.UTC().Format("2006-01-02T15:04:05.000Z07:00"), "elapsed_ms": time.Since(sent).Milliseconds(),
		"method": method, "path": path, "request": body, "status": resp.StatusCode, "response": json.RawMessage(data),
	})
	var out map[string]any
	if len(data) > 0 {
		if err := json.Unmarshal(data, &out); err != nil {
			return nil, resp.StatusCode, fmt.Errorf("decode %s %s: %w", method, path, err)
		}
	}
	return out, resp.StatusCode, nil
}

// Get is API's HTTPGet-shaped adapter for AssertRoutineRunning.
func (p *ServiceProcess) Get(method, path string) (map[string]any, error) {
	v, status, err := p.API(method, path, nil, "")
	if err != nil {
		return nil, err
	}
	if status != 200 {
		return nil, fmt.Errorf("%s %s: status %d", method, path, status)
	}
	return v, nil
}

// SessionToken checks /api/health against this process and returns the
// explicit-player session token.
func (p *ServiceProcess) SessionToken() (string, error) {
	health, status, err := p.API("GET", "/api/health", nil, "")
	if err != nil {
		return "", err
	}
	if status != 200 || AsString(health["service"]) != "rimgovernor" || AsString(health["backend"]) != "go" || int(AsNumber(health["pid"])) != p.PID {
		return "", fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health)
	}
	session, status, err := p.API("GET", "/api/player/session", nil, "")
	if err != nil {
		return "", err
	}
	if status != 200 || AsString(session["mode"]) != "explicit-player" || AsString(session["token"]) == "" {
		return "", fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session)
	}
	return AsString(session["token"]), nil
}

// WaitAttached polls /api/state until the service's own bridge session has
// attached to the same running game identity the harness prepared.
func (p *ServiceProcess) WaitAttached(identity map[string]any, timeout time.Duration) (map[string]any, error) {
	deadline := time.Now().Add(timeout)
	for {
		state, status, err := p.API("GET", "/api/state", nil, "")
		if err != nil {
			return nil, err
		}
		if status != 200 {
			return nil, fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state)
		}
		if connected, _ := AsBool(state["connected"]); connected {
			if svc, ok := AsMap(state["identity"]); ok && MatchesIdentity(svc, identity) {
				return state, nil
			}
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("service did not attach to the fixture's identity in time: %#v", state)
		}
		select {
		case <-p.ctx.Done():
			return nil, p.ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

// MatchesIdentity reports whether v names the same colony/load/map as want.
func MatchesIdentity(v, want map[string]any) bool {
	return AsString(v["colonyId"]) == AsString(want["colonyId"]) &&
		AsString(v["loadToken"]) == AsString(want["loadToken"]) &&
		AsNumber(v["mapId"]) == AsNumber(want["mapId"])
}

// SubmitAndResume submits one player building plan (guidance the running
// world carries) and resumes automatic control, returning the world's root
// plan id from the resume reply.
func (p *ServiceProcess) SubmitAndResume(prefix string, identity map[string]any, building map[string]any, token string, report Report) (rootPlanID string, err error) {
	submission, status, err := p.API("POST", "/api/buildings/plans", map[string]any{
		"requestId": prefix + "-construction-1", "expected": identity, "building": building,
	}, token)
	if err != nil {
		return "", err
	}
	if status != 200 && status != 201 {
		return "", fmt.Errorf("unexpected building submission status=%d body=%#v", status, submission)
	}
	if AsString(submission["planId"]) == "" || AsString(submission["revision"]) == "" {
		return "", fmt.Errorf("unexpected building submission: %#v", submission)
	}
	report["submission"] = submission
	return p.Resume(prefix, identity, token, report)
}

// Resume enters automate mode without an anchor plan: authority is the
// world's own root plan, created on first resume (SIMP02, #55). It returns
// that root plan id.
func (p *ServiceProcess) Resume(prefix string, identity map[string]any, token string, report Report) (rootPlanID string, err error) {
	resumed, status, err := p.API("POST", "/api/player/control/resume", map[string]any{
		"requestId": prefix + "-resume-1", "expected": identity,
	}, token)
	if err != nil {
		return "", err
	}
	if status != 200 {
		return "", fmt.Errorf("unexpected resume status=%d body=%#v", status, resumed)
	}
	record, _ := AsMap(resumed["record"])
	if AsString(record["phase"]) != "running" {
		return "", fmt.Errorf("resume was not running: %#v", resumed)
	}
	report["resumed"] = resumed
	state, _ := AsMap(resumed["state"])
	generation, _ := AsMap(state["generation"])
	rootPlanID = AsString(generation["plan"])
	if rootPlanID == "" {
		return "", fmt.Errorf("resume reported no root plan: %#v", resumed)
	}
	return rootPlanID, nil
}

// AuthorityKeepAlive resumes native player authority whenever the service
// drops out of automate mode, acknowledging any clock hold first, exactly as
// cases/routinehaul's own keep-alive does: a bounded native generation
// legitimately lapses on any unrecognised clock event (a random world
// letter), and nothing resumes it automatically.
type AuthorityKeepAlive struct {
	Service  *ServiceProcess
	Prefix   string
	Identity map[string]any
	Token    string

	// hold, while set, stops the loop re-acquiring: a checkpoint pauses the
	// clock on purpose and the save needs manual control to stay manual.
	hold atomic.Bool

	mu                sync.Mutex
	attempts          int
	reacquired        int
	acknowledged      int
	acknowledgeFailed int
	// generationAdvance sums, over reacquisitions, the native generations
	// each resume cost: the grant's generation less the one the service
	// reported before it. A resume from a plain clock hold costs one;
	// generationsOverOne counts those that cost more (a revoke first, the
	// Manual->Auto pair #259 removed, or a native revocation in between).
	generationAdvance  uint64
	generationsOverOne int
	lastError          string
}

// Start runs the keep-alive loop until ctx is cancelled.
func (k *AuthorityKeepAlive) Start(ctx context.Context) (stop func() map[string]any) {
	loopCtx, cancel := context.WithCancel(ctx)
	var wg sync.WaitGroup
	wg.Add(1)
	go func() { defer wg.Done(); k.run(loopCtx) }()
	return func() map[string]any {
		cancel()
		wg.Wait()
		return k.Snapshot()
	}
}

// Snapshot reports the loop's counters for the report.
func (k *AuthorityKeepAlive) Snapshot() map[string]any {
	k.mu.Lock()
	defer k.mu.Unlock()
	out := map[string]any{
		"attempts": k.attempts, "reacquired": k.reacquired,
		"acknowledged": k.acknowledged, "acknowledge_failed": k.acknowledgeFailed,
		"generation_advance": k.generationAdvance, "generations_over_one": k.generationsOverOne,
	}
	if k.lastError != "" {
		out["last_error"] = k.lastError
	}
	return out
}

// Hold pauses (true) or resumes (false) re-acquisition.
func (k *AuthorityKeepAlive) Hold(held bool) { k.hold.Store(held) }

func (k *AuthorityKeepAlive) fail(message string) {
	k.mu.Lock()
	k.lastError = message
	k.mu.Unlock()
}

// KeepAliveInterval is how often a keep-alive checks the service is still
// in automate mode: a lapse costs the colony that many ticks of no
// governor, so the check is cheap (/api/state) and frequent (#267).
const KeepAliveInterval = 500 * time.Millisecond

func (k *AuthorityKeepAlive) run(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-time.After(KeepAliveInterval):
		}
		if k.hold.Load() {
			continue
		}
		state, status, err := k.Service.API("GET", "/api/state", nil, "")
		if err != nil || status != 200 {
			continue
		}
		if AsString(state["mode"]) == "automate" {
			continue
		}
		before, beforeKnown := keepAliveGeneration(state["generation"])
		if clk, clkStatus, clkErr := k.Service.API("GET", "/api/player/clock", nil, ""); clkErr == nil && clkStatus == 200 {
			if holds := AsSlice(clk["holds"]); len(holds) > 0 {
				ackBody := map[string]any{
					"requestId":        fmt.Sprintf("%s-ack-%d", k.Prefix, time.Now().UnixNano()),
					"expectedRevision": AsString(clk["revision"]),
					"throughCursor":    AsString(clk["inboxCursor"]),
				}
				if _, ackStatus, ackErr := k.Service.API("POST", "/api/player/clock/acknowledge", ackBody, k.Token); ackErr != nil || ackStatus != 200 {
					k.mu.Lock()
					if ackErr != nil {
						k.lastError = "acknowledge: " + ackErr.Error()
					} else {
						k.lastError = fmt.Sprintf("acknowledge status=%d", ackStatus)
					}
					k.acknowledgeFailed++
					k.mu.Unlock()
				} else {
					k.mu.Lock()
					k.acknowledged++
					k.mu.Unlock()
				}
			}
		}
		k.mu.Lock()
		k.attempts++
		k.mu.Unlock()
		body := map[string]any{
			"requestId": fmt.Sprintf("%s-resume-%d", k.Prefix, time.Now().UnixNano()),
			"expected":  k.Identity,
		}
		resumed, status, err := k.Service.API("POST", "/api/player/control/resume", body, k.Token)
		if err != nil {
			k.fail(err.Error())
			continue
		}
		if status != 200 {
			k.fail(fmt.Sprintf("resume status=%d body=%#v", status, resumed))
			continue
		}
		record, _ := AsMap(resumed["record"])
		if AsString(record["phase"]) != "running" {
			k.fail(fmt.Sprintf("resume not running: %#v", resumed))
			continue
		}
		k.mu.Lock()
		k.reacquired++
		k.lastError = ""
		if after, err := strconv.ParseUint(AsString(record["nativeGeneration"]), 10, 64); err == nil && beforeKnown && after > before {
			k.generationAdvance += after - before
			if after-before > 1 {
				k.generationsOverOne++
			}
		}
		k.mu.Unlock()
	}
}

// keepAliveGeneration reads the native generation out of /api/state's
// generation object, if it carries one.
func keepAliveGeneration(v any) (uint64, bool) {
	generation, ok := AsMap(v)
	if !ok {
		return 0, false
	}
	native, err := strconv.ParseUint(AsString(generation["native"]), 10, 64)
	return native, err == nil
}

// OpenStoreWithRetry opens the service's SQLite journal read-side, retrying
// until the service has created it.
func OpenStoreWithRetry(ctx context.Context, path string) (*store.Store, error) {
	deadline := time.Now().Add(60 * time.Second)
	for {
		s, err := store.Open(ctx, path)
		if err == nil {
			return s, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

// WaitRoutineReview polls until the service reaches automate mode and a
// routine review has been persisted, returning the diagnostics it gathered.
// Besides timeout the wait stalls (StallBudget) when neither the service
// mode nor the review revision changes.
func (p *ServiceProcess) WaitRoutineReview(ctx context.Context, s *store.Store, timeout time.Duration) (store.RoutineReview, []map[string]any, error) {
	var diagnostics []map[string]any
	sawAutomate := false
	var review store.RoutineReview
	err := WaitProgress(ctx, Wait{Ceiling: timeout, Stall: StallBudget(), Terminal: p.Exited}, func(ctx context.Context) (string, bool, error) {
		st, _, stErr := p.API("GET", "/api/state", nil, "")
		clk, _, clkErr := p.API("GET", "/api/player/clock", nil, "")
		entry := map[string]any{"state": st, "clock": clk}
		if stErr != nil {
			entry["state_error"] = stErr.Error()
		}
		if clkErr != nil {
			entry["clock_error"] = clkErr.Error()
		}
		diagnostics = append(diagnostics, entry)
		if AsString(st["mode"]) == "automate" {
			sawAutomate = true
		}
		if r, err := s.LoadRoutineReview(ctx); err == nil {
			review = r
			if review.Revision > 0 && sawAutomate {
				return "", true, nil
			}
		}
		return Signature(AsString(st["mode"]), review.Revision), false, nil
	})
	if err == nil {
		return review, diagnostics, nil
	}
	if !sawAutomate {
		return review, diagnostics, fmt.Errorf("service never reached automate mode after acquire: %w", err)
	}
	return review, diagnostics, fmt.Errorf("service reached automate mode but the routine review was never persisted (revision 0): %w", err)
}

// WaitReview polls the durable routine review under w until ready accepts
// it. The progress signature is the review's latches and goal bindings, so
// a review that keeps revising without moving either stalls.
func WaitReview(ctx context.Context, s *store.Store, w Wait, ready func(store.RoutineReview) bool) (store.RoutineReview, error) {
	var review store.RoutineReview
	if w.Interval <= 0 {
		w.Interval = time.Second
	}
	err := WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		r, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		review = r
		if ready(r) {
			return "", true, nil
		}
		needs := make([]string, 0, len(r.Goals))
		for _, binding := range r.Goals {
			needs = append(needs, string(binding.Need)+"="+string(binding.Goal))
		}
		return Signature(fmt.Sprintf("%+v", r.Latches), needs), false, nil
	})
	return review, err
}

// WaitPlan polls plan under w until check accepts its state; check's
// signature is whatever of the plan must move (usually its action stages
// and attempts, see PlanSignature).
func WaitPlan(ctx context.Context, s *store.Store, w Wait, planID domain.PlanID, check func(store.PlanState) (signature string, done bool, err error)) (store.PlanState, error) {
	var state store.PlanState
	if w.Interval <= 0 {
		w.Interval = time.Second
	}
	err := WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		st, err := s.LoadPlan(ctx, planID)
		if err != nil {
			return "", false, err
		}
		state = st
		return check(st)
	})
	return state, err
}

// PlanSignature is each action's stage, attempt and receipt.
func PlanSignature(state store.PlanState) string {
	parts := make([]any, 0, 3*len(state.Progress))
	for _, p := range state.Progress {
		v := p.View()
		receipt, _ := v.Receipt.Value()
		parts = append(parts, v.Stage, v.Attempt, receipt)
	}
	return Signature(parts...)
}

// WaitGoalMethod polls the durable routine review for need's goal binding
// and a committed method on it other than previous, exactly what the
// building planners themselves read (review.Goals then the goal's Methods).
// A method whose plan completed and retired within a single clock window
// (the goal recovered before the harness polled) is no longer on the goal's
// live Methods, so the current epoch's bounded history is consulted too.
// The wait stalls (StallBudget) when the binding and its method count stop
// changing.
func WaitGoalMethod(ctx context.Context, s *store.Store, need policy.GoalID, previous *domain.GoalMethod) (domain.GoalID, domain.GoalMethod, error) {
	var seen map[domain.PlanID]bool
	if previous != nil {
		seen = map[domain.PlanID]bool{previous.Plan: true}
	}
	return WaitGoalMethodExcluding(ctx, s, need, seen)
}

// WaitGoalMethodExcluding is WaitGoalMethod over a set of already-followed
// plans: a goal whose history holds several settled methods (retired attempts
// stay in LoadGoalMethods) would otherwise hand the same old plan back on
// every call that names only the last one.
func WaitGoalMethodExcluding(ctx context.Context, s *store.Store, need policy.GoalID, seen map[domain.PlanID]bool) (domain.GoalID, domain.GoalMethod, error) {
	return waitGoalMethod(ctx, s, Wait{Stall: StallBudget(), Interval: time.Second}, need, seen)
}

// waitGoalMethod is WaitGoalMethodExcluding under an explicit Wait.
func waitGoalMethod(ctx context.Context, s *store.Store, w Wait, need policy.GoalID, seen map[domain.PlanID]bool) (domain.GoalID, domain.GoalMethod, error) {
	var foundGoal domain.GoalID
	var found domain.GoalMethod
	err := WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		var goalID domain.GoalID
		for _, binding := range review.Goals {
			if binding.Need == need {
				goalID = binding.Goal
				break
			}
		}
		if goalID == "" {
			return Signature("unbound", review.Revision > 0), false, nil
		}
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		methods := goal.Methods
		if err == nil && len(methods) == 0 {
			if methods, err = s.LoadGoalMethods(ctx, goalID, goal.Goal.Epoch); err != nil {
				return "", false, err
			}
		}
		for _, method := range methods {
			if !seen[method.Plan] {
				foundGoal, found = goalID, method
				return "", true, nil
			}
		}
		return Signature(goalID, len(methods)), false, nil
	})
	if err != nil {
		return "", domain.GoalMethod{}, fmt.Errorf("goal method for %s: %w", need, err)
	}
	return foundGoal, found, nil
}

// WaitPlanTerminal polls plan until every action reaches a terminal stage.
// incidental reports the executor's own authority-discontinuity cancellation
// (Cancelled with Effect Absent, see domain.Progress.Observe), which the
// caller should follow with a fresh WaitGoalMethod rather than fail on. The
// wait stalls (StallBudget) when no action's stage or attempt changes.
func WaitPlanTerminal(ctx context.Context, s *store.Store, planID domain.PlanID) (state store.PlanState, incidental bool, err error) {
	return waitPlanTerminal(ctx, s, Wait{Stall: StallBudget(), Interval: time.Second}, planID)
}

// waitPlanTerminal is WaitPlanTerminal under an explicit Wait.
func waitPlanTerminal(ctx context.Context, s *store.Store, w Wait, planID domain.PlanID) (state store.PlanState, incidental bool, err error) {
	err = WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		state, err = s.LoadPlan(ctx, planID)
		if err != nil {
			return "", false, err
		}
		terminal := len(state.Progress) > 0
		var signature []any
		for _, progress := range state.Progress {
			view := progress.View()
			signature = append(signature, view.Stage, view.Attempt, view.Unresolved)
			switch view.Stage {
			case domain.Completed:
			case domain.Unsuccessful:
				return "", false, fmt.Errorf("plan %s reached unsuccessful instead of completed", planID)
			case domain.Cancelled:
				// Never dispatched, or dispatched with a known absent effect:
				// the executor's own no-effect rule (executor/accounting.go).
				effect, known := view.Effect.Value()
				if !view.Unresolved && (view.Attempt == 0 || known && effect == domain.EffectAbsent) {
					incidental = true
					return "", true, nil
				}
				return "", false, fmt.Errorf("plan %s reached cancelled instead of completed", planID)
			default:
				terminal = false
			}
		}
		return Signature(signature...), terminal, nil
	})
	if err != nil {
		return state, false, err
	}
	return state, incidental, nil
}

// ReopenSession retries OpenBridgeSession for a few seconds after a service stop,
// since the killed service's own GABS subprocess releases the game slot
// asynchronously.
func ReopenSession(ctx context.Context, gabsExecutable, configuration, gameID string) (*bridge.Client, error) {
	deadline := time.Now().Add(30 * time.Second)
	for {
		c, err := OpenBridgeSession(ctx, gabsExecutable, configuration, gameID, 60*time.Second)
		if err == nil {
			return c, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
}

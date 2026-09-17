package nativeaccept

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// ErrReuseRetired is returned once a GameReuse has stopped its game: the
// RimWorld process is gone and the caller must not attempt another case.
var ErrReuseRetired = errors.New("reusable game retired")

// GameReuse keeps one owned GABS/RimWorld process alive across several
// acceptance cases (issue #22). Every case begins with a reload of its save
// into the same process and a reset check (ResetState/CheckReset) that the
// reload actually gave the case a clean baseline; a case that fails, or that
// leaves ownership behind, retires the whole game rather than letting the next
// case inherit its state. Fresh-process mode (one OpenSession per binary) stays
// the default everywhere; this is an opt-in optimisation for multi-case loops
// such as sustainedmatrixaccept.
//
// The single GABP slot constraint (see routinehaulaccept) is respected through
// Session/ReleaseSession: a case that launches `rimgovernor serve` releases the
// harness session first and reacquires it once the service has stopped. The
// game itself survives both, because ReleaseSession never calls games_stop.
//
// Mod static state is process-scoped and is NOT reset by a reload -- see
// contracts/native-static-state.md. Cases asserting on those statics must stay
// in fresh-process mode.
type GameReuse struct {
	Config *Config
	GABS   string
	// Output holds this lifecycle's own evidence (session/retire rows); each
	// case records under its own directory.
	Output string

	// StartupDuration is the one launch this lifecycle paid for.
	StartupDuration time.Duration

	client    *bridge.Client
	harness   *Harness
	current   *ReuseCase
	tokens    map[string]string
	baselines map[string]ResetState
	rows      []map[string]any
	retired   bool
	reason    string
}

// ReuseCase is one case's handle: its loaded identity and a Harness recording
// evidence under its own output directory.
type ReuseCase struct {
	Name     string
	Save     string
	Output   string
	Identity map[string]any
	Harness  *Harness
	Reset    ResetState

	began time.Time
	row   map[string]any
}

// StockCounts is the slice of a resource's stock a reload must restore:
// units from the colony-facts census and, when a bounded supplies read was
// available, the forbidden count (-1 when it was not sampled).
type StockCounts struct {
	Units, Forbidden int64
}

// ResetState is what BeginCase/EndCase observe about the loaded game. Kept as
// plain data so CheckReset is unit-testable without a game.
type ResetState struct {
	ColonyID        string
	LoadToken       string
	Tick            float64
	Paused          bool
	AuthorityActive bool
	OwnedDrafts     int
	Stock           map[string]StockCounts
}

// CheckReset lists every way now fails the reset contract for save against
// what earlier cases established: baseline is the first observation of the
// same save (nil when this is its first load), tokens are the load tokens
// every prior case received. It returns nil when the reload is clean.
func CheckReset(now ResetState, baseline *ResetState, tokens map[string]string) error {
	var problems []string
	if now.LoadToken == "" || now.ColonyID == "" {
		problems = append(problems, "missing colony id or load token")
	}
	if prior, seen := tokens[now.LoadToken]; seen {
		problems = append(problems, fmt.Sprintf("load token %q was already issued to case %q", now.LoadToken, prior))
	}
	if !now.Paused {
		problems = append(problems, "game is not paused after the reload")
	}
	if now.AuthorityActive {
		problems = append(problems, "authority is still active after the reload")
	}
	if now.OwnedDrafts > 0 {
		problems = append(problems, fmt.Sprintf("%d owned draft claim(s) survive the reload", now.OwnedDrafts))
	}
	// ColonyID is deliberately not compared: a fixture save never written by
	// the mod has no persisted colony id, so each load mints a new one.
	if baseline != nil {
		if now.Tick != baseline.Tick {
			problems = append(problems, fmt.Sprintf("tick %v differs from the baseline tick %v", now.Tick, baseline.Tick))
		}
		if diff := stockDiff(baseline.Stock, now.Stock); diff != "" {
			problems = append(problems, "stock differs from the baseline: "+diff)
		}
	}
	if len(problems) == 0 {
		return nil
	}
	return errors.New(strings.Join(problems, "; "))
}

func stockDiff(want, got map[string]StockCounts) string {
	var diffs []string
	names := make([]string, 0, len(want)+len(got))
	seen := map[string]bool{}
	for n := range want {
		names = append(names, n)
		seen[n] = true
	}
	for n := range got {
		if !seen[n] {
			names = append(names, n)
		}
	}
	sort.Strings(names)
	for _, n := range names {
		w, wok := want[n]
		g, gok := got[n]
		if !wok || !gok || w.Units != g.Units || (w.Forbidden >= 0 && g.Forbidden >= 0 && w.Forbidden != g.Forbidden) {
			diffs = append(diffs, fmt.Sprintf("%s want=%+v got=%+v", n, w, g))
		}
	}
	return strings.Join(diffs, ", ")
}

// OpenReusableGame launches the game once (one games_start) and returns a
// lifecycle at the main menu; nothing is loaded until the first BeginCase.
func OpenReusableGame(ctx context.Context, cfg *Config, gabsExecutable string) (*GameReuse, error) {
	if cfg.Configuration == "" {
		return nil, fmt.Errorf("reuse: Config.PrepareConfig must run first")
	}
	output := filepath.Join(cfg.Output, "reuse")
	if err := os.MkdirAll(output, 0755); err != nil {
		return nil, err
	}
	g := &GameReuse{Config: cfg, GABS: gabsExecutable, Output: output,
		tokens: map[string]string{}, baselines: map[string]ResetState{}}
	started := time.Now()
	if _, err := g.Session(ctx); err != nil {
		return nil, err
	}
	g.StartupDuration = time.Since(started)
	return g, nil
}

// Session returns the live harness session, reopening it when a case released
// it for a service. A reopen tolerates the previous holder's GABS subprocess
// still letting go of the game (up to 30s), matching sustainedfood's reopen.
func (g *GameReuse) Session(ctx context.Context) (*Harness, error) {
	if g.retired {
		return nil, ErrReuseRetired
	}
	if g.client != nil {
		return g.harness, nil
	}
	deadline := time.Now().Add(30 * time.Second)
	for {
		client, err := OpenSession(ctx, g.GABS, g.Config.Configuration, g.Config.GameID, 60*time.Second)
		if err == nil {
			g.client = client
			g.harness = NewHarness(client, g.harnessOutput())
			return g.harness, nil
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("reuse: reopen bridge session: %w", err)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

func (g *GameReuse) harnessOutput() string {
	if g.current != nil {
		return g.current.Output
	}
	return g.Output
}

// ReleaseSession closes the harness session without stopping the game, freeing
// the GABP slot for a `rimgovernor serve` subprocess.
func (g *GameReuse) ReleaseSession() error {
	if g.client == nil {
		return nil
	}
	err := g.client.Close()
	g.client, g.harness = nil, nil
	return err
}

// BeginCase reloads save into the running game, verifies the reset contract
// and returns the case handle. Any contract violation retires the game and
// returns an ErrReuseRetired-wrapped error.
func (g *GameReuse) BeginCase(ctx context.Context, name, save, output string) (*ReuseCase, error) {
	if g.retired {
		return nil, ErrReuseRetired
	}
	if g.current != nil {
		return nil, fmt.Errorf("reuse: case %q is still open", g.current.Name)
	}
	if err := os.MkdirAll(output, 0755); err != nil {
		return nil, err
	}
	c := &ReuseCase{Name: name, Save: save, Output: output, began: time.Now(),
		row: map[string]any{"case": name, "save": save, "output": output}}
	g.current = c
	h, err := g.Session(ctx)
	if err != nil {
		g.current = nil
		return nil, err
	}
	// Point the shared session's evidence at this case's directory.
	h.Output = output
	c.Harness = h

	if err := g.load(ctx, h, save); err != nil {
		return nil, g.abandon(ctx, c, fmt.Sprintf("case %q load: %v", name, err))
	}
	if _, err := h.Call(ctx, "reuse-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, g.abandon(ctx, c, fmt.Sprintf("case %q pause: %v", name, err))
	}
	state, identity, err := g.observe(ctx, h, "reuse-begin")
	if err != nil {
		return nil, g.abandon(ctx, c, fmt.Sprintf("case %q observe: %v", name, err))
	}
	var baseline *ResetState
	if first, ok := g.baselines[save]; ok {
		baseline = &first
	}
	if err := CheckReset(state, baseline, g.tokens); err != nil {
		c.row["reset_violation"] = err.Error()
		return nil, g.abandon(ctx, c, fmt.Sprintf("case %q reset contract: %v", name, err))
	}
	if baseline == nil {
		g.baselines[save] = state
		g.rows = append(g.rows, map[string]any{"baseline": save, "colonyId": state.ColonyID, "tick": state.Tick, "stocks": len(state.Stock)})
	}
	g.tokens[state.LoadToken] = name
	c.Identity = identity
	c.Reset = state
	c.row["loadToken"] = state.LoadToken
	c.row["colonyId"] = state.ColonyID
	c.row["tick"] = state.Tick
	c.row["readiness_ms"] = time.Since(c.began).Milliseconds()
	return c, nil
}

// abandon records a case that never got past BeginCase and retires the game.
func (g *GameReuse) abandon(ctx context.Context, c *ReuseCase, reason string) error {
	c.row["failed"] = true
	c.row["duration_ms"] = time.Since(c.began).Milliseconds()
	g.rows = append(g.rows, c.row)
	g.current = nil
	return g.retire(ctx, reason)
}

// EndCase closes the case. A failed case retires the game. A successful case
// must have released everything it owned: authority inactive and no owned
// draft claims; otherwise the game is retired too, since the next case could
// not trust its baseline. Returns ErrReuseRetired (wrapped) on retirement.
func (g *GameReuse) EndCase(ctx context.Context, c *ReuseCase, failed bool) error {
	if g.current != c {
		return fmt.Errorf("reuse: EndCase for a case that is not open")
	}
	defer func() {
		c.row["duration_ms"] = time.Since(c.began).Milliseconds()
		c.row["failed"] = failed
		g.rows = append(g.rows, c.row)
		g.current = nil
		if g.harness != nil {
			g.harness.Output = g.Output
		}
	}()
	if g.retired {
		return ErrReuseRetired
	}
	if failed {
		return g.retire(ctx, fmt.Sprintf("case %q failed", c.Name))
	}
	h, err := g.Session(ctx)
	if err != nil {
		return g.retire(ctx, fmt.Sprintf("case %q end: %v", c.Name, err))
	}
	h.Output = c.Output
	state, _, err := g.observe(ctx, h, "reuse-end")
	if err != nil {
		return g.retire(ctx, fmt.Sprintf("case %q end observe: %v", c.Name, err))
	}
	var problems []string
	if state.LoadToken != c.Reset.LoadToken {
		problems = append(problems, "the case changed the loaded map")
	}
	if state.AuthorityActive {
		problems = append(problems, "authority left active")
	}
	if state.OwnedDrafts > 0 {
		problems = append(problems, fmt.Sprintf("%d owned draft claim(s) left behind", state.OwnedDrafts))
	}
	if len(problems) > 0 {
		c.row["quiescence_violation"] = strings.Join(problems, "; ")
		return g.retire(ctx, fmt.Sprintf("case %q left the game unclean: %s", c.Name, strings.Join(problems, "; ")))
	}
	c.row["quiescent"] = true
	return nil
}

// Retire stops the game (games_stop) and closes the session. Idempotent.
func (g *GameReuse) Retire(ctx context.Context, reason string) error {
	if g.retired {
		return nil
	}
	err := g.retire(ctx, reason)
	if errors.Is(err, ErrReuseRetired) {
		return nil
	}
	return err
}

func (g *GameReuse) retire(ctx context.Context, reason string) error {
	if g.retired {
		return fmt.Errorf("%w: %s", ErrReuseRetired, g.reason)
	}
	g.retired, g.reason = true, reason
	row := map[string]any{"retired": reason}
	stopCtx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	if g.client == nil {
		// A service may still be letting go; reopen just to stop.
		deadline := time.Now().Add(30 * time.Second)
		for g.client == nil {
			client, err := OpenSession(stopCtx, g.GABS, g.Config.Configuration, g.Config.GameID, 60*time.Second)
			if err == nil {
				g.client = client
				break
			}
			if time.Now().After(deadline) {
				row["stop_error"] = "reopen for games_stop: " + err.Error()
				g.rows = append(g.rows, row)
				return fmt.Errorf("%w: %s", ErrReuseRetired, reason)
			}
			time.Sleep(time.Second)
		}
	}
	if stopped, err := g.client.GamesStop(stopCtx); err == nil {
		row["stop"] = string(stopped.Envelope)
	} else {
		row["stop_error"] = err.Error()
	}
	_ = g.client.Close()
	g.client, g.harness = nil, nil
	g.rows = append(g.rows, row)
	return fmt.Errorf("%w: %s", ErrReuseRetired, reason)
}

// Retired reports whether the game has been stopped, and why.
func (g *GameReuse) Retired() (bool, string) { return g.retired, g.reason }

// Rows returns the lifecycle's evidence rows for a report: the one startup,
// each baseline sample, each case (load token, readiness, quiescence) and the
// retirement, in order.
func (g *GameReuse) Rows() []map[string]any {
	out := []map[string]any{{"startup_ms": g.StartupDuration.Milliseconds()}}
	return append(out, g.rows...)
}

// Record writes the lifecycle's rows into report under "reuse".
func (g *GameReuse) Record(report Report) {
	report["reuse_game"] = true
	report["reuse"] = g.Rows()
	if g.retired {
		report["reuse_retired"] = g.reason
	}
}

func (g *GameReuse) load(ctx context.Context, h *Harness, save string) error {
	// Every load goes through the harness-level rimworld/load_game_ready.
	// The trusted rimgovernor/lifecycle_load path is not usable here: it
	// requires the expected identity to survive the load, but a fixture save
	// that was never written by the mod carries no persisted colony id, so
	// native mints a fresh one per load (ColonyIdentity.cs) and lifecycle_load
	// reports the load as superseded. loadaccept covers that path with a
	// save it wrote itself.
	_, err := h.Call(ctx, "reuse-load", "rimworld/load_game_ready", map[string]any{
		"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	})
	return err
}

// observe reads everything ResetState needs from the loaded game.
func (g *GameReuse) observe(ctx context.Context, h *Harness, label string) (ResetState, map[string]any, error) {
	var state ResetState
	identityReply, err := h.Wire(ctx, label+"-identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return state, nil, err
	}
	_, loaded, err := Outcome(identityReply, "loaded")
	if err != nil {
		return state, nil, fmt.Errorf("identity: %w", err)
	}
	loadedContext, _ := AsMap(loaded["context"])
	identity, _ := AsMap(loadedContext["identity"])
	state.ColonyID = AsString(identity["colonyId"])
	state.LoadToken = AsString(identity["loadToken"])
	state.Tick = AsNumber(loadedContext["tick"])
	state.Paused, _ = AsBool(loaded["paused"])

	statusReply, err := h.Wire(ctx, label+"-authority", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return state, nil, err
	}
	_, status, err := Outcome(statusReply, "status")
	if err != nil {
		return state, nil, fmt.Errorf("authority status: %w", err)
	}
	_, state.AuthorityActive = status["active"]

	pawnsReply, err := h.Wire(ctx, label+"-pawns", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
		"page": map[string]any{"limit": 256},
	})
	if err != nil {
		return state, nil, err
	}
	_, pawns, err := Outcome(pawnsReply, "observed")
	if err != nil {
		return state, nil, fmt.Errorf("pawns: %w", err)
	}
	for _, v := range AsSlice(pawns["pawns"]) {
		row, _ := AsMap(v)
		claim, _ := AsMap(row["draftClaim"])
		if _, owned := claim["owned"]; owned {
			state.OwnedDrafts++
		}
	}

	// Stock: the colony-facts resource census (units per definition) is the
	// bounded, save-independent sample; an unfiltered supplies listing
	// exceeds the native 256-row item limit on an ordinary colony. Forbidden
	// counts come from a supplies read scoped to those definitions and are
	// skipped (-1) when native declines that read as too large as well.
	factsReply, err := h.Wire(ctx, label+"-stock", "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return state, nil, err
	}
	_, facts, err := Outcome(factsReply, "observed")
	if err != nil {
		return state, nil, fmt.Errorf("colony facts: %w", err)
	}
	state.Stock = map[string]StockCounts{}
	var defNames []any
	for _, v := range AsSlice(facts["resources"]) {
		row, _ := AsMap(v)
		name := AsString(row["defName"])
		if name == "" {
			continue
		}
		state.Stock[name] = StockCounts{Units: int64(AsNumber(row["units"])), Forbidden: -1}
		defNames = append(defNames, name)
	}
	if len(defNames) == 0 {
		return state, identity, nil
	}
	suppliesReply, err := h.Wire(ctx, label+"-forbidden", "observations_list_supplies", map[string]any{
		"scope":  map[string]any{"expectedIdentity": identity},
		"filter": map[string]any{"defNames": defNames, "ownership": "all"},
		"page":   map[string]any{"limit": 256},
	})
	if err != nil {
		return state, nil, err
	}
	if _, supplies, err := Outcome(suppliesReply, "observed"); err == nil {
		for _, v := range AsSlice(supplies["stocks"]) {
			row, _ := AsMap(v)
			definition, _ := AsMap(row["definition"])
			if counts, ok := state.Stock[AsString(definition["defName"])]; ok {
				counts.Forbidden = int64(AsNumber(row["forbidden"]))
				state.Stock[AsString(definition["defName"])] = counts
			}
		}
	} else if _, unavailable := UnavailableReason(suppliesReply); !unavailable {
		return state, nil, fmt.Errorf("supplies: %w", err)
	}
	return state, identity, nil
}

// PollLoad drives rimgovernor/lifecycle_read_load until a completed outcome,
// a native failure, or a bounded number of attempts elapses. A pending or
// superseded outcome is otherwise a definite non-completion the caller must
// fail on rather than loop past.
func PollLoad(ctx context.Context, h *Harness, first map[string]any, requestID, label string) (map[string]any, error) {
	reply := first
	for attempt := 0; attempt < 300; attempt++ {
		if _, completed, err := Outcome(reply, "completed"); err == nil {
			return completed, nil
		}
		if _, superseded, err := Outcome(reply, "superseded"); err == nil {
			return nil, fmt.Errorf("load was superseded: %v", superseded["detail"])
		}
		if code, ok := FailureCode(reply); ok {
			return nil, fmt.Errorf("load failed: %s", code)
		}
		if _, _, err := Outcome(reply, "pending"); err != nil {
			return nil, fmt.Errorf("unexpected load reply shape: %v", reply)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
		next, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, attempt), "lifecycle_read_load", map[string]any{"requestId": requestID})
		if err != nil {
			return nil, err
		}
		reply = next
	}
	return nil, fmt.Errorf("load did not complete within the polling budget")
}

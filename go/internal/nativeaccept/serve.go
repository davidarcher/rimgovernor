package nativeaccept

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/childproc"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ServeSpec declares the `rimgovernor serve` process a serve-driven harness
// runs against (issue #138). What every launch shares is fixed here rather
// than per harness: the clock speed comes from ClockSpeedArgs
// (RIMGOVERNOR_ACCEPT_CLOCK_SPEED, #128), the flight recorder and
// --listen 127.0.0.1:0 are always on, --pprof is on unless
// RIMGOVERNOR_ACCEPT_PPROF opts out (see pprof.go), --refresh is 1s, and
// the service's profile, state, logs and profiles live under the run's
// output directory.
type ServeSpec struct {
	// Binary is the prebuilt rimgovernor binary (absolute path).
	Binary string
	// Save, when set, is loaded through the harness's own session (naming
	// dialog dismissed, clock paused) before that session is released to
	// the service. Empty means the game already holds the case's state.
	Save string
	// Resume starts the service with --resume: it acquires authority for
	// the observed world by itself, so the harness skips Acquire.
	Resume bool
	// Families is RIMGOVERNOR_ROUTINE_FAMILIES; nil composes serve's
	// autonomous default (every family).
	Families []string
	// Extra are further serve arguments, appended verbatim.
	Extra []string
	// Env are further environment entries (KEY=value) for the process.
	Env []string
	// NativeTimeout is the service's --timeout; zero means 15s.
	NativeTimeout time.Duration
	// StepStall, when > 0, is how long the scheduler may go without
	// admitting a clock window before StepAdmitted reports a StepStallError
	// (a starved composed step under peer contention, #103).
	StepStall time.Duration
	// Prefix disambiguates the requestIds the handle issues (resume,
	// acknowledge); empty means "serve".
	Prefix string
	// KeepColonyNaming leaves a pending faction/settlement naming dialog
	// open for the service instead of dismissing it before the slot is
	// released: the case proves the ConfirmColonyNames routine family.
	KeepColonyNaming bool
}

// ServiceProcess is one running `rimgovernor serve` launched by Serve (or
// LaunchService) against the game the harness prepared. The harness's own
// bridge session must be released first, since only one GABP client may be
// connected to a running game at a time. Its waits (WaitReview, WaitPlan,
// WaitGoalMethod, WaitPlanTerminal, WaitStep) end as soon as the service
// exits on its own.
type ServiceProcess struct {
	URL       string
	PID       int
	StatePath string
	// Token is the explicit-player session token, set by Serve.
	Token string
	// Identity is the loaded colony the service attached to, set by Serve.
	Identity map[string]any
	// Launch is 1 for the first service of a run, 2 for a restart, and so on.
	Launch int
	// Spec is what the service was launched with.
	Spec ServeSpec

	cfg    *Config
	game   *Game
	report Report
	// entry is the report's record of this launch (report["service"],
	// report["service_2"], ...): argv, pid, url, state and what followed.
	entry map[string]any

	cmd      *exec.Cmd
	done     chan error
	stopped  bool
	exited   bool
	exit     error
	dir      string
	client   *http.Client
	ctx      context.Context
	counter  int
	store    *store.Store
	keep     *AuthorityKeepAlive
	stopKeep func() map[string]any
	stepped  bool
	profile  *serviceProfile
	mu       sync.Mutex
}

// ServiceLaunch names LaunchService's inputs beyond the harness's Config:
// the binary, the routine families to compose and any further serve flags
// or environment. It is ServeSpec's older shape; new code uses Serve.
type ServiceLaunch struct {
	Binary   string
	Families []string
	Extra    []string
	Env      []string
	// Output, when set, is where this launch's service directory (state,
	// logs) goes instead of Config.Output, and Report, when set, is where
	// the launch is recorded instead of the harness's report: a case that
	// launches once per sub-run (speedmatrix) keeps each apart.
	Output string
	Report Report
}

// Serve is the serve-driven family's one lifecycle: it loads spec.Save
// through the harness's session when asked, reads the loaded identity
// unless the caller supplies one, releases the session (Game.Release; the
// process is kept like any other afterwards, its clock journal intact,
// #119), launches rimgovernor serve, checks
// its health and player session and waits until the service's own bridge
// session has attached to the same identity. game may be nil when the
// caller has already released the slot itself (a GameReuse case), in which
// case identity is required. The launch is recorded under report["service"]
// (report["service_N"] for a restart).
func Serve(ctx context.Context, cfg *Config, game *Game, identity map[string]any, spec ServeSpec, report Report) (*ServiceProcess, error) {
	return serve(ctx, cfg, game, identity, spec, 1, report)
}

// serve is Serve with the launch number a game-less restart carries; with
// a game the game's own count wins.
func serve(ctx context.Context, cfg *Config, game *Game, identity map[string]any, spec ServeSpec, launch int, report Report) (*ServiceProcess, error) {
	gabs, err := GABSExecutable(cfg.Root, cfg.Configuration)
	if err != nil {
		return nil, err
	}
	if game != nil {
		if !game.released {
			h := NewHarness(game.Client, cfg.Output)
			if spec.Save != "" {
				if _, err := LoadSave(ctx, h, spec.Save, report); err != nil {
					return nil, err
				}
			}
			if identity == nil {
				if identity, err = ReadIdentity(ctx, h, "identity"); err != nil {
					return nil, err
				}
				report["identity"] = identity
			}
			// Free the sole GABP slot before the service starts its own
			// bridge session; this does NOT call games_stop, so the loaded
			// game survives.
			if err := game.Release(); err != nil {
				return nil, err
			}
		}
		game.serves++
		launch = game.serves
	}
	if identity == nil {
		return nil, errors.New("serve: the loaded identity is required once the harness session is released")
	}
	p, err := launchServe(ctx, cfg, gabs, spec, launch, report)
	if err != nil {
		return nil, err
	}
	p.game, p.Identity = game, identity
	if err := p.attach(); err != nil {
		p.Stop()
		return nil, err
	}
	return p, nil
}

// LaunchService starts rimgovernor serve for a harness that manages the
// attach and authority steps itself (the routine verticals); Serve is the
// full lifecycle. The service is recorded under report["service"].
func LaunchService(ctx context.Context, cfg *Config, gabsExecutable string, launch ServiceLaunch, report Report) (*ServiceProcess, error) {
	if launch.Output != "" {
		own := *cfg
		own.Output = launch.Output
		cfg = &own
	}
	if launch.Report != nil {
		report = launch.Report
	}
	return launchServe(ctx, cfg, gabsExecutable, ServeSpec{Binary: launch.Binary, Families: launch.Families, Extra: launch.Extra, Env: launch.Env}, 1, report)
}

// Restart launches the service again on the same state path after Stop, so
// a harness can exercise restart reconciliation: the harness session is
// released again if it reattached in between, and the new handle attaches
// to the same identity under report["service_N"].
func (p *ServiceProcess) Restart(ctx context.Context) (*ServiceProcess, error) {
	p.mu.Lock()
	stopped := p.stopped || p.exited
	p.mu.Unlock()
	if !stopped {
		return nil, fmt.Errorf("restart: service %d is still running", p.PID)
	}
	spec := p.Spec
	spec.Save = "" // already loaded
	return serve(ctx, p.cfg, p.game, p.Identity, spec, p.Launch+1, p.report)
}

// ServeArgs is the serve argv every launch shares (see ServeSpec), before
// the binary. profile, state and flight are the run's own paths.
func ServeArgs(cfg *Config, gabs, profileDir, statePath, flightPath string, spec ServeSpec) []string {
	timeout := spec.NativeTimeout
	if timeout <= 0 {
		timeout = 15 * time.Second
	}
	argv := []string{
		"serve",
		"--profile", profileDir,
		"--gabs", gabs,
		"--config", cfg.Configuration,
		"--game", cfg.GameID,
		"--state", statePath,
		"--listen", "127.0.0.1:0",
		"--refresh", "1s",
		"--timeout", timeout.String(),
		"--flight-recorder", flightPath,
	}
	argv = append(argv, ClockSpeedArgs()...)
	if ProfileServices() {
		argv = append(argv, "--pprof")
	}
	if spec.Resume {
		argv = append(argv, "--resume")
	}
	return append(argv, spec.Extra...)
}

// launchServe starts the process under output/service (output/service-N
// for a restart) with the state at output/service.sqlite, waits for its
// startup line and records the launch on the report.
func launchServe(ctx context.Context, cfg *Config, gabs string, spec ServeSpec, launch int, report Report) (*ServiceProcess, error) {
	output := cfg.Output
	profileDir := filepath.Join(output, "service-profile")
	serviceDir := filepath.Join(output, "service")
	key := "service"
	if launch > 1 {
		serviceDir = filepath.Join(output, fmt.Sprintf("service-%d", launch))
		key = fmt.Sprintf("service_%d", launch)
	}
	for _, dir := range []string{profileDir, serviceDir} {
		if err := os.MkdirAll(dir, 0755); err != nil {
			return nil, err
		}
	}
	statePath := filepath.Join(output, "service.sqlite")
	flightPath := FlightRecorderPath(output)
	if launch > 1 {
		flightPath = filepath.Join(serviceDir, "flight.jsonl")
	}
	argv := ServeArgs(cfg, gabs, profileDir, statePath, flightPath, spec)
	entry := map[string]any{"argv": append([]string{spec.Binary}, argv...), "state": "starting", "launch": launch}
	if report != nil {
		report[key] = entry
	}
	cmd := exec.CommandContext(ctx, spec.Binary, argv...)
	childproc.HideConsole(cmd)
	cmd.Env = append(os.Environ(), spec.Env...)
	if spec.Families != nil {
		families := strings.Join(spec.Families, ",")
		cmd.Env = append(cmd.Env, "RIMGOVERNOR_ROUTINE_FAMILIES="+families)
		entry["families"] = families
	}
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return nil, err
	}
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		stderrFile.Close()
		entry["state"] = "start failed: " + err.Error()
		return nil, fmt.Errorf("start rimgovernor serve: %w", err)
	}
	p := &ServiceProcess{
		PID: cmd.Process.Pid, StatePath: statePath, Launch: launch, Spec: spec,
		cfg: cfg, report: report, entry: entry,
		cmd: cmd, done: make(chan error, 1), dir: serviceDir,
		client: &http.Client{Timeout: 20 * time.Second}, ctx: ctx,
	}
	go func() { p.done <- cmd.Wait(); stderrFile.Close() }()
	entry["pid"], entry["state"] = p.PID, "running"

	reader := bufio.NewReader(stdoutPipe)
	firstLine, err := reader.ReadString('\n')
	if err != nil {
		p.Stop()
		return nil, fmt.Errorf("read service startup line: %w", err)
	}
	const prefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, prefix) {
		p.Stop()
		return nil, fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	p.URL = strings.TrimPrefix(firstLine, prefix)
	entry["url"] = p.URL
	if ProfileServices() {
		p.profile = startProfile(p.URL, serviceDir, profileSeconds(report), entry)
	}
	stdoutLogFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		p.Stop()
		return nil, err
	}
	go func() { _, _ = io.Copy(stdoutLogFile, reader); stdoutLogFile.Close() }()
	return p, nil
}

// attach is Serve's post-launch half: the player token and the service's
// own bridge session seeing the harness's identity.
func (p *ServiceProcess) attach() error {
	token, err := p.SessionToken()
	if err != nil {
		return err
	}
	p.Token = token
	state, err := p.WaitAttached(p.Identity, 90*time.Second)
	if err != nil {
		return err
	}
	p.entry["attached"] = state
	return nil
}

// Label is the requestId prefix for this launch: <prefix>-<launch>.
func (p *ServiceProcess) Label() string {
	prefix := p.Spec.Prefix
	if prefix == "" {
		prefix = "serve"
	}
	return fmt.Sprintf("%s-%d", prefix, p.Launch)
}

// Acquire resumes automatic control for the attached identity (authority is
// the world's own root plan, created on first resume) and returns that root
// plan id. A service started with Spec.Resume does this by itself.
func (p *ServiceProcess) Acquire() (rootPlanID string, err error) {
	return p.Resume(p.Label(), p.Identity, p.Token, p.entry)
}

// KeepAuthority runs an AuthorityKeepAlive for the attached identity until
// Stop, which records its counters under the launch's report entry and
// returns them.
func (p *ServiceProcess) KeepAuthority(ctx context.Context) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.keep != nil {
		return
	}
	p.keep = &AuthorityKeepAlive{Service: p, Prefix: p.Label(), Identity: p.Identity, Token: p.Token}
	p.stopKeep = p.keep.Start(ctx)
}

// HoldAuthority pauses (true) or resumes (false) the keep-alive's
// re-acquisition, for a harness that pauses the clock on purpose.
func (p *ServiceProcess) HoldAuthority(held bool) {
	p.mu.Lock()
	keep := p.keep
	p.mu.Unlock()
	if keep != nil {
		keep.Hold(held)
	}
}

// Stop ends the keep-alive, collects the service's profiles (pprof.go),
// kills the service if still running and waits for it to exit, and closes
// the handle's store. It returns the keep-alive
// counters when KeepAuthority ran, else nil. The service's own GABS
// subprocess ends with it (bridge's job object) and releases the game
// shortly (not synchronously) afterwards; Game.Reattach retries for that.
func (p *ServiceProcess) Stop() map[string]any {
	p.mu.Lock()
	if p.stopped {
		p.mu.Unlock()
		return nil
	}
	p.stopped = true
	stopKeep := p.stopKeep
	p.mu.Unlock()
	// The keep-alive may be inside API, which takes the mutex to number its
	// record: join it before holding the lock for the shutdown itself.
	var keep map[string]any
	if stopKeep != nil {
		keep = stopKeep()
	}
	if p.profile != nil {
		p.profile.stop(p.Exited() == nil)
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if keep != nil {
		p.entry["keepalive"] = keep
	}
	if !p.exited {
		if p.cmd.ProcessState == nil {
			_ = p.cmd.Process.Kill()
		}
		p.exit = <-p.done
		p.exited = true
		p.entry["state"] = "stopped"
	}
	if p.store != nil {
		p.store.Close()
		p.store = nil
	}
	return keep
}

// Exited reports, without blocking, whether the service has already exited
// on its own; the error names its exit. It is a Wait.Terminal for the poll
// loops that verify the service's durable state.
func (p *ServiceProcess) Exited() error {
	p.mu.Lock()
	defer p.mu.Unlock()
	if !p.exited {
		select {
		case err := <-p.done:
			p.exited, p.exit = true, err
			p.entry["state"] = "exited: " + errOrExit(err).Error()
		default:
			return nil
		}
	}
	return fmt.Errorf("service %d exited: %w", p.PID, errOrExit(p.exit))
}

// errOrExit names a clean exit, which is as terminal for a wait as a crash.
func errOrExit(err error) error {
	if err == nil {
		return errors.New("exit status 0")
	}
	return err
}

// AssertStopped verifies a stopped service no longer answers: its old session
// token (and any client still holding its URL) is dead, so a game reused after
// this service cannot be reached through the retired controller. Stop must
// have been called first.
func (p *ServiceProcess) AssertStopped() error {
	p.mu.Lock()
	stopped := p.stopped
	p.mu.Unlock()
	if !stopped {
		return fmt.Errorf("service %d has not been stopped", p.PID)
	}
	_, status, err := p.API("GET", "/api/health", nil, "")
	if err == nil {
		return fmt.Errorf("stopped service %d still answers /api/health with status %d", p.PID, status)
	}
	return nil
}

// StderrPath is the service's stderr log.
func (p *ServiceProcess) StderrPath() string { return filepath.Join(p.dir, "stderr.log") }

// Store opens (once) the service's durable journal for concurrent read-only
// verification; SQLite serves it alongside the running service. Stop closes
// it.
func (p *ServiceProcess) Store(ctx context.Context) (*store.Store, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.store != nil {
		return p.store, nil
	}
	s, err := OpenStoreWithRetry(ctx, p.StatePath)
	if err != nil {
		return nil, fmt.Errorf("open verification store: %w", err)
	}
	p.store = s
	return s, nil
}

// wait fills w's defaults for this service: the stall budget and the
// service's exit as the terminal.
func (p *ServiceProcess) wait(w Wait) Wait {
	if w.Stall <= 0 {
		w.Stall = StallBudget()
	}
	if w.Terminal == nil {
		w.Terminal = p.Exited
	}
	return w
}

// WaitReview is WaitReview on this service's store, ended by its exit.
func (p *ServiceProcess) WaitReview(ctx context.Context, w Wait, ready func(store.RoutineReview) bool) (store.RoutineReview, error) {
	s, err := p.Store(ctx)
	if err != nil {
		return store.RoutineReview{}, err
	}
	return WaitReview(ctx, s, p.wait(w), ready)
}

// WaitPlan is WaitPlan on this service's store, ended by its exit.
func (p *ServiceProcess) WaitPlan(ctx context.Context, w Wait, planID domain.PlanID, check func(store.PlanState) (string, bool, error)) (store.PlanState, error) {
	s, err := p.Store(ctx)
	if err != nil {
		return store.PlanState{}, err
	}
	return WaitPlan(ctx, s, p.wait(w), planID, check)
}

// WaitGoalMethod is WaitGoalMethod on this service's store, ended by its exit.
func (p *ServiceProcess) WaitGoalMethod(ctx context.Context, need policy.GoalID, previous *domain.GoalMethod) (domain.GoalID, domain.GoalMethod, error) {
	var seen map[domain.PlanID]bool
	if previous != nil {
		seen = map[domain.PlanID]bool{previous.Plan: true}
	}
	return p.WaitGoalMethodExcluding(ctx, need, seen)
}

// WaitGoalMethodExcluding is WaitGoalMethodExcluding on this service's
// store, ended by its exit.
func (p *ServiceProcess) WaitGoalMethodExcluding(ctx context.Context, need policy.GoalID, seen map[domain.PlanID]bool) (domain.GoalID, domain.GoalMethod, error) {
	s, err := p.Store(ctx)
	if err != nil {
		return "", domain.GoalMethod{}, err
	}
	return waitGoalMethod(ctx, s, p.wait(Wait{Interval: time.Second}), need, seen)
}

// WaitPlanTerminal is WaitPlanTerminal on this service's store, ended by
// its exit.
func (p *ServiceProcess) WaitPlanTerminal(ctx context.Context, planID domain.PlanID) (store.PlanState, bool, error) {
	s, err := p.Store(ctx)
	if err != nil {
		return store.PlanState{}, false, err
	}
	return waitPlanTerminal(ctx, s, p.wait(Wait{Interval: time.Second}), planID)
}

// StepAdmitted reports whether the scheduler has admitted a clock window (a
// journaled clock attempt) since the service started, recording when the
// first one was seen. Once Spec.StepStall has elapsed since `since` with
// none, it returns a StepStallError naming the last step failure the
// service logged; with StepStall zero it only reports.
func (p *ServiceProcess) StepAdmitted(ctx context.Context, since time.Time) (bool, error) {
	p.mu.Lock()
	stepped := p.stepped
	p.mu.Unlock()
	if stepped {
		return true, nil
	}
	s, err := p.Store(ctx)
	if err != nil {
		return false, err
	}
	// The retained window, not one row: LoadClockAttempts refuses
	// (ErrCapacity) when more attempts are retained than the limit, and
	// a fast clock retains several between two polls.
	attempts, err := s.LoadClockAttempts(ctx, 4096)
	if err != nil {
		return false, err
	}
	if len(attempts) > 0 {
		p.mu.Lock()
		p.stepped = true
		p.entry["first_clock_attempt_after"] = time.Since(since).Round(time.Second).String()
		p.mu.Unlock()
		return true, nil
	}
	if p.Spec.StepStall > 0 && time.Since(since) >= p.Spec.StepStall {
		return false, &StepStallError{Stall: p.Spec.StepStall, Families: strings.Join(p.Spec.Families, ","), LastFailure: lastStepFailure(p.StderrPath())}
	}
	return false, nil
}

// WaitStep blocks until StepAdmitted, ended by the service's exit or the
// context; it fails at once when Spec.StepStall is zero and nothing has
// been admitted within StallBudget.
func (p *ServiceProcess) WaitStep(ctx context.Context) error {
	since := time.Now()
	stall := p.Spec.StepStall
	if stall <= 0 {
		stall = StallBudget()
	}
	return WaitProgress(ctx, Wait{Ceiling: stall + time.Second, Stall: stall, Interval: time.Second, Terminal: p.Exited}, func(ctx context.Context) (string, bool, error) {
		ok, err := p.StepAdmitted(ctx, since)
		return "", ok, err
	})
}

// Entry is the report's record of this launch.
func (p *ServiceProcess) Entry() map[string]any { return p.entry }

// LoadSave loads profile/Saves/<save>.rws through h (visual readiness),
// pauses the clock and dismisses the colony naming dialog a fresh load can
// leave open: ConfirmColonyNames is a priority-zero emergency that blocks
// every other goal until it is resolved. It returns the home/colony_facts
// reply.
func LoadSave(ctx context.Context, h *Harness, save string, report Report) (map[string]any, error) {
	if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
		"saveName": save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	return ConfirmColonyNames(ctx, h, report)
}

// ConfirmColonyNames dismisses the faction/settlement naming dialog when
// one is pending and returns the home/colony_facts reply it consulted.
func ConfirmColonyNames(ctx context.Context, h *Harness, report Report) (map[string]any, error) {
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return nil, err
	}
	if naming, ok := AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(AsNumber(naming["windowId"])),
			"factionName":    AsString(naming["factionName"]),
			"settlementName": AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return nil, err
		}
		if success, _ := AsBool(confirmed["success"]); !success {
			return nil, fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	} else {
		report["confirmed_colony_names"] = "no pending naming dialog"
	}
	return facts, nil
}

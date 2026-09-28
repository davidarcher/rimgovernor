package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

type serveConfig struct {
	bridge                          bridge.ProcessConfig
	flightRecorder                  string
	state, listen, assets           string
	profile                         string
	playerControl                   bool
	clockControl                    bool
	routineReviews                  bool
	routineSleepingPlans            bool
	routineAcquisitionPlans         bool
	routineFieldPlans               bool
	routineFoodStoragePlans         bool
	routineBillPlans                bool
	routineWorkPlans                bool
	routineSupplyPlans              bool
	routineCookingPlans             bool
	routineShelterPlans             bool
	routineComfortPlans             bool
	routineWorkshopPlans            bool
	routineResearchPlans            bool
	routineIngredientStoragePlans   bool
	routineHospitalPlans            bool
	routineExpansionPlans           bool
	routinePowerPlans               bool
	routineTemperaturePlans         bool
	routineDefensePlans             bool
	routineTendPlans                bool
	routineRescuePlans              bool
	routineEquipPlans               bool
	routineSecureSuppliesPlans      bool
	routineRepairPlans              bool
	routineFireSafetyPlans          bool
	routineCleanPlans               bool
	routineWastePlans               bool
	routineBlightPlans              bool
	routineClearancePlans           bool
	routineShrinePlans              bool
	routineTidyPlans                bool
	routineStockpilePlans           bool
	routineMoodPlans                bool
	routineHaulPlans                bool
	routineGearPlans                bool
	routineMedicalPlans             bool
	routineFoodStorageUpkeepPlans   bool
	routineRefrigerationPlans       bool
	routineLightingPlans            bool
	routineFlooringPlans            bool
	routineRoutesPlans              bool
	routineAnimalContainmentPlans   bool
	routineRecoveryPlans            bool
	routineHusbandryPlans           bool
	layoutOverlay                   bool
	routinePrisonerInteractionPlans bool
	routinePopulationCustodyPlans   bool
	routinePopulationJoinerPlans    bool
	routineHomeCoveragePlans        bool
	routineStoneShellPlans          bool
	routineDefensiveLayoutPlans     bool
	routineNamingPlans              bool
	routineDialogPlans              bool
	routineTradePlans               bool
	routineResourcePlans            bool
	routineAnimalFeedPlans          bool
	routineMethods                  bool
	worldEvaluation                 bool
	refresh                         time.Duration
	clockTestAcceleration           bool
	clockBlindTicks                 uint
	chat                            bool
	resume                          bool
	pprof                           bool
	debug                           bool
	chatModel                       string
	chatBaseURL                     string
}

// Fixed serve settings that were flags until #875.
const (
	serveRefresh                  = 3 * time.Second // observation refresh interval (tests shorten serveConfig.refresh)
	worldEvaluationFoodMarginDays = 0.5             // caravan food days beyond the home route
	chatContextTokens             = 8192            // approximate model context window
	chatMaxOutputTokens           = 1024            // output tokens per chat completion
)

// routineFamiliesEnv names the environment variable that narrows the routine
// planner families an autonomous serve composes, for targeted/debug runs. It
// is a comma-separated list of the names in routineFamilies; empty or unset
// composes every family.
const routineFamiliesEnv = "RIMGOVERNOR_ROUTINE_FAMILIES"

// lookupEnv is os.LookupEnv, replaceable by tests.
var lookupEnv = os.LookupEnv

func parseServe(args []string, diagnostics io.Writer) (serveConfig, error) {
	var c serveConfig
	flags := flag.NewFlagSet("serve", flag.ContinueOnError)
	flags.SetOutput(diagnostics)
	observe := flags.Bool("observe", false, "observe an already running game without acquiring control or writing to it")
	flags.StringVar(&c.profile, "profile", "", "absolute shared game profile directory (required unless --observe)")
	flags.StringVar(&c.bridge.Launch.StateDir, "config", "", "absolute game configuration directory: config.json describes the launch and the running game's endpoint record lives under it")
	flags.StringVar(&c.bridge.GameID, "game", "", "configured game ID")
	flags.StringVar(&c.state, "state", "", "absolute fresh Go SQLite database path")
	flags.StringVar(&c.assets, "assets", "", "absolute built dashboard directory (optional)")
	flags.StringVar(&c.listen, "listen", "127.0.0.1:0", "loopback IP:port; 0 selects an available port")
	flags.DurationVar(&c.bridge.Timeout, "timeout", 15*time.Second, "native call timeout")
	flags.BoolVar(&c.clockTestAcceleration, "clock-test-acceleration", false, "acceptance only: ask native for its dev tick boost and run every window at Ultrafast whatever the player chose; the game refuses it unless launched with -rimgovernor-test-acceleration (headless acceptance profiles)")
	c.refresh = serveRefresh
	flags.UintVar(&c.clockBlindTicks, "clock-blind-ticks", 0, fmt.Sprintf("arm the native blind-tick regulator (issue #583): past this many game ticks since the controller's last read or oldest unread clock event, native throttles the window toward Normal and ramps back once the controller catches up, without ending the window (1..%d; 0 leaves windows unregulated)", maxClockBlindTicks))
	flags.BoolVar(&c.debug, "debug", false, "log debug records too: the clock trace (which step branch ran, what each planner decided, what a routine refused and why) and refused pawn orders; stderr only, never flight rows")
	flags.BoolVar(&c.pprof, "pprof", false, "serve net/http/pprof under /debug/pprof/ on the listener (CPU profile, heap, trace); off by default")
	flags.StringVar(&c.flightRecorder, "flight-recorder", "", "absolute path of the flight-recorder ring (every native request/response/error and service event; read back by /api/telemetry); default <profile>/flight/flight.jsonl, none under --observe")
	flags.BoolVar(&c.layoutOverlay, "layout-overlay", true, "draw the colony layout plan as a color-coded native overlay with role labels (#817); false deletes the overlay")
	flags.BoolVar(&c.resume, "resume", false, "run the bot for the observed world at startup and again after every native load, without a dashboard Resume")
	flags.StringVar(&c.chatModel, "chat-model", "", "model name as loaded by the local OpenAI-compatible server; enables POST /api/chat")
	flags.StringVar(&c.chatBaseURL, "chat-base-url", "http://127.0.0.1:1234/v1", "local OpenAI-compatible base URL (e.g. LM Studio) chat sends completions to")
	if err := flags.Parse(args); err != nil {
		return c, err
	}
	if flags.NArg() != 0 {
		return c, errors.New("serve takes no positional arguments")
	}
	explicit := map[string]bool{}
	flags.Visit(func(f *flag.Flag) { explicit[f.Name] = true })
	if *observe {
		for _, name := range []string{"profile", "clock-test-acceleration", "chat-model", "chat-base-url", "resume"} {
			if explicit[name] {
				return c, fmt.Errorf("--%s does not apply to --observe", name)
			}
		}
		if _, set := lookupEnv(routineFamiliesEnv); set {
			return c, fmt.Errorf("%s does not apply to --observe", routineFamiliesEnv)
		}
	} else {
		c.playerControl, c.clockControl, c.routineReviews, c.routineMethods = true, true, true, true
		c.worldEvaluation = true
		c.chat = c.chatModel != ""
		if err := c.selectRoutineFamilies(lookupEnv(routineFamiliesEnv)); err != nil {
			return c, err
		}
		if !filepath.IsAbs(c.profile) {
			return c, errors.New("serve requires an absolute --profile (or --observe)")
		}
	}
	if c.clockBlindTicks > maxClockBlindTicks {
		return c, fmt.Errorf("--clock-blind-ticks must be 0 through %d", maxClockBlindTicks)
	}
	if !filepath.IsAbs(c.state) || !filepath.IsAbs(c.bridge.Launch.StateDir) || c.bridge.GameID == "" {
		return c, errors.New("absolute --state, --config and a --game ID are required")
	}
	host, port, err := net.SplitHostPort(c.listen)
	if err != nil || net.ParseIP(host) == nil || !net.ParseIP(host).IsLoopback() {
		return c, errors.New("--listen must use a loopback IP and port")
	}
	if _, err := strconv.ParseUint(port, 10, 16); err != nil || port == "" || strings.Trim(port, "0123456789") != "" {
		return c, errors.New("--listen port must be numeric in 0..65535")
	}
	if c.assets != "" {
		if err := validateAssets(c.assets); err != nil {
			return c, err
		}
	}
	if c.bridge.Timeout < time.Second || c.bridge.Timeout > time.Minute {
		return c, errors.New("--timeout must be 1s..1m")
	}
	if c.flightRecorder != "" && !filepath.IsAbs(c.flightRecorder) {
		return c, errors.New("--flight-recorder requires an absolute path")
	}
	// The recorder is on by default under the profile (#299): a player
	// launch keeps the same evidence the acceptance runner reads, in a ring
	// the profile owns. Acceptance names its per-case path explicitly.
	if c.flightRecorder == "" && c.profile != "" {
		c.flightRecorder = filepath.Join(c.profile, "flight", "flight.jsonl")
	}
	if !c.chat && (explicit["chat-base-url"]) {
		return c, errors.New("--chat-base-url requires --chat-model")
	}
	return c, nil
}

// selectRoutineFamilies enables every routine planner family, or exactly the
// comma-separated names in selection when it is non-empty.
func (c *serveConfig) selectRoutineFamilies(selection string, set bool) error {
	families := routineFamilies(c)
	if !set || strings.TrimSpace(selection) == "" {
		for _, entry := range families {
			*entry.Enabled = true
		}
		return nil
	}
	byName := map[string]*bool{}
	for _, entry := range families {
		byName[entry.Name] = entry.Enabled
	}
	for _, name := range strings.Split(selection, ",") {
		name = strings.TrimSpace(name)
		enabled, ok := byName[name]
		if !ok {
			return fmt.Errorf("%s: unknown routine family %q", routineFamiliesEnv, name)
		}
		*enabled = true
	}
	return nil
}

// routineFamily names one routine planner family alongside a pointer into
// the serveConfig that enables it.
type routineFamily struct {
	Name    string
	Enabled *bool
}

// routineFamilies lists every routine planner family, in composition order.
// It backs the autonomous default (every family on), RIMGOVERNOR_ROUTINE_FAMILIES
// selection and the /api/routines diagnostics family list.
func routineFamilies(c *serveConfig) []routineFamily {
	return []routineFamily{
		{"sleeping", &c.routineSleepingPlans},
		{"bill", &c.routineBillPlans},
		{"field", &c.routineFieldPlans},
		{"food-storage", &c.routineFoodStoragePlans},
		{"acquisition", &c.routineAcquisitionPlans},
		{"work", &c.routineWorkPlans},
		{"supply", &c.routineSupplyPlans},
		{"cooking", &c.routineCookingPlans},
		{"shelter", &c.routineShelterPlans},
		{"comfort", &c.routineComfortPlans},
		{"workshop", &c.routineWorkshopPlans},
		{"research", &c.routineResearchPlans},
		{"ingredient-storage", &c.routineIngredientStoragePlans},
		{"hospital", &c.routineHospitalPlans},
		{"expansion", &c.routineExpansionPlans},
		{"temperature", &c.routineTemperaturePlans},
		{"power", &c.routinePowerPlans},
		{"defense", &c.routineDefensePlans},
		{"tend", &c.routineTendPlans},
		{"rescue", &c.routineRescuePlans},
		{"equip", &c.routineEquipPlans},
		{"secure-supplies", &c.routineSecureSuppliesPlans},
		{"repair", &c.routineRepairPlans},
		{"fire", &c.routineFireSafetyPlans},
		{"clean", &c.routineCleanPlans},
		{"waste", &c.routineWastePlans},
		{"blight", &c.routineBlightPlans},
		{"clearance", &c.routineClearancePlans},
		{"shrine", &c.routineShrinePlans},
		{"tidy", &c.routineTidyPlans},
		{"stockpiles", &c.routineStockpilePlans},
		{"mood", &c.routineMoodPlans},
		{"haul", &c.routineHaulPlans},
		{"gear", &c.routineGearPlans},
		{"medical", &c.routineMedicalPlans},
		{"food-storage-upkeep", &c.routineFoodStorageUpkeepPlans},
		{"refrigeration", &c.routineRefrigerationPlans},
		{"lighting", &c.routineLightingPlans},
		{"flooring", &c.routineFlooringPlans},
		{"routes", &c.routineRoutesPlans},
		{"animal-containment", &c.routineAnimalContainmentPlans},
		{"recovery", &c.routineRecoveryPlans},
		{"husbandry", &c.routineHusbandryPlans},
		{"prisoner-interaction", &c.routinePrisonerInteractionPlans},
		{"population-custody", &c.routinePopulationCustodyPlans},
		{"population-joiner", &c.routinePopulationJoinerPlans},
		{"home-coverage", &c.routineHomeCoveragePlans},
		{"stone-shell", &c.routineStoneShellPlans},
		{"defensive-layout", &c.routineDefensiveLayoutPlans},
		{"naming", &c.routineNamingPlans},
		{"dialog", &c.routineDialogPlans},
		{"trade", &c.routineTradePlans},
		{"resource", &c.routineResourcePlans},
		{"animal-feed", &c.routineAnimalFeedPlans},
	}
}

// footholdComposed reports whether the composition plans every Foothold
// exit criterion (policy.ReviewColonyStage): shelter, cooking, food storage
// and basic defense. Only then can the measured colony stage climb.
func (c serveConfig) footholdComposed() bool {
	return (c.routineSleepingPlans || c.routineShelterPlans) && (c.routineBillPlans || c.routineCookingPlans) &&
		c.routineFoodStoragePlans && (c.routineDefensePlans || c.routineEquipPlans)
}

// researchPlans reports whether EnsureResearch is composed: with the research
// family, which always has the default research ladder (#230).
func (c serveConfig) researchPlans() bool {
	return c.routineResearchPlans
}

func (c serveConfig) workshopPlans() bool {
	return c.routineWorkshopPlans && (c.resourceTargetsConfigured() || c.routineGearPlans)
}

// resourceTargetsConfigured reports whether MaintainResource runs: always
// with the resource family, which keeps the default floors (#875).
func (c serveConfig) resourceTargetsConfigured() bool {
	return c.routineResourcePlans
}

// resourceTargets is the MaintainResource floor map: the defaults
// (#875). Goal-derived needs raise it through EffectiveResourceTargets.
func (c serveConfig) resourceTargets() map[policy.Resource]int64 {
	return policy.DefaultResourceTargets()
}

// activeRoutineFamilies reports the name of every routine planner family this
// configuration enabled, for runtime diagnostics.
func (c serveConfig) activeRoutineFamilies() []string {
	cp := c
	var names []string
	for _, entry := range routineFamilies(&cp) {
		if *entry.Enabled {
			names = append(names, entry.Name)
		}
	}
	return names
}

// Validate before opening the game session or creating state. The HTTP server subsequently
// opens and retains its own confined directory handle for serving.
func validateAssets(directory string) (result error) {
	if !filepath.IsAbs(directory) {
		return errors.New("--assets requires an absolute directory")
	}
	root, err := os.OpenRoot(directory)
	if err != nil {
		return fmt.Errorf("dashboard assets: %w", err)
	}
	defer func() { result = errors.Join(result, root.Close()) }()
	index, err := root.Open("index.html")
	if err != nil {
		return fmt.Errorf("dashboard index: %w", err)
	}
	defer func() { result = errors.Join(result, index.Close()) }()
	info, err := index.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return errors.New("dashboard index must be a regular file")
	}
	return nil
}

func serve(ctx context.Context, args []string, out, diagnostics io.Writer) int {
	config, err := parseServe(args, diagnostics)
	if err != nil {
		fmt.Fprintln(diagnostics, err)
		return 2
	}
	// Service events: every record stamped with time and tick on
	// diagnostics; kinded records also become flight-recorder rows so the
	// scheduler, worker and routine layers sit in sequence with the bridge's
	// native_* rows (#295). Debug records are the clock trace (--debug);
	// they reach stderr only.
	var sink telemetry.Recorder
	if config.flightRecorder != "" {
		recorder, err := bridge.NewFlightRecorder(config.flightRecorder)
		if err != nil {
			fmt.Fprintln(diagnostics, "flight recorder:", err)
			return 1
		}
		defer recorder.Close()
		config.bridge.Recorder = recorder
		sink = recorder
	}
	level := slog.LevelInfo
	if config.debug {
		level = slog.LevelDebug
	}
	slog.SetDefault(telemetry.New(diagnostics, level, sink))
	if config.playerControl {
		err = serveBuildingControl(ctx, config, out)
	} else {
		err = serveReadOnly(ctx, config, out)
	}
	if err != nil {
		fmt.Fprintln(diagnostics, "Go service:", err)
		return 1
	}
	return 0
}

func serveReadOnly(ctx context.Context, config serveConfig, out io.Writer) (result error) {
	return serveWithBridge(ctx, config, out, func(ctx context.Context, c bridge.ProcessConfig) (serviceBridge, error) {
		return openConfigured(ctx, c)
	})
}

type serviceBridge interface {
	observation.Source
	bridgeReattacher
	GamesStart(context.Context) (bridge.Result, error)
	ConnectWithPoll(context.Context, bridge.Result) (bridge.Result, error)
	Close() error
}
type bridgeOpener func(context.Context, bridge.ProcessConfig) (serviceBridge, error)

func serveWithBridge(ctx context.Context, config serveConfig, out io.Writer, open bridgeOpener) (result error) {
	if err := ctx.Err(); err != nil {
		return err
	}
	listener, err := net.Listen("tcp", config.listen)
	if err != nil {
		return err
	}
	defer listener.Close()
	client, err := open(ctx, config.bridge)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, client.Close()) }()
	started, err := client.GamesStart(ctx)
	if err != nil {
		return err
	}
	if _, err = client.ConnectWithPoll(ctx, started); err != nil {
		return err
	}
	database, err := openState(ctx, config.state)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, database.Close()) }()
	var random [16]byte
	if _, err = rand.Read(random[:]); err != nil {
		return err
	}
	snapshots, err := newReadState(hex.EncodeToString(random[:]), client, wallClock{}, 2*config.refresh+config.bridge.Timeout)
	if err != nil {
		return err
	}
	_ = snapshots.Refresh(ctx)
	presentation, _ := client.(httpapi.PresentationReader)
	notifications, _ := client.(httpapi.NotificationReader)
	server, err := httpapi.New(httpapi.Config{Notifications: notifications, Presentation: presentation, AssetsDir: config.assets, Pprof: config.pprof, FlightRecorder: config.flightRecorder, ReadTimeout: 5 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, snapshots, database)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, server.Close()) }()
	pollCtx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})
	go func() { defer close(done); snapshots.Poll(pollCtx, config.refresh) }()
	superviseDone := make(chan struct{})
	go func() { defer close(superviseDone); superviseBridge(pollCtx, client, out) }()
	defer func() { cancel(); <-done; <-superviseDone }()
	if _, err := fmt.Fprintf(out, "RimGovernor Go read-only service: http://%s\n", listener.Addr()); err != nil {
		return err
	}
	return server.Serve(ctx, listener)
}

// openConfigured opens a bridge session whose launch spec is read from
// games.<id> of the --config directory's config.json.
func openConfigured(ctx context.Context, config bridge.ProcessConfig) (*bridge.Client, error) {
	launch, err := bridge.LaunchSpecFromConfig(config.Launch.StateDir, config.GameID)
	if err != nil {
		return nil, err
	}
	config.Launch = launch
	return bridge.Open(ctx, config)
}

// openState opens the service database, replacing one from another schema
// version: saves regenerate, so the old file is kept aside, not migrated.
func openState(ctx context.Context, path string) (*store.Store, error) {
	database, aside, err := store.OpenOrReplace(ctx, path)
	if aside != "" {
		slog.Warn("state database from another schema version moved aside; starting fresh", "path", path, "aside", aside)
	}
	return database, err
}

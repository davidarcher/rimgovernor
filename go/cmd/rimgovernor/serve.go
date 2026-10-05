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
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

type serveConfig struct {
	bridge                         bridge.ProcessConfig
	flightRecorder                 string
	state, listen                  string
	profile                        string
	playerControl                  bool
	clockControl                   bool
	roundsEnabled                  bool
	roundsSleepingPlans            bool
	roundsAcquisitionPlans         bool
	roundsFieldPlans               bool
	roundsBillPlans                bool
	roundsWorkPlans                bool
	roundsSupplyPlans              bool
	roundsCookingPlans             bool
	roundsShelterPlans             bool
	roundsComfortPlans             bool
	roundsWorkshopPlans            bool
	roundsResearchPlans            bool
	roundsHospitalPlans            bool
	roundsExpansionPlans           bool
	roundsPowerPlans               bool
	roundsTemperaturePlans         bool
	roundsDefensePlans             bool
	roundsTendPlans                bool
	roundsRescuePlans              bool
	roundsEquipPlans               bool
	roundsRepairPlans              bool
	roundsFireSafetyPlans          bool
	roundsCleanPlans               bool
	roundsWastePlans               bool
	roundsBlightPlans              bool
	roundsPollutionPlans           bool
	roundsMechChargerPlans         bool
	roundsGeneBankPlans            bool
	roundsArmoryPlans              bool
	roundsClearancePlans           bool
	roundsShrinePlans              bool
	roundsTidyPlans                bool
	roundsStockpilePlans           bool
	roundsMoodPlans                bool
	roundsGearPlans                bool
	roundsMedicalPlans             bool
	roundsFoodStorageUpkeepPlans   bool
	roundsRefrigerationPlans       bool
	roundsLightingPlans            bool
	roundsArtPlans                 bool
	roundsMechPlans                bool
	roundsFlooringPlans            bool
	roundsRoutesPlans              bool
	roundsAnimalContainmentPlans   bool
	roundsRecoveryPlans            bool
	roundsHusbandryPlans           bool
	layoutOverlay                  bool
	roundsPrisonerInteractionPlans bool
	roundsPopulationCustodyPlans   bool
	roundsPopulationJoinerPlans    bool
	roundsHomeCoveragePlans        bool
	roundsShelteringPlans          bool
	roundsFirebreakPlans           bool
	roundsPsylinkPlans             bool
	roundsCreepJoinerPlans         bool
	roundsPermitPlans              bool
	roundsIdeoRolePlans            bool
	roundsRitualPlans              bool
	roundsStoneShellPlans          bool
	roundsDefensiveLayoutPlans     bool
	roundsNamingPlans              bool
	roundsDialogPlans              bool
	roundsTradePlans               bool
	roundsResourcePlans            bool
	roundsAnimalFeedPlans          bool
	roundsMethods                  bool
	refresh                        time.Duration
	clockTestAcceleration          bool
	followPlayerSpeed              bool
	clockBlindTicks                uint
	resume                         bool
	pprof                          bool
	debug                          bool
}

// Fixed serve settings that were flags until #875.
const (
	serveRefresh = 3 * time.Second // observation refresh interval (tests shorten serveConfig.refresh)
)

// roundsFamiliesEnv names the environment variable that narrows the routine
// planner families an autonomous serve composes, for targeted/debug runs. It
// is a comma-separated list of the names in roundsFamilies; empty or unset
// composes every family.
const roundsFamiliesEnv = "RIMGOVERNOR_ROUTINE_FAMILIES"

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
	flags.StringVar(&c.listen, "listen", "127.0.0.1:0", "loopback IP:port; 0 selects an available port")
	flags.DurationVar(&c.bridge.Timeout, "timeout", 15*time.Second, "native call timeout")
	flags.BoolVar(&c.clockTestAcceleration, "clock-test-acceleration", false, "acceptance only: ask native for its dev tick boost and run every window at Ultrafast whatever the player chose; the game refuses it unless launched with -rimgovernor-test-acceleration (headless acceptance profiles)")
	flags.BoolVar(&c.followPlayerSpeed, "follow-player-speed", false, "run each window at the speed the player last chose in game instead of always Ultrafast; acceptance harnesses that pin a slower speed use it")
	c.refresh = serveRefresh
	flags.UintVar(&c.clockBlindTicks, "clock-blind-ticks", 0, fmt.Sprintf("arm the native blind-tick regulator (issue #583): past this many game ticks since the controller's last read or oldest unread clock event, native throttles the window toward Normal and ramps back once the controller catches up, without ending the window (1..%d; 0 leaves windows unregulated)", maxClockBlindTicks))
	flags.BoolVar(&c.debug, "debug", false, "log debug records too: the clock trace (which step branch ran, what each planner decided, what a routine refused and why) and refused pawn orders; stderr only, never flight rows")
	flags.BoolVar(&c.pprof, "pprof", false, "serve net/http/pprof under /debug/pprof/ on the listener (CPU profile, heap, trace); off by default")
	flags.StringVar(&c.flightRecorder, "flight-recorder", "", "absolute path of the flight-recorder ring (every native request/response/error and service event; read back by /api/telemetry); default <profile>/flight/flight.jsonl, none under --observe")
	flags.BoolVar(&c.layoutOverlay, "layout-overlay", true, "draw the colony layout plan as a color-coded native overlay with role labels (#817); false deletes the overlay")
	flags.BoolVar(&c.resume, "resume", false, "run the bot for the observed world at startup and again after every native load, without a launcher Resume")
	if err := flags.Parse(args); err != nil {
		return c, err
	}
	if flags.NArg() != 0 {
		return c, errors.New("serve takes no positional arguments")
	}
	explicit := map[string]bool{}
	flags.Visit(func(f *flag.Flag) { explicit[f.Name] = true })
	if *observe {
		for _, name := range []string{"profile", "clock-test-acceleration", "follow-player-speed", "resume"} {
			if explicit[name] {
				return c, fmt.Errorf("--%s does not apply to --observe", name)
			}
		}
		if _, set := lookupEnv(roundsFamiliesEnv); set {
			return c, fmt.Errorf("%s does not apply to --observe", roundsFamiliesEnv)
		}
	} else {
		c.playerControl, c.clockControl, c.roundsEnabled, c.roundsMethods = true, true, true, true
		if err := c.selectRoundsFamilies(lookupEnv(roundsFamiliesEnv)); err != nil {
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
	return c, nil
}

// selectRoundsFamilies enables every routine planner family, or exactly the
// comma-separated names in selection when it is non-empty.
func (c *serveConfig) selectRoundsFamilies(selection string, set bool) error {
	families := roundsFamilies(c)
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
			return fmt.Errorf("%s: unknown routine family %q", roundsFamiliesEnv, name)
		}
		*enabled = true
	}
	return nil
}

// roundsFamily names one routine planner family alongside a pointer into
// the serveConfig that enables it.
type roundsFamily struct {
	Name    string
	Enabled *bool
}

// roundsFamilies lists every routine planner family, in composition order.
// It backs the autonomous default (every family on), RIMGOVERNOR_ROUTINE_FAMILIES
// selection and the /api/routines diagnostics family list.
func roundsFamilies(c *serveConfig) []roundsFamily {
	return []roundsFamily{
		{"sleeping", &c.roundsSleepingPlans},
		{"bill", &c.roundsBillPlans},
		{"field", &c.roundsFieldPlans},
		{"acquisition", &c.roundsAcquisitionPlans},
		{"work", &c.roundsWorkPlans},
		{"supply", &c.roundsSupplyPlans},
		{"cooking", &c.roundsCookingPlans},
		{"shelter", &c.roundsShelterPlans},
		{"comfort", &c.roundsComfortPlans},
		{"workshop", &c.roundsWorkshopPlans},
		{"research", &c.roundsResearchPlans},
		{"hospital", &c.roundsHospitalPlans},
		{"expansion", &c.roundsExpansionPlans},
		{"temperature", &c.roundsTemperaturePlans},
		{"power", &c.roundsPowerPlans},
		{"defense", &c.roundsDefensePlans},
		{"tend", &c.roundsTendPlans},
		{"rescue", &c.roundsRescuePlans},
		{"equip", &c.roundsEquipPlans},
		{"repair", &c.roundsRepairPlans},
		{"fire", &c.roundsFireSafetyPlans},
		{"clean", &c.roundsCleanPlans},
		{"waste", &c.roundsWastePlans},
		{"blight", &c.roundsBlightPlans},
		{"pollution", &c.roundsPollutionPlans},
		{"mechcharger", &c.roundsMechChargerPlans},
		{"genebank", &c.roundsGeneBankPlans},
		{"armory", &c.roundsArmoryPlans},
		{"clearance", &c.roundsClearancePlans},
		{"shrine", &c.roundsShrinePlans},
		{"tidy", &c.roundsTidyPlans},
		{"stockpiles", &c.roundsStockpilePlans},
		{"mood", &c.roundsMoodPlans},
		{"gear", &c.roundsGearPlans},
		{"medical", &c.roundsMedicalPlans},
		{"food-storage-upkeep", &c.roundsFoodStorageUpkeepPlans},
		{"refrigeration", &c.roundsRefrigerationPlans},
		{"lighting", &c.roundsLightingPlans},
		{"art", &c.roundsArtPlans},
		{"mechs", &c.roundsMechPlans},
		{"flooring", &c.roundsFlooringPlans},
		{"routes", &c.roundsRoutesPlans},
		{"animal-containment", &c.roundsAnimalContainmentPlans},
		{"recovery", &c.roundsRecoveryPlans},
		{"husbandry", &c.roundsHusbandryPlans},
		{"prisoner-interaction", &c.roundsPrisonerInteractionPlans},
		{"population-custody", &c.roundsPopulationCustodyPlans},
		{"population-joiner", &c.roundsPopulationJoinerPlans},
		{"home-coverage", &c.roundsHomeCoveragePlans},
		{"sheltering", &c.roundsShelteringPlans},
		{"firebreak", &c.roundsFirebreakPlans},
		{"psylink", &c.roundsPsylinkPlans},
		{"creepjoiner", &c.roundsCreepJoinerPlans},
		{"permits", &c.roundsPermitPlans},
		{"ideo-roles", &c.roundsIdeoRolePlans},
		{"rituals", &c.roundsRitualPlans},
		{"stone-shell", &c.roundsStoneShellPlans},
		{"defensive-layout", &c.roundsDefensiveLayoutPlans},
		{"naming", &c.roundsNamingPlans},
		{"dialog", &c.roundsDialogPlans},
		{"trade", &c.roundsTradePlans},
		{"resource", &c.roundsResourcePlans},
		{"animal-feed", &c.roundsAnimalFeedPlans},
	}
}

// footholdComposed reports whether the composition plans every Foothold
// exit criterion (policy.ReviewColonyStage): shelter, cooking, food storage
// and basic defense. Only then can the measured colony stage climb.
func (c serveConfig) footholdComposed() bool {
	return (c.roundsSleepingPlans || c.roundsShelterPlans) && (c.roundsBillPlans || c.roundsCookingPlans) &&
		c.roundsStockpilePlans && (c.roundsDefensePlans || c.roundsEquipPlans)
}

// researchPlans reports whether EnsureResearch is composed: with the research
// family, which always has the default research ladder (#230).
func (c serveConfig) researchPlans() bool {
	return c.roundsResearchPlans
}

func (c serveConfig) workshopPlans() bool {
	return c.roundsWorkshopPlans && (c.resourceTargetsConfigured() || c.roundsGearPlans)
}

// resourceTargetsConfigured reports whether MaintainResource runs: always
// with the resource family, which keeps the default floors (#875).
func (c serveConfig) resourceTargetsConfigured() bool {
	return c.roundsResourcePlans
}

// resourceTargets is the MaintainResource floor map: the defaults
// (#875). Goal-derived needs raise it through EffectiveResourceTargets.
func (c serveConfig) resourceTargets() map[policy.Resource]int64 {
	return policy.DefaultResourceTargets()
}

// activeRoundsFamilies reports the name of every routine planner family this
// configuration enabled, for runtime diagnostics.
func (c serveConfig) activeRoundsFamilies() []string {
	cp := c
	var names []string
	for _, entry := range roundsFamilies(&cp) {
		if *entry.Enabled {
			names = append(names, entry.Name)
		}
	}
	return names
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
	defer snapshot.Flush()
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
	// The supervisor ends the service when the player closes the game.
	ctx, closed := context.WithCancel(ctx)
	defer closed()
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
	presentation, notifications, err := requirePresentationReaders(client)
	if err != nil {
		return err
	}
	server, err := httpapi.New(httpapi.Config{Notifications: notifications, Presentation: presentation, Pprof: config.pprof, FlightRecorder: config.flightRecorder, ReadTimeout: 5 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, snapshots, database)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, server.Close()) }()
	pollCtx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})
	go func() { defer close(done); snapshots.Poll(pollCtx, config.refresh) }()
	superviseDone := make(chan struct{})
	go func() { defer close(superviseDone); superviseBridge(pollCtx, client, out, closed) }()
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

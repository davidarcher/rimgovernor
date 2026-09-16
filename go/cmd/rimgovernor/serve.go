package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
	routineExpansionPlans           bool
	routinePowerPlans               bool
	routineTemperaturePlans         bool
	routineDefensePlans             bool
	routineTendPlans                bool
	routineRescuePlans              bool
	routineEquipPlans               bool
	routineSecureSuppliesPlans      bool
	routineRepairPlans              bool
	routineCleanPlans               bool
	routineWastePlans               bool
	routineMoodPlans                bool
	routineHaulPlans                bool
	routineGearPlans                bool
	routineMedicalPlans             bool
	routineFoodStorageUpkeepPlans   bool
	routineRefrigerationPlans       bool
	routineAnimalContainmentPlans   bool
	routineRecoveryPlans            bool
	routineHusbandryPlans           bool
	routineAllowSlaughter           bool
	routineHerdPopulationMax        herdPopulationMaxFlags
	routinePrisonerInteractionPlans bool
	routinePopulationCustodyPlans   bool
	routineHomeCoveragePlans        bool
	routineStoneShellPlans          bool
	routineDefensiveLayoutPlans     bool
	routineNamingPlans              bool
	routineResearchTarget           string
	routineResourcePlans            bool
	routineResourceTargets          resourceTargetFlags
	routineAnimalFeedPlans          bool
	routineProductionPolicyPlans    bool
	routineResourceReserves         resourceReserveFlags
	routineStoppedResources         stoppedResourceFlags
	routineMethods                  bool
	routineProjectLimit             int
	caravanJourneyTracking          bool
	worldEvaluation                 bool
	worldEvaluationFoodMarginDays   float64
	resourceRules                   resourceRuleFlags
	refresh                         time.Duration
	clockSpeed                      string
	chat                            bool
	resume                          bool
	chatModel                       string
	chatBaseURL                     string
	chatContextTokens               int
	chatMaxOutputTokens             int
}

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
	flags.StringVar(&c.bridge.Executable, "gabs", "", "absolute GABS executable")
	flags.StringVar(&c.bridge.ConfigDir, "config", "", "absolute GABS configuration directory")
	flags.StringVar(&c.bridge.GameID, "game", "", "configured game ID")
	flags.StringVar(&c.state, "state", "", "absolute fresh Go SQLite database path")
	flags.StringVar(&c.assets, "assets", "", "absolute built dashboard directory (optional)")
	flags.StringVar(&c.listen, "listen", "127.0.0.1:0", "loopback IP:port; 0 selects an available port")
	flags.DurationVar(&c.refresh, "refresh", 3*time.Second, "observation refresh interval")
	flags.DurationVar(&c.bridge.Timeout, "timeout", 15*time.Second, "native call timeout")
	flags.StringVar(&c.clockSpeed, "clock-speed", "Normal", "requested native game-clock speed while a supervised window is held: Normal, Fast or Superfast")
	flags.StringVar(&c.flightRecorder, "flight-recorder", "", "absolute path recording every native request/response/error (optional; opt-in diagnostics)")
	flags.IntVar(&c.routineProjectLimit, "routine-project-limit", 2, "maximum concurrent optional projects, also bounded by observed workers (1..8)")
	flags.StringVar(&c.routineResearchTarget, "routine-research-target", "", "native ResearchProjectDef name EnsureResearch selects prerequisite-ordered toward once no research project is current")
	flags.Var(&c.routineResourceTargets, "routine-resource-target", "repeatable RESOURCE:TARGET native stock floor MaintainResource dispatches a production bill toward")
	flags.Var(&c.routineResourceReserves, "routine-resource-reserve", "repeatable RESOURCE:FLOOR native stock floor ProductionPolicy replaces into the current native production policy")
	flags.Var(&c.routineStoppedResources, "routine-resource-stop", "repeatable RESOURCE name ProductionPolicy keeps stopped in the current native production policy")
	flags.BoolVar(&c.routineAllowSlaughter, "routine-allow-slaughter", false, "let MaintainHerd propose a slaughter write for a surplus animal once --routine-herd-population-max is declared; slaughter is irreversible and stays off unless explicitly set")
	flags.Var(&c.routineHerdPopulationMax, "routine-herd-population-max", "repeatable RACE:MAX native animal definition population ceiling MaintainHerd slaughters surplus toward, only once --routine-allow-slaughter is also set")
	flags.Var(&c.resourceRules, "resource-rule", "repeatable RESOURCE:allow|stop|defense_only:RESERVE for building admission and dispatch")
	flags.Float64Var(&c.worldEvaluationFoodMarginDays, "world-evaluation-food-margin-days", 0.5, "days of caravan food required beyond its home route's estimated travel time before it is reported as needing recovery")
	flags.BoolVar(&c.resume, "resume", false, "run the bot for the observed world at startup and again after every native load, without a dashboard Resume")
	flags.StringVar(&c.chatModel, "chat-model", "", "model name as loaded by the local OpenAI-compatible server; enables POST /api/chat")
	flags.StringVar(&c.chatBaseURL, "chat-base-url", "http://127.0.0.1:1234/v1", "local OpenAI-compatible base URL (e.g. LM Studio) chat sends completions to")
	flags.IntVar(&c.chatContextTokens, "chat-context-tokens", 8192, "approximate model context window chat budgets prompts against (4096..16777216)")
	flags.IntVar(&c.chatMaxOutputTokens, "chat-max-output-tokens", 1024, "maximum output tokens chat requests per completion")
	if err := flags.Parse(args); err != nil {
		return c, err
	}
	if flags.NArg() != 0 {
		return c, errors.New("serve takes no positional arguments")
	}
	explicit := map[string]bool{}
	flags.Visit(func(f *flag.Flag) { explicit[f.Name] = true })
	if *observe {
		for _, name := range []string{"profile", "clock-speed", "routine-project-limit", "routine-research-target", "routine-resource-target", "routine-resource-reserve", "routine-resource-stop", "routine-allow-slaughter", "routine-herd-population-max", "resource-rule", "world-evaluation-food-margin-days", "chat-model", "chat-base-url", "chat-context-tokens", "chat-max-output-tokens", "resume"} {
			if explicit[name] {
				return c, fmt.Errorf("--%s does not apply to --observe", name)
			}
		}
		if _, set := lookupEnv(routineFamiliesEnv); set {
			return c, fmt.Errorf("%s does not apply to --observe", routineFamiliesEnv)
		}
	} else {
		c.playerControl, c.clockControl, c.routineReviews, c.routineMethods = true, true, true, true
		c.caravanJourneyTracking, c.worldEvaluation = true, true
		c.chat = c.chatModel != ""
		if err := c.selectRoutineFamilies(lookupEnv(routineFamiliesEnv)); err != nil {
			return c, err
		}
		if !filepath.IsAbs(c.profile) {
			return c, errors.New("serve requires an absolute --profile (or --observe)")
		}
	}
	if c.routineProjectLimit < 1 || c.routineProjectLimit > 8 {
		return c, errors.New("--routine-project-limit must be 1 through 8")
	}
	if c.clockSpeed != "Normal" && c.clockSpeed != "Fast" && c.clockSpeed != "Superfast" {
		return c, errors.New("--clock-speed must be Normal, Fast or Superfast")
	}
	if c.worldEvaluationFoodMarginDays < 0 {
		return c, errors.New("--world-evaluation-food-margin-days must be non-negative")
	}
	if len(c.routineResourceTargets) > 0 && !c.routineResourcePlans {
		return c, errors.New("--routine-resource-target requires the resource routine family")
	}
	if (len(c.routineResourceReserves) > 0 || len(c.routineStoppedResources) > 0) && !c.routineProductionPolicyPlans {
		return c, errors.New("--routine-resource-reserve and --routine-resource-stop require the production-policy routine family")
	}
	if (c.routineAllowSlaughter || len(c.routineHerdPopulationMax) > 0) && !c.routineHusbandryPlans {
		return c, errors.New("--routine-allow-slaughter and --routine-herd-population-max require the husbandry routine family")
	}
	if !filepath.IsAbs(c.state) || !filepath.IsAbs(c.bridge.Executable) || !filepath.IsAbs(c.bridge.ConfigDir) || c.bridge.GameID == "" {
		return c, errors.New("absolute --state, --gabs, --config and a --game ID are required")
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
	if c.refresh < 500*time.Millisecond || c.refresh > time.Minute || c.bridge.Timeout < time.Second || c.bridge.Timeout > time.Minute {
		return c, errors.New("refresh must be 500ms..1m and timeout 1s..1m")
	}
	if c.flightRecorder != "" && !filepath.IsAbs(c.flightRecorder) {
		return c, errors.New("--flight-recorder requires an absolute path")
	}
	if !c.chat && (explicit["chat-base-url"] || explicit["chat-context-tokens"] || explicit["chat-max-output-tokens"]) {
		return c, errors.New("--chat-base-url, --chat-context-tokens and --chat-max-output-tokens require --chat-model")
	}
	if c.chatContextTokens < 4096 || c.chatContextTokens > 1<<24 {
		return c, errors.New("--chat-context-tokens must be 4096..16777216")
	}
	if c.chatMaxOutputTokens < 1 || c.chatMaxOutputTokens >= c.chatContextTokens-2048 {
		return c, errors.New("--chat-max-output-tokens must be positive and leave room under --chat-context-tokens")
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
		{"expansion", &c.routineExpansionPlans},
		{"temperature", &c.routineTemperaturePlans},
		{"power", &c.routinePowerPlans},
		{"defense", &c.routineDefensePlans},
		{"tend", &c.routineTendPlans},
		{"rescue", &c.routineRescuePlans},
		{"equip", &c.routineEquipPlans},
		{"secure-supplies", &c.routineSecureSuppliesPlans},
		{"repair", &c.routineRepairPlans},
		{"clean", &c.routineCleanPlans},
		{"waste", &c.routineWastePlans},
		{"mood", &c.routineMoodPlans},
		{"haul", &c.routineHaulPlans},
		{"gear", &c.routineGearPlans},
		{"medical", &c.routineMedicalPlans},
		{"food-storage-upkeep", &c.routineFoodStorageUpkeepPlans},
		{"refrigeration", &c.routineRefrigerationPlans},
		{"animal-containment", &c.routineAnimalContainmentPlans},
		{"recovery", &c.routineRecoveryPlans},
		{"husbandry", &c.routineHusbandryPlans},
		{"prisoner-interaction", &c.routinePrisonerInteractionPlans},
		{"population-custody", &c.routinePopulationCustodyPlans},
		{"home-coverage", &c.routineHomeCoveragePlans},
		{"stone-shell", &c.routineStoneShellPlans},
		{"defensive-layout", &c.routineDefensiveLayoutPlans},
		{"naming", &c.routineNamingPlans},
		{"resource", &c.routineResourcePlans},
		{"animal-feed", &c.routineAnimalFeedPlans},
		{"production-policy", &c.routineProductionPolicyPlans},
	}
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

// Validate before opening GABS or creating state. The HTTP server subsequently
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
	if config.flightRecorder != "" {
		recorder, err := bridge.NewFlightRecorder(config.flightRecorder)
		if err != nil {
			fmt.Fprintln(diagnostics, "flight recorder:", err)
			return 1
		}
		defer recorder.Close()
		config.bridge.Recorder = recorder
	}
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
	return serveWithBridge(ctx, config, out, func(ctx context.Context, c bridge.ProcessConfig) (serviceBridge, error) { return bridge.Open(ctx, c) })
}

type serviceBridge interface {
	observation.Source
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
	database, err := store.Open(ctx, config.state)
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
	server, err := httpapi.New(httpapi.Config{Notifications: notifications, Presentation: presentation, AssetsDir: config.assets, ReadTimeout: 5 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, snapshots, database)
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, server.Close()) }()
	pollCtx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})
	go func() { defer close(done); snapshots.Poll(pollCtx, config.refresh) }()
	defer func() { cancel(); <-done }()
	if _, err := fmt.Fprintf(out, "RimGovernor Go read-only service: http://%s\n", listener.Addr()); err != nil {
		return err
	}
	return server.Serve(ctx, listener)
}

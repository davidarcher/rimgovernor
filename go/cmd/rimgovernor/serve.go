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
	"math"
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
	routineAllowSlaughter           bool
	routineAllowRelease             bool
	routineHerdPopulationMax        herdPopulationMaxFlags
	routineHerdPopulationMin        herdPopulationMaxFlags
	routinePrisonerInteractionPlans bool
	routinePrisonerReleaseAfterDays float64
	routinePopulationCustodyPlans   bool
	routineHomeCoveragePlans        bool
	routineStoneShellPlans          bool
	routineDefensiveLayoutPlans     bool
	routineNamingPlans              bool
	routineDialogPlans              bool
	routineTradePlans               bool
	routineSilverReserve            int64
	routineComponentTarget          int64
	routineDialogPrefer             string
	routineResearchTarget           string
	routineResearchLadder           string
	routineResourcePlans            bool
	routineResourceTargets          resourceTargetFlags
	routineStoneBlockTarget         int64
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
	clockTestAcceleration           bool
	clockWindowTicks                uint
	clockWindowSeconds              float64
	chat                            bool
	resume                          bool
	pprof                           bool
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
	flags.StringVar(&c.clockSpeed, "clock-speed", "Normal", "requested native game-clock speed while a supervised window is held: Normal, Fast, Superfast or Ultrafast")
	flags.BoolVar(&c.clockTestAcceleration, "clock-test-acceleration", false, "acceptance only: ask native for its dev tick boost under each Ultrafast window; the game refuses it unless launched with -rimgovernor-test-acceleration (headless acceptance profiles)")
	flags.UintVar(&c.clockWindowTicks, "clock-window-ticks", defaultClockWindowTicks, fmt.Sprintf("game ticks one supervised colony window may run before it pauses for review (1..%d); a watched outcome, danger or player input still stops it earlier", maxClockWindowTicks))
	flags.Float64Var(&c.clockWindowSeconds, "clock-window-seconds", defaultClockWindowSeconds, fmt.Sprintf("least wall seconds a supervised colony window should run at --clock-speed: the window grows past --clock-window-ticks to the ticks the speed runs in this time, or in the pause observed between windows if longer, up to %d ticks; 0 keeps --clock-window-ticks fixed", maxClockWindowTicks))
	flags.BoolVar(&c.pprof, "pprof", false, "serve net/http/pprof under /debug/pprof/ on the listener (CPU profile, heap, trace); off by default")
	flags.StringVar(&c.flightRecorder, "flight-recorder", "", "absolute path recording every native request/response/error (optional; opt-in diagnostics)")
	flags.IntVar(&c.routineProjectLimit, "routine-project-limit", 2, "maximum concurrent optional projects, also bounded by observed workers (1..8)")
	flags.StringVar(&c.routineDialogPrefer, "routine-dialog-prefer", strings.Join(policy.DefaultDialogAnswerPrefer, ","), "comma-separated option patterns AnswerDialog prefers when a force-pausing choice dialog is open: each matches an option's Keyed translation key exactly or its label as a case-insensitive substring, first match wins; the first selectable resolving option otherwise")
	flags.Int64Var(&c.routineSilverReserve, "routine-silver-reserve", 0, "silver TradeWithCaravan never spends below when buying from a caravan")
	flags.Int64Var(&c.routineComponentTarget, "routine-component-target", 0, "ComponentIndustrial stock TradeWithCaravan buys toward and, with the resource family, MaintainResource mines toward; 0 tracks no component target")
	flags.StringVar(&c.routineResearchTarget, "routine-research-target", "", "native ResearchProjectDef name EnsureResearch selects prerequisite-ordered toward once no research project is current")
	flags.StringVar(&c.routineResearchLadder, "routine-research-ladder", strings.Join(policy.DefaultResearchLadder(), ","), "comma-separated ResearchProjectDef names EnsureResearch walks in order when no --routine-research-target is set and no workshop ladder records a need; empty disables the roadmap")
	flags.Var(&c.routineResourceTargets, "routine-resource-target", "repeatable RESOURCE:TARGET native stock floor MaintainResource dispatches a production bill toward")
	flags.Int64Var(&c.routineStoneBlockTarget, "routine-stone-block-target", 0, "native stock floor MaintainResource keeps for stone blocks of the stone whose chunks the map counts most, staging a stonecutter's table and a do-until bill fed from those chunks; 0 disables")
	flags.Var(&c.routineResourceReserves, "routine-resource-reserve", "repeatable RESOURCE:FLOOR native stock floor ProductionPolicy replaces into the current native production policy")
	flags.Var(&c.routineStoppedResources, "routine-resource-stop", "repeatable RESOURCE name ProductionPolicy keeps stopped in the current native production policy")
	flags.BoolVar(&c.routineAllowSlaughter, "routine-allow-slaughter", false, "let MaintainHerd propose a slaughter write for a surplus animal once --routine-herd-population-max is declared; slaughter is irreversible and stays off unless explicitly set")
	flags.BoolVar(&c.routineAllowRelease, "routine-allow-release", false, "let MaintainHerd propose a release-to-wild write for a surplus animal once --routine-herd-population-max is declared; preferred over slaughter when both are set")
	flags.Var(&c.routineHerdPopulationMax, "routine-herd-population-max", "repeatable RACE:MAX native animal definition population ceiling MaintainHerd removes surplus toward, only once --routine-allow-release or --routine-allow-slaughter is also set")
	flags.Var(&c.routineHerdPopulationMin, "routine-herd-population-min", "repeatable RACE:MIN native animal definition population floor MaintainHerd designates tameable wild animals toward")
	flags.Float64Var(&c.routinePrisonerReleaseAfterDays, "routine-prisoner-release-after-days", 0, "days in custody after which MaintainPopulation proposes releasing a prisoner whose recruit resistance is unbroken (or who was never recruitable) while the colony food runway is below its routine target; 0 (the default) never releases")
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
		for _, name := range []string{"profile", "clock-speed", "clock-test-acceleration", "clock-window-ticks", "clock-window-seconds", "routine-project-limit", "routine-dialog-prefer", "routine-research-target", "routine-research-ladder", "routine-resource-target", "routine-resource-reserve", "routine-resource-stop", "routine-allow-slaughter", "routine-herd-population-max", "resource-rule", "world-evaluation-food-margin-days", "chat-model", "chat-base-url", "chat-context-tokens", "chat-max-output-tokens", "resume"} {
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
	if c.clockSpeed != "Normal" && c.clockSpeed != "Fast" && c.clockSpeed != "Superfast" && c.clockSpeed != "Ultrafast" {
		return c, errors.New("--clock-speed must be Normal, Fast, Superfast or Ultrafast")
	}
	if c.clockTestAcceleration && c.clockSpeed != "Ultrafast" {
		return c, errors.New("--clock-test-acceleration requires --clock-speed Ultrafast")
	}
	if c.clockWindowTicks < 1 || c.clockWindowTicks > maxClockWindowTicks {
		return c, fmt.Errorf("--clock-window-ticks must be 1 through %d", maxClockWindowTicks)
	}
	if c.clockWindowSeconds < 0 || math.IsNaN(c.clockWindowSeconds) || math.IsInf(c.clockWindowSeconds, 0) {
		return c, errors.New("--clock-window-seconds must be non-negative")
	}
	if c.worldEvaluationFoodMarginDays < 0 {
		return c, errors.New("--world-evaluation-food-margin-days must be non-negative")
	}
	if (c.routineSilverReserve != 0 || c.routineComponentTarget != 0) && !c.routineTradePlans {
		return c, errors.New("--routine-silver-reserve and --routine-component-target require the trade routine family")
	}
	if c.routineSilverReserve < 0 || c.routineComponentTarget < 0 {
		return c, errors.New("--routine-silver-reserve and --routine-component-target must be non-negative")
	}
	if c.routineComponentTarget > 0 && c.routineResourcePlans {
		if _, set := c.routineResourceTargets[policy.ComponentResource]; !set {
			if err := c.routineResourceTargets.Set(fmt.Sprintf("%s:%d", policy.ComponentResource, c.routineComponentTarget)); err != nil {
				return c, err
			}
		}
	}
	if c.routineStoneBlockTarget < 0 || c.routineStoneBlockTarget > 10000 {
		return c, errors.New("--routine-stone-block-target must be within 0..10000")
	}
	if c.resourceTargetsConfigured() && !c.routineResourcePlans {
		return c, errors.New("--routine-resource-target and --routine-stone-block-target require the resource routine family")
	}
	if (len(c.routineResourceReserves) > 0 || len(c.routineStoppedResources) > 0) && !c.routineProductionPolicyPlans {
		return c, errors.New("--routine-resource-reserve and --routine-resource-stop require the production-policy routine family")
	}
	if (c.routineAllowSlaughter || c.routineAllowRelease || len(c.routineHerdPopulationMax) > 0 || len(c.routineHerdPopulationMin) > 0) && !c.routineHusbandryPlans {
		return c, errors.New("--routine-allow-slaughter, --routine-allow-release, --routine-herd-population-max and --routine-herd-population-min require the husbandry routine family")
	}
	if c.routinePrisonerReleaseAfterDays != 0 && !c.routinePrisonerInteractionPlans {
		return c, errors.New("--routine-prisoner-release-after-days requires the prisoner-interaction routine family")
	}
	for race, minimum := range c.routineHerdPopulationMin {
		if max, ok := c.routineHerdPopulationMax[race]; ok && minimum > max {
			return c, errors.New("--routine-herd-population-min exceeds --routine-herd-population-max for " + string(race))
		}
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
		{"home-coverage", &c.routineHomeCoveragePlans},
		{"stone-shell", &c.routineStoneShellPlans},
		{"defensive-layout", &c.routineDefensiveLayoutPlans},
		{"naming", &c.routineNamingPlans},
		{"dialog", &c.routineDialogPlans},
		{"trade", &c.routineTradePlans},
		{"resource", &c.routineResourcePlans},
		{"animal-feed", &c.routineAnimalFeedPlans},
		{"production-policy", &c.routineProductionPolicyPlans},
	}
}

// researchPlans reports whether EnsureResearch is composed: an operator
// target always is; the research family follows the projects the workshop
// ladder records for a MaintainResource bench (issue #4 M4) and otherwise
// the research ladder (#230).
func (c serveConfig) researchPlans() bool {
	return c.routineResearchTarget != "" || c.routineResearchPlans && (c.resourceTargetsConfigured() || len(c.researchLadder()) > 0)
}

// resourceTargetsConfigured reports whether MaintainResource has a floor to
// keep: an operator resource target or the derived stone-block target.
func (c serveConfig) resourceTargetsConfigured() bool {
	return len(c.routineResourceTargets) > 0 || c.routineStoneBlockTarget > 0
}

// researchLadder is --routine-research-ladder split, blanks dropped.
func (c serveConfig) researchLadder() []string {
	var ladder []string
	for _, name := range strings.Split(c.routineResearchLadder, ",") {
		if name = strings.TrimSpace(name); name != "" {
			ladder = append(ladder, name)
		}
	}
	return ladder
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
	// Service events: every record stamped with time and tick on
	// diagnostics; kinded records also become flight-recorder rows so the
	// scheduler, worker and routine layers sit in sequence with the bridge's
	// native_* rows (#295). Debug records are the clock trace
	// (RIMGOVERNOR_CLOCK_DEBUG); they reach stderr only.
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
	if os.Getenv("RIMGOVERNOR_CLOCK_DEBUG") != "" {
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
	return serveWithBridge(ctx, config, out, func(ctx context.Context, c bridge.ProcessConfig) (serviceBridge, error) { return bridge.Open(ctx, c) })
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
	server, err := httpapi.New(httpapi.Config{Notifications: notifications, Presentation: presentation, AssetsDir: config.assets, Pprof: config.pprof, ReadTimeout: 5 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, snapshots, database)
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

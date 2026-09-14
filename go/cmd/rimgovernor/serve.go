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
	"github.com/davidarcher/RimGovernor/go/internal/controller"
	"github.com/davidarcher/RimGovernor/go/internal/flightrecorder"
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
	routineHaulPlans                bool
	routineGearPlans                bool
	routineMedicalPlans             bool
	routineAnimalContainmentPlans   bool
	routineRecoveryPlans            bool
	routineHusbandryPlans           bool
	routinePrisonerInteractionPlans bool
	routinePopulationCustodyPlans   bool
	routineHomeCoveragePlans        bool
	routineStoneShellPlans          bool
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
	resourceRules                   resourceRuleFlags
	refresh                         time.Duration
}

func parseServe(args []string, diagnostics io.Writer) (serveConfig, error) {
	var c serveConfig
	flags := flag.NewFlagSet("serve", flag.ContinueOnError)
	flags.SetOutput(diagnostics)
	readOnly := flags.Bool("read-only", false, "observe an already running game; game writes are unavailable")
	flags.BoolVar(&c.playerControl, "player-control", false, "enable explicit player building and draft controls; never acquire on startup")
	flags.BoolVar(&c.clockControl, "clock-control", false, "supervise finite game-clock windows for enabled player work")
	flags.BoolVar(&c.routineReviews, "routine-reviews", false, "review routine needs at paused clock boundaries")
	flags.IntVar(&c.routineProjectLimit, "routine-project-limit", 2, "maximum concurrent optional projects, also bounded by observed workers (1..8)")
	flags.BoolVar(&c.routineSleepingPlans, "routine-sleeping-plans", false, "compile reviewed indoor sleeping needs into pending shared plans")
	flags.BoolVar(&c.routineBillPlans, "routine-bill-plans", false, "compile ordinary cooking, preservation and butcher bills with bench prerequisites")
	flags.BoolVar(&c.routineFieldPlans, "routine-field-plans", false, "compile native crop selection and protected growing patches into shared plans")
	flags.BoolVar(&c.routineFoodStoragePlans, "routine-food-storage-plans", false, "compile a protected food stockpile zone into the completed starter shell through shared plans")
	flags.BoolVar(&c.routineAcquisitionPlans, "routine-acquisition-plans", false, "compile safe food harvest and wood acquisition into shared plans")
	flags.BoolVar(&c.routineWorkPlans, "routine-work-plans", false, "compile saved work preferences into shared pawn settings plans")
	flags.BoolVar(&c.routineSupplyPlans, "routine-supply-plans", false, "compile original starting supplies into bounded shared Allow plans")
	flags.BoolVar(&c.routineCookingPlans, "routine-cooking-plans", false, "compile reviewed cooking deficits into pending campfire plans")
	flags.BoolVar(&c.routineShelterPlans, "routine-shelter-plans", false, "compile indoor sleeping or a starter wall-and-door shell into shared plans")
	flags.BoolVar(&c.routineComfortPlans, "routine-comfort-plans", false, "compile reviewed dining and recreation deficits into shared building plans")
	flags.BoolVar(&c.routineExpansionPlans, "routine-expansion-plans", false, "compile one spare indoor sleeping place through shared building plans")
	flags.BoolVar(&c.routineTemperaturePlans, "routine-temperature-plans", false, "compile sleeping-room heating and cooling through shared building plans")
	flags.BoolVar(&c.routinePowerPlans, "routine-power-plans", false, "compile network generation and conduit deficits through shared building plans")
	flags.BoolVar(&c.routineDefensePlans, "routine-defense-plans", false, "compile bounded squad defense against observed hostiles into shared plans")
	flags.BoolVar(&c.routineTendPlans, "routine-tend-plans", false, "compile native-approved doctor/patient tend selection into shared plans")
	flags.BoolVar(&c.routineRescuePlans, "routine-rescue-plans", false, "compile downed-colonist rescue selection into shared plans")
	flags.BoolVar(&c.routineEquipPlans, "routine-equip-plans", false, "compile unarmed-colonist weapon equip selection into shared plans")
	flags.BoolVar(&c.routineSecureSuppliesPlans, "routine-secure-supplies-plans", false, "compile a vulnerable-item haul selection into shared plans")
	flags.BoolVar(&c.routineRepairPlans, "routine-repair-plans", false, "compile a damaged-structure repair selection into shared plans")
	flags.BoolVar(&c.routineCleanPlans, "routine-clean-plans", false, "compile a filth cleaning selection into shared plans")
	flags.BoolVar(&c.routineHaulPlans, "routine-haul-plans", false, "compile an ordinary unstored-item haul selection into shared plans")
	flags.BoolVar(&c.routineGearPlans, "routine-gear-plans", false, "compile existing-gear wear replacement selection into shared plans")
	flags.BoolVar(&c.routineMedicalPlans, "routine-medical-plans", false, "compile medicine reserve replenishment bill selection into shared plans")
	flags.BoolVar(&c.routineAnimalContainmentPlans, "routine-animal-containment-plans", false, "compile animal pen shell/marker containment method selection into shared plans")
	flags.BoolVar(&c.routineRecoveryPlans, "routine-recovery-plans", false, "compile disaster-recovery repair/breakdown/refuel service selection into shared plans")
	flags.BoolVar(&c.routineHusbandryPlans, "routine-husbandry-plans", false, "compile herd recursive-training selection into shared plans")
	flags.BoolVar(&c.routinePrisonerInteractionPlans, "routine-prisoner-interaction-plans", false, "compile recruitable-prisoner recruit-interaction selection into shared plans")
	flags.BoolVar(&c.routinePopulationCustodyPlans, "routine-population-custody-plans", false, "compile downed-hostile capture and unadmitted-guest rescue custody selection into shared plans")
	flags.BoolVar(&c.routineHomeCoveragePlans, "routine-home-coverage-plans", false, "compile native Home-area extension over owned facilities/stockpiles into shared plans")
	flags.BoolVar(&c.routineStoneShellPlans, "routine-stone-shell-plans", false, "compile guarded flammable-wall stone replacement bundles into shared plans")
	flags.StringVar(&c.routineResearchTarget, "routine-research-target", "", "operator-declared native ResearchProjectDef name EnsureResearch's routine planner selects prerequisite-ordered toward, once no research project is already current")
	flags.BoolVar(&c.routineResourcePlans, "routine-resource-plans", false, "compile MaintainResource bench/recipe replenishment bill selection into shared plans")
	flags.Var(&c.routineResourceTargets, "routine-resource-target", "repeatable RESOURCE:TARGET native stock floor MaintainResource's dynamic-target selection dispatches a production bill toward")
	flags.BoolVar(&c.routineAnimalFeedPlans, "routine-animal-feed-plans", false, "compile MaintainAnimalFeed shared-feed-stock replenishment selection into shared plans")
	flags.BoolVar(&c.routineProductionPolicyPlans, "routine-production-policy-plans", false, "compile ProductionPolicy resource floor/stop replacement into shared plans")
	flags.Var(&c.routineResourceReserves, "routine-resource-reserve", "repeatable RESOURCE:FLOOR native stock floor ProductionPolicy's routine planner replaces into the current native production policy")
	flags.Var(&c.routineStoppedResources, "routine-resource-stop", "repeatable RESOURCE name ProductionPolicy's routine planner keeps stopped in the current native production policy")
	flags.BoolVar(&c.routineMethods, "routine-methods", false, "execute reviewed routine building methods under the current player direction")
	flags.BoolVar(&c.caravanJourneyTracking, "caravan-journey-tracking", false, "poll world progression each clock step and resolve tracked caravans that have returned home")
	flags.Var(&c.resourceRules, "resource-rule", "repeatable RESOURCE:allow|stop|defense_only:RESERVE for building admission and dispatch")
	flags.StringVar(&c.profile, "profile", "", "absolute shared game profile directory for player control")
	flags.StringVar(&c.bridge.Executable, "gabs", "", "absolute GABS executable")
	flags.StringVar(&c.bridge.ConfigDir, "config", "", "absolute GABS configuration directory")
	flags.StringVar(&c.bridge.GameID, "game", "", "configured game ID")
	flags.StringVar(&c.state, "state", "", "absolute fresh Go SQLite database path")
	flags.StringVar(&c.assets, "assets", "", "absolute built dashboard directory (optional)")
	flags.StringVar(&c.listen, "listen", "127.0.0.1:0", "loopback IP:port; 0 selects an available port")
	flags.DurationVar(&c.refresh, "refresh", 3*time.Second, "observation refresh interval")
	flags.DurationVar(&c.bridge.Timeout, "timeout", 15*time.Second, "native call timeout")
	flags.StringVar(&c.flightRecorder, "flight-recorder", "", "absolute path recording every native request/response/error (optional; opt-in diagnostics)")
	if err := flags.Parse(args); err != nil {
		return c, err
	}
	if flags.NArg() != 0 || *readOnly == c.playerControl {
		return c, errors.New("serve requires exactly one of --read-only or --player-control")
	}
	projectLimitExplicit := false
	explicitPlan := false
	flags.Visit(func(f *flag.Flag) {
		projectLimitExplicit = projectLimitExplicit || f.Name == "routine-project-limit"
		explicitPlan = explicitPlan || strings.HasPrefix(f.Name, "routine-") && strings.HasSuffix(f.Name, "-plans")
	})
	if c.routineProjectLimit < 1 || c.routineProjectLimit > 8 || projectLimitExplicit && !c.routineReviews {
		return c, errors.New("--routine-project-limit requires --routine-reviews and a value from 1 through 8")
	}
	// Composed default: an operator who asks for --routine-methods without
	// naming any individual --routine-*-plans flag gets every implemented and
	// tested planner family at once, instead of having to enumerate ~30 flags.
	// Naming even one --routine-*-plans flag opts out of this default and
	// falls back to exactly the named families (existing targeted/debug use).
	if c.routineMethods && !explicitPlan {
		for _, entry := range routinePlanFlags(&c) {
			*entry.Enabled = true
		}
	}
	if len(c.resourceRules) > 0 && !c.playerControl {
		return c, errors.New("--resource-rule requires --player-control")
	}
	if c.clockControl && !c.playerControl {
		return c, errors.New("--clock-control requires --player-control")
	}
	if c.routineReviews && !c.clockControl {
		return c, errors.New("--routine-reviews requires --clock-control")
	}
	if c.caravanJourneyTracking && !c.clockControl {
		return c, errors.New("--caravan-journey-tracking requires --clock-control")
	}
	if (c.routineBillPlans || c.routineFieldPlans || c.routineFoodStoragePlans || c.routineAcquisitionPlans || c.routineWorkPlans || c.routineSupplyPlans || c.routineSleepingPlans || c.routineCookingPlans || c.routineShelterPlans || c.routineComfortPlans || c.routineExpansionPlans || c.routinePowerPlans || c.routineTemperaturePlans || c.routineDefensePlans || c.routineTendPlans || c.routineRescuePlans || c.routineEquipPlans || c.routineSecureSuppliesPlans || c.routineRepairPlans || c.routineCleanPlans || c.routineHaulPlans || c.routineGearPlans || c.routineMedicalPlans || c.routineAnimalContainmentPlans || c.routineRecoveryPlans || c.routineHusbandryPlans || c.routinePrisonerInteractionPlans || c.routinePopulationCustodyPlans || c.routineHomeCoveragePlans || c.routineStoneShellPlans || c.routineResearchTarget != "" || c.routineResourcePlans || c.routineAnimalFeedPlans || c.routineProductionPolicyPlans) && !c.routineReviews {
		return c, errors.New("routine building plans require --routine-reviews")
	}
	if c.routineMethods && !c.routineBillPlans && !c.routineFieldPlans && !c.routineFoodStoragePlans && !c.routineAcquisitionPlans && !c.routineWorkPlans && !c.routineSupplyPlans && !c.routineSleepingPlans && !c.routineCookingPlans && !c.routineShelterPlans && !c.routineComfortPlans && !c.routineExpansionPlans && !c.routinePowerPlans && !c.routineTemperaturePlans && !c.routineDefensePlans && !c.routineTendPlans && !c.routineRescuePlans && !c.routineEquipPlans && !c.routineSecureSuppliesPlans && !c.routineRepairPlans && !c.routineCleanPlans && !c.routineHaulPlans && !c.routineGearPlans && !c.routineMedicalPlans && !c.routineAnimalContainmentPlans && !c.routineRecoveryPlans && !c.routineHusbandryPlans && !c.routinePrisonerInteractionPlans && !c.routinePopulationCustodyPlans && !c.routineHomeCoveragePlans && !c.routineStoneShellPlans && c.routineResearchTarget == "" && !c.routineResourcePlans && !c.routineAnimalFeedPlans && !c.routineProductionPolicyPlans {
		return c, errors.New("--routine-methods requires a routine building planner")
	}
	// Defense/tend/rescue/equip/secure-supplies/repair/clean/haul/gear/medical/animal-containment/recovery/husbandry/prisoner-interaction/population-custody/home-coverage/stone-shell/research/resource/animal-feed/production-policy
	// plans never become the literal current plan (see clockSchedulerWork); they
	// can only run through the RoutineMethods concurrent-authorization path, so
	// without it their committed plans would never be authorized or dispatched.
	if (c.routineDefensePlans || c.routineTendPlans || c.routineRescuePlans || c.routineEquipPlans || c.routineSecureSuppliesPlans || c.routineRepairPlans || c.routineCleanPlans || c.routineHaulPlans || c.routineGearPlans || c.routineMedicalPlans || c.routineAnimalContainmentPlans || c.routineRecoveryPlans || c.routineHusbandryPlans || c.routinePrisonerInteractionPlans || c.routinePopulationCustodyPlans || c.routineHomeCoveragePlans || c.routineStoneShellPlans || c.routineResearchTarget != "" || c.routineResourcePlans || c.routineAnimalFeedPlans || c.routineProductionPolicyPlans) && !c.routineMethods {
		return c, errors.New("--routine-defense-plans, --routine-tend-plans, --routine-rescue-plans, --routine-equip-plans, --routine-secure-supplies-plans, --routine-repair-plans, --routine-clean-plans, --routine-haul-plans, --routine-gear-plans, --routine-medical-plans, --routine-animal-containment-plans, --routine-recovery-plans, --routine-husbandry-plans, --routine-prisoner-interaction-plans, --routine-population-custody-plans, --routine-home-coverage-plans, --routine-stone-shell-plans, --routine-research-target, --routine-resource-plans, --routine-animal-feed-plans and --routine-production-policy-plans require --routine-methods")
	}
	if len(c.routineResourceTargets) > 0 && !c.routineResourcePlans {
		return c, errors.New("--routine-resource-target requires --routine-resource-plans")
	}
	if (len(c.routineResourceReserves) > 0 || len(c.routineStoppedResources) > 0) && !c.routineProductionPolicyPlans {
		return c, errors.New("--routine-resource-reserve and --routine-resource-stop require --routine-production-policy-plans")
	}
	if c.playerControl && !filepath.IsAbs(c.profile) || !c.playerControl && c.profile != "" {
		return c, errors.New("--player-control requires an absolute --profile; read-only mode takes no profile")
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
	return c, nil
}

// routinePlanFlag names one "--routine-*-plans" boolean flag alongside a
// pointer into the serveConfig it was parsed into.
type routinePlanFlag struct {
	Name    string
	Enabled *bool
}

// routinePlanFlags lists every "--routine-*-plans" boolean flag, in
// registration order. It backs both the composed default (parseServe turns
// every one of these on when --routine-methods is set and none of them was
// named explicitly) and the /api/routines diagnostics family list, so the two
// stay in lockstep with the flag registrations above.
func routinePlanFlags(c *serveConfig) []routinePlanFlag {
	return []routinePlanFlag{
		{"routine-sleeping-plans", &c.routineSleepingPlans},
		{"routine-bill-plans", &c.routineBillPlans},
		{"routine-field-plans", &c.routineFieldPlans},
		{"routine-food-storage-plans", &c.routineFoodStoragePlans},
		{"routine-acquisition-plans", &c.routineAcquisitionPlans},
		{"routine-work-plans", &c.routineWorkPlans},
		{"routine-supply-plans", &c.routineSupplyPlans},
		{"routine-cooking-plans", &c.routineCookingPlans},
		{"routine-shelter-plans", &c.routineShelterPlans},
		{"routine-comfort-plans", &c.routineComfortPlans},
		{"routine-expansion-plans", &c.routineExpansionPlans},
		{"routine-temperature-plans", &c.routineTemperaturePlans},
		{"routine-power-plans", &c.routinePowerPlans},
		{"routine-defense-plans", &c.routineDefensePlans},
		{"routine-tend-plans", &c.routineTendPlans},
		{"routine-rescue-plans", &c.routineRescuePlans},
		{"routine-equip-plans", &c.routineEquipPlans},
		{"routine-secure-supplies-plans", &c.routineSecureSuppliesPlans},
		{"routine-repair-plans", &c.routineRepairPlans},
		{"routine-clean-plans", &c.routineCleanPlans},
		{"routine-haul-plans", &c.routineHaulPlans},
		{"routine-gear-plans", &c.routineGearPlans},
		{"routine-medical-plans", &c.routineMedicalPlans},
		{"routine-animal-containment-plans", &c.routineAnimalContainmentPlans},
		{"routine-recovery-plans", &c.routineRecoveryPlans},
		{"routine-husbandry-plans", &c.routineHusbandryPlans},
		{"routine-prisoner-interaction-plans", &c.routinePrisonerInteractionPlans},
		{"routine-population-custody-plans", &c.routinePopulationCustodyPlans},
		{"routine-home-coverage-plans", &c.routineHomeCoveragePlans},
		{"routine-stone-shell-plans", &c.routineStoneShellPlans},
		{"routine-resource-plans", &c.routineResourcePlans},
		{"routine-animal-feed-plans", &c.routineAnimalFeedPlans},
		{"routine-production-policy-plans", &c.routineProductionPolicyPlans},
	}
}

// activeRoutineFamilies reports the flag names of every routine planner
// family this configuration enabled, for runtime diagnostics.
func (c serveConfig) activeRoutineFamilies() []string {
	cp := c
	var names []string
	for _, entry := range routinePlanFlags(&cp) {
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
		recorder, err := flightrecorder.New(config.flightRecorder)
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
	ConnectGame(context.Context) (bridge.Result, error)
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
	if _, err = client.ConnectGame(ctx); err != nil {
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
	snapshots, err := controller.NewReadState(hex.EncodeToString(random[:]), client, wallClock{}, 2*config.refresh+config.bridge.Timeout)
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

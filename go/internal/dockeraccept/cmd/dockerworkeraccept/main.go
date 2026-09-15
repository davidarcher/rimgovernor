// Command dockerworkeraccept is the first Docker-acceptance slice: build the
// containers/Dockerfile "worker" target, start one --network host worker
// container against real Linux game/mods/profile/GABS inputs, prove it
// reports a connected native session over its own HTTP API (never via a
// second bridge.Client -- see go/internal/dockeraccept's package comment),
// then stop it cleanly. It does not yet exercise the paired two-worker
// checkpoint scenario (go/internal/dockeraccept/cmd/dockernativeaccept,
// unbuilt); that is the next slice.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/dockeraccept"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	source := flag.String("source", "", "absolute repository root containing containers/Dockerfile (default: working directory)")
	game := flag.String("game", "", "absolute host path to the Linux game build")
	mods := flag.String("mods", "", "absolute host path to the shared platform Mods directory (Harmony/RimBridgeServer/RimGovernor); RimGovernor is treated as a stale snapshot and rebuilt fresh unless -no-native-build is set")
	profile := flag.String("profile", "", "absolute host path to the prepared game profile (Config/Saves)")
	gabs := flag.String("gabs", "", "absolute host path to the directory containing the Linux GABS binary")
	configTemplate := flag.String("config-template", "", "absolute host path to a config.json whose games.<gameID> section already uses the container-internal /inputs/... paths")
	gameID := flag.String("game-id", "rimgovernor-trial", "configured game ID")
	image := flag.String("image", "rimgovernor-worker:local", "worker image tag")
	noBuild := flag.Bool("no-build", false, "use an existing image instead of building")
	noNativeBuild := flag.Bool("no-native-build", false, "mount -mods as-is instead of rebuilding a fresh native package from -source")
	clockControl := flag.Bool("clock-control", false, "start the worker with --clock-control (autopilot ticking)")
	output := flag.String("output", "", "fresh output directory")
	startupTimeout := flag.Duration("startup-timeout", 240*time.Second, "worker connected-session startup timeout")
	flag.Parse()

	for name, value := range map[string]string{"game": *game, "mods": *mods, "profile": *profile, "gabs": *gabs, "config-template": *configTemplate, "output": *output} {
		if value == "" {
			fmt.Fprintf(os.Stderr, "-%s is required\n", name)
			os.Exit(2)
		}
	}
	if *source == "" {
		wd, err := os.Getwd()
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		*source = wd
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}

	report := na.NewReport("One --network host worker container reports a connected native session over its own HTTP API, then stops cleanly. No pawn-work, checkpoint pairing or sustained throughput acceptance.", false)
	ctx, cancel := context.WithTimeout(context.Background(), *startupTimeout+2*time.Minute)
	defer cancel()
	err := run(ctx, runConfig{
		source: *source, game: *game, mods: *mods, profile: *profile, gabs: *gabs,
		configTemplate: *configTemplate, gameID: *gameID, image: *image, noBuild: *noBuild,
		noNativeBuild: *noNativeBuild,
		clockControl:  *clockControl, output: *output, startupTimeout: *startupTimeout,
	}, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

type runConfig struct {
	source, game, mods, profile, gabs, configTemplate, gameID, image, output string
	noBuild, noNativeBuild, clockControl                                     bool
	startupTimeout                                                           time.Duration
}

func run(ctx context.Context, cfg runConfig, report na.Report) error {
	docker, err := dockeraccept.DockerBinary()
	if err != nil {
		return err
	}
	if err := dockeraccept.RequireLinuxContainers(ctx, docker); err != nil {
		return err
	}
	if !cfg.noBuild {
		id, err := dockeraccept.BuildImage(ctx, docker, cfg.source, "worker", cfg.image, filepath.Join(cfg.output, "build.log"))
		if err != nil {
			return err
		}
		report["image"] = id
	} else {
		id, err := dockeraccept.InspectImage(ctx, docker, cfg.image)
		if err != nil {
			return err
		}
		report["image"] = id
	}

	root := filepath.Join(cfg.output, "worker")
	worker, err := dockeraccept.StartWorker(ctx, dockeraccept.WorkerConfig{
		Docker: docker, Image: cfg.image,
		Inputs:          dockeraccept.Inputs{Game: cfg.game, Mods: cfg.mods, Profile: cfg.profile, Gabs: cfg.gabs},
		ConfigTemplate:  cfg.configTemplate,
		Root:            root,
		Name:            "rimgovernor-dockerworkeraccept-" + filepath.Base(cfg.output),
		GameID:          cfg.gameID,
		ClockControl:    cfg.clockControl,
		StartupTimeout:  cfg.startupTimeout,
		Source:          cfg.source,
		SkipNativeBuild: cfg.noNativeBuild,
	})
	if err != nil {
		return fmt.Errorf("start worker: %w", err)
	}
	report["port"] = worker.Port
	state, err := worker.State(ctx)
	stopErr := worker.Stop(context.Background())
	if err != nil {
		return fmt.Errorf("read worker state: %w", err)
	}
	if !state.Connected {
		return fmt.Errorf("worker reported not connected at inspection time")
	}
	report["connected"] = state.Connected
	if state.Game.Tick != nil {
		report["tick"] = *state.Game.Tick
	}
	if stopErr != nil {
		return fmt.Errorf("stop worker: %w", stopErr)
	}
	report["stopped_clean"] = true
	return nil
}

// Command dockerworkeraccept is the first Docker-acceptance slice: build the
// containers/Dockerfile "worker" target, start one --network host worker
// container against real Linux game/mods/profile/GABS inputs, prove it
// reports a connected native session over its own HTTP API (never via a
// second bridge.Client -- see go/internal/nativeaccept/docker's package comment),
// then stop it cleanly under the worker storage contract (docker.Storage:
// private volume by default, exported and integrity-checked after the stop,
// volume verified gone; -storage bind for the host bind-mount comparison).
// It does not exercise a paired two-worker scenario; Go has no paired
// checkpoints (#59).
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/docker"
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
	output := flag.String("output", "", "fresh output directory")
	startupTimeout := flag.Duration("startup-timeout", 240*time.Second, "worker connected-session startup timeout")
	storageFlag := flag.String("storage", string(docker.StorageVolume), "worker /worker storage: volume (private Docker volume, exported after stop) or bind (host bind mount comparison)")
	flag.Parse()
	storage, err := docker.ParseStorage(*storageFlag)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}

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

	report := na.NewReport("One --network host worker container reports a connected native session over its own HTTP API, then stops cleanly with its /worker storage exported, integrity-checked and released. No pawn-work, checkpoint pairing or sustained throughput acceptance.", false)
	report["storage"] = string(storage)
	ctx, cancel := context.WithTimeout(context.Background(), *startupTimeout+2*time.Minute)
	defer cancel()
	err = run(ctx, runConfig{
		source: *source, game: *game, mods: *mods, profile: *profile, gabs: *gabs,
		configTemplate: *configTemplate, gameID: *gameID, image: *image, noBuild: *noBuild,
		noNativeBuild: *noNativeBuild, storage: storage,
		output: *output, startupTimeout: *startupTimeout,
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
	noBuild, noNativeBuild                                                   bool
	storage                                                                  docker.Storage
	startupTimeout                                                           time.Duration
}

func run(ctx context.Context, cfg runConfig, report na.Report) error {
	dockerBinary, err := docker.DockerBinary()
	if err != nil {
		return err
	}
	if err := docker.RequireLinuxContainers(ctx, dockerBinary); err != nil {
		return err
	}
	if !cfg.noBuild {
		id, err := docker.BuildImage(ctx, dockerBinary, cfg.source, "worker", cfg.image, filepath.Join(cfg.output, "build.log"))
		if err != nil {
			return err
		}
		report["image"] = id
	} else {
		id, err := docker.InspectImage(ctx, dockerBinary, cfg.image)
		if err != nil {
			return err
		}
		report["image"] = id
	}

	root := filepath.Join(cfg.output, "worker")
	worker, err := docker.StartWorker(ctx, docker.WorkerConfig{
		Docker: dockerBinary, Image: cfg.image,
		Inputs:          docker.Inputs{Game: cfg.game, Mods: cfg.mods, Profile: cfg.profile, Gabs: cfg.gabs},
		ConfigTemplate:  cfg.configTemplate,
		Root:            root,
		Name:            "rimgovernor-dockerworkeraccept-" + filepath.Base(cfg.output),
		GameID:          cfg.gameID,
		StartupTimeout:  cfg.startupTimeout,
		Source:          cfg.source,
		SkipNativeBuild: cfg.noNativeBuild,
		Storage:         cfg.storage,
	})
	if err != nil {
		return fmt.Errorf("start worker: %w", err)
	}
	report["port"] = worker.Port
	state, err := worker.State(ctx)
	// Stop always runs, and its evidence is reported even when the state
	// read failed: a retained volume or a failed export is exactly what a
	// failed run's post-mortem needs to know about.
	evidence, stopErr := worker.Stop(context.Background())
	report["storage_evidence"] = evidence
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
	if err := checkStorage(cfg.storage, evidence, root); err != nil {
		return err
	}
	report["stopped_clean"] = true
	return nil
}

// checkStorage asserts the storage contract's success-path outcome: the
// container stopped before anything was exported, the controller database
// reached the host root and passed its integrity check, and (under volume
// storage) the private volume is gone rather than quietly retained.
func checkStorage(storage docker.Storage, evidence docker.StorageEvidence, root string) error {
	if !evidence.Stopped {
		return fmt.Errorf("storage: container stop was not confirmed before export")
	}
	if !evidence.Exported {
		return fmt.Errorf("storage: worker tree was not exported: %s", evidence.ExportError)
	}
	if _, err := os.Stat(filepath.Join(root, "state.db")); err != nil {
		return fmt.Errorf("storage: exported state.db missing from host root: %w", err)
	}
	if storage == docker.StorageVolume {
		if len(evidence.Databases) == 0 {
			return fmt.Errorf("storage: no exported database was integrity-checked")
		}
		if evidence.Retained {
			return fmt.Errorf("storage: private volume %s retained after a successful run: %s", evidence.Volume, evidence.CleanupError)
		}
	}
	return nil
}

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
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

type serveConfig struct {
	bridge                bridge.ProcessConfig
	state, listen, assets string
	profile               string
	playerControl         bool
	clockControl          bool
	routineReviews        bool
	routineSleepingPlans  bool
	routineMethods        bool
	refresh               time.Duration
}

func parseServe(args []string, diagnostics io.Writer) (serveConfig, error) {
	var c serveConfig
	flags := flag.NewFlagSet("serve", flag.ContinueOnError)
	flags.SetOutput(diagnostics)
	readOnly := flags.Bool("read-only", false, "observe an already running game; game writes are unavailable")
	flags.BoolVar(&c.playerControl, "player-control", false, "enable explicit player building and draft controls; never acquire on startup")
	flags.BoolVar(&c.clockControl, "clock-control", false, "supervise finite game-clock windows for enabled player work")
	flags.BoolVar(&c.routineReviews, "routine-reviews", false, "review routine needs at paused clock boundaries; method execution remains unavailable")
	flags.BoolVar(&c.routineSleepingPlans, "routine-sleeping-plans", false, "compile reviewed indoor sleeping needs into pending shared plans; execution remains unavailable")
	flags.BoolVar(&c.routineMethods, "routine-methods", false, "execute reviewed routine building methods under the current player direction")
	flags.StringVar(&c.profile, "profile", "", "absolute shared game profile directory for player control")
	flags.StringVar(&c.bridge.Executable, "gabs", "", "absolute GABS executable")
	flags.StringVar(&c.bridge.ConfigDir, "config", "", "absolute GABS configuration directory")
	flags.StringVar(&c.bridge.GameID, "game", "", "configured game ID")
	flags.StringVar(&c.state, "state", "", "absolute fresh Go SQLite database path")
	flags.StringVar(&c.assets, "assets", "", "absolute built dashboard directory (optional)")
	flags.StringVar(&c.listen, "listen", "127.0.0.1:0", "loopback IP:port; 0 selects an available port")
	flags.DurationVar(&c.refresh, "refresh", 3*time.Second, "observation refresh interval")
	flags.DurationVar(&c.bridge.Timeout, "timeout", 15*time.Second, "native call timeout")
	if err := flags.Parse(args); err != nil {
		return c, err
	}
	if flags.NArg() != 0 || *readOnly == c.playerControl {
		return c, errors.New("serve requires exactly one of --read-only or --player-control")
	}
	if c.clockControl && !c.playerControl {
		return c, errors.New("--clock-control requires --player-control")
	}
	if c.routineReviews && !c.clockControl {
		return c, errors.New("--routine-reviews requires --clock-control")
	}
	if c.routineSleepingPlans && !c.routineReviews {
		return c, errors.New("--routine-sleeping-plans requires --routine-reviews")
	}
	if c.routineMethods && !c.routineSleepingPlans {
		return c, errors.New("--routine-methods requires --routine-sleeping-plans")
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
	return c, nil
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

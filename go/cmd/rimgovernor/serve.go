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
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/controller"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

type serveConfig struct {
	bridge        bridge.ProcessConfig
	state, listen string
	refresh       time.Duration
}

func parseServe(args []string, diagnostics io.Writer) (serveConfig, error) {
	var c serveConfig
	flags := flag.NewFlagSet("serve", flag.ContinueOnError)
	flags.SetOutput(diagnostics)
	readOnly := flags.Bool("read-only", false, "observe an already running game; game writes are unavailable")
	flags.StringVar(&c.bridge.Executable, "gabs", "", "absolute GABS executable")
	flags.StringVar(&c.bridge.ConfigDir, "config", "", "absolute GABS configuration directory")
	flags.StringVar(&c.bridge.GameID, "game", "", "configured game ID")
	flags.StringVar(&c.state, "state", "", "absolute fresh Go SQLite database path")
	flags.StringVar(&c.listen, "listen", "127.0.0.1:0", "loopback IP:port; 0 selects an available port")
	flags.DurationVar(&c.refresh, "refresh", 3*time.Second, "observation refresh interval")
	flags.DurationVar(&c.bridge.Timeout, "timeout", 15*time.Second, "native call timeout")
	if err := flags.Parse(args); err != nil {
		return c, err
	}
	if flags.NArg() != 0 || !*readOnly {
		return c, errors.New("serve requires --read-only; native execution is not enabled")
	}
	if !filepath.IsAbs(c.state) || !filepath.IsAbs(c.bridge.Executable) || !filepath.IsAbs(c.bridge.ConfigDir) || c.bridge.GameID == "" {
		return c, errors.New("absolute --state, --gabs, --config and a --game ID are required")
	}
	host, _, err := net.SplitHostPort(c.listen)
	if err != nil || net.ParseIP(host) == nil || !net.ParseIP(host).IsLoopback() {
		return c, errors.New("--listen must use a loopback IP and port")
	}
	if c.refresh < 500*time.Millisecond || c.refresh > time.Minute || c.bridge.Timeout < time.Second || c.bridge.Timeout > time.Minute {
		return c, errors.New("refresh must be 500ms..1m and timeout 1s..1m")
	}
	return c, nil
}

func serve(ctx context.Context, args []string, out, diagnostics io.Writer) int {
	config, err := parseServe(args, diagnostics)
	if err != nil {
		fmt.Fprintln(diagnostics, err)
		return 2
	}
	if err = serveReadOnly(ctx, config, out); err != nil {
		fmt.Fprintln(diagnostics, "Go service:", err)
		return 1
	}
	return 0
}

func serveReadOnly(ctx context.Context, config serveConfig, out io.Writer) (result error) {
	client, err := bridge.Open(ctx, config.bridge)
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
	server, err := httpapi.New(httpapi.Config{ReadTimeout: 5 * time.Second, ShutdownTimeout: 5 * time.Second, MaxResponseBytes: 1 << 20}, snapshots, database)
	if err != nil {
		return err
	}
	listener, err := net.Listen("tcp", config.listen)
	if err != nil {
		return err
	}
	defer listener.Close()
	pollCtx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})
	go func() { defer close(done); snapshots.Poll(pollCtx, config.refresh) }()
	defer func() { cancel(); <-done }()
	fmt.Fprintf(out, "RimGovernor Go read-only service: http://%s\n", listener.Addr())
	return server.Serve(ctx, listener)
}

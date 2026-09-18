// Command gamesstop stops the game a disposable worker root owns through
// GABS games_stop: the PID-owned launch recorded by that root's own GABS
// configuration, never a process matched by image name.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root")
	configDir := flag.String("config", "", "GABS config directory (default <root>/config-headless, then <root>/config)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	takeover := flag.Bool("takeover", false, "take the GABS attachment from a stalled controller of this same root before stopping")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *configDir == "" {
		*configDir = *root + "/config-headless"
		if _, err := os.Stat(*configDir); err != nil {
			*configDir = *root + "/config"
		}
	}
	gabs, err := na.GABSExecutable(*root, *configDir)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	client, err := na.OpenBridgeSession(ctx, gabs, *configDir, *game, 60*time.Second)
	if err != nil && *takeover {
		// The root's own controller still holds the attachment; the caller
		// owns both sessions, so the handoff is explicit.
		client, err = na.OpenBridgeSessionWithTakeover(ctx, gabs, *configDir, *game, 60*time.Second)
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer client.Close()
	stopped, err := client.GamesStop(ctx)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println(string(stopped.Envelope))
}

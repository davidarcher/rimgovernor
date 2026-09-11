// Command readsmoke verifies the read-only Go boundary against an already-loaded,
// paused disposable native game. The external scenario owns game startup/cleanup.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

type clock struct{}

func (clock) Now() time.Time { return time.Now() }

type sample struct {
	Before      observation.Identity `json:"before"`
	After       observation.Identity `json:"after"`
	StartedAt   time.Time            `json:"startedAt"`
	ObservedAt  time.Time            `json:"observedAt"`
	PausedKnown bool                 `json:"pausedKnown"`
	Paused      bool                 `json:"paused"`
	Receipts    [3]bridge.Result     `json:"receipts"`
}
type report struct {
	Passed  bool     `json:"passed"`
	Scope   string   `json:"scope"`
	Error   string   `json:"error,omitempty"`
	Samples []sample `json:"samples"`
}

func main() {
	executable := flag.String("gabs", "", "absolute GABS executable")
	config := flag.String("config", "", "absolute GABS configuration directory")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	output := flag.String("output", "", "fresh report path")
	flag.Parse()
	if *output == "" {
		fmt.Fprintln(os.Stderr, "-output is required")
		os.Exit(2)
	}
	file, err := os.OpenFile(*output, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	result := report{Scope: "Existing initialized fresh game; GABS connection/discovery and colony identity/status reads only. No game startup, stop, save, placement or clock control."}
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	err = observe(ctx, bridge.ProcessConfig{Executable: *executable, ConfigDir: *config, GameID: *game, Timeout: 15 * time.Second}, &result)
	cancel()
	if err != nil {
		result.Error = err.Error()
	} else {
		result.Passed = true
	}
	encoder := json.NewEncoder(file)
	encoder.SetIndent("", "  ")
	writeErr := encoder.Encode(result)
	closeErr := file.Close()
	if err != nil || writeErr != nil || closeErr != nil {
		fmt.Fprintln(os.Stderr, "read smoke failed:", err, writeErr, closeErr)
		os.Exit(1)
	}
	fmt.Println("read-only native observation passed")
}
func observe(ctx context.Context, config bridge.ProcessConfig, result *report) (err error) {
	client, err := bridge.Open(ctx, config)
	if err != nil {
		return err
	}
	defer func() {
		closeErr := client.Close()
		if err == nil {
			err = closeErr
		}
	}()
	if _, err = client.ConnectGame(ctx); err != nil {
		return err
	}
	for range 2 {
		reading, readErr := observation.Observe(ctx, client, clock{})
		snapshot := reading.Snapshot
		paused, known := snapshot.Status.Paused.Value()
		result.Samples = append(result.Samples, sample{Before: snapshot.Before, After: snapshot.After, StartedAt: snapshot.StartedAt, ObservedAt: snapshot.ObservedAt, PausedKnown: known, Paused: paused, Receipts: reading.Receipts})
		if readErr != nil {
			return readErr
		}
		if !known || !paused || !snapshot.SameTick() {
			return fmt.Errorf("fixture was not observed paused at one unchanged tick")
		}
		if err = snapshot.CheckFresh(time.Now(), 30*time.Second, snapshot.After); err != nil {
			return err
		}
	}
	first, last := result.Samples[0], result.Samples[1]
	if !first.After.SameContext(last.After) || first.Before.Tick != last.After.Tick {
		return fmt.Errorf("native identity or tick changed across reads")
	}
	return nil
}

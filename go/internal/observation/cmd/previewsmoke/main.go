// Command previewsmoke checks typed placement previews in a paused native fixture.
// The external scenario owns game startup, placement choices and cleanup.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	common "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	wire "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type options struct{ gabs, config, game, requests, output string }
type fixture struct {
	placements []*wire.PlacementCandidate
	expected   []string
}
type clock struct{}

func (clock) Now() time.Time { return time.Now() }

type sample struct {
	Before, After       observation.Identity
	Paused, PausedKnown bool
	Receipts            [3]bridge.Result
}
type report struct {
	Passed     bool          `json:"passed"`
	Scope      string        `json:"scope"`
	Error      string        `json:"error,omitempty"`
	ErrorKind  string        `json:"errorKind,omitempty"`
	Connection bridge.Result `json:"connection"`
	Before     sample        `json:"before"`
	Preview    bridge.Result `json:"preview"`
	After      sample        `json:"after"`
	Expected   []string      `json:"expected"`
	Actual     []string      `json:"actual"`
}

func parseOptions(args []string) (options, error) {
	var o options
	flags := flag.NewFlagSet("previewsmoke", flag.ContinueOnError)
	flags.SetOutput(io.Discard)
	flags.StringVar(&o.gabs, "gabs", "", "absolute GABS executable")
	flags.StringVar(&o.config, "config", "", "absolute configuration directory")
	flags.StringVar(&o.game, "game", "rimgovernor-trial", "configured game ID")
	flags.StringVar(&o.requests, "requests", "", "absolute request fixture path")
	flags.StringVar(&o.output, "output", "", "fresh output directory")
	if err := flags.Parse(args); err != nil {
		return o, err
	}
	if flags.NArg() != 0 || strings.TrimSpace(o.game) == "" {
		return o, errors.New("unexpected arguments or empty game ID")
	}
	for _, path := range []string{o.gabs, o.config, o.requests, o.output} {
		if !filepath.IsAbs(path) {
			return o, errors.New("-gabs, -config, -requests and -output require absolute paths")
		}
	}
	return o, nil
}
func decodeFixture(data []byte) (fixture, error) {
	if len(data) > 1<<20 {
		return fixture{}, errors.New("fixture exceeds 1 MiB")
	}
	d := json.NewDecoder(bytes.NewReader(data))
	token, err := d.Token()
	if err != nil || token != json.Delim('{') {
		return fixture{}, errors.New("fixture must be an object")
	}
	seen := map[string]bool{}
	version := 0
	var raw json.RawMessage
	var expected []string
	for d.More() {
		key, err := d.Token()
		if err != nil {
			return fixture{}, err
		}
		name, ok := key.(string)
		if !ok || seen[name] {
			return fixture{}, errors.New("duplicate fixture field")
		}
		seen[name] = true
		switch name {
		case "version":
			err = d.Decode(&version)
		case "placements":
			err = d.Decode(&raw)
		case "expected":
			err = d.Decode(&expected)
		default:
			return fixture{}, fmt.Errorf("unknown fixture field %q", name)
		}
		if err != nil {
			return fixture{}, err
		}
	}
	if _, err = d.Token(); err != nil {
		return fixture{}, err
	}
	if _, err = d.Token(); err != io.EOF {
		return fixture{}, errors.New("trailing fixture JSON")
	}
	if version != 1 {
		return fixture{}, errors.New("fixture version must be 1")
	}
	request := &wire.PlacementRequest{}
	err = protojson.Unmarshal(append(append([]byte(`{"placements":`), raw...), '}'), request)
	placements := request.Placements
	if err != nil || len(placements) < 1 || len(placements) > 16 {
		return fixture{}, fmt.Errorf("invalid placement fixture count or ProtoJSON: %v", err)
	}
	if len(placements) != len(expected) {
		return fixture{}, errors.New("expectation count must match placement count")
	}
	for i, outcome := range expected {
		switch outcome {
		case "placeable", "refused", "invalid_definition":
		default:
			return fixture{}, fmt.Errorf("unknown expectation %q", outcome)
		}
		if placements[i] == nil || placements[i].DefName == nil || placements[i].X == nil || placements[i].Z == nil {
			return fixture{}, errors.New("candidate presence")
		}
		switch placements[i].GetRotation() {
		case wire.Rotation_ROTATION_NORTH, wire.Rotation_ROTATION_EAST, wire.Rotation_ROTATION_SOUTH, wire.Rotation_ROTATION_WEST:
		default:
			return fixture{}, errors.New("smoke requests require one exact cardinal rotation")
		}
	}
	return fixture{placements, expected}, nil
}
func main() { os.Exit(run(os.Args[1:], os.Stdout, os.Stderr)) }
func run(args []string, stdout, stderr io.Writer) int {
	o, err := parseOptions(args)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	f, err := os.Open(o.requests)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	data, err := io.ReadAll(io.LimitReader(f, (1<<20)+1))
	closeErr := f.Close()
	if err == nil {
		err = closeErr
	}
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	requests, err := decodeFixture(data)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	if err = os.Mkdir(o.output, 0700); err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	if err = os.WriteFile(filepath.Join(o.output, "requests.json"), data, 0600); err != nil {
		fmt.Fprintln(stderr, err)
		return 1
	}
	result := report{Scope: "Read-only generated placement previews in an existing paused native game; no placement, clock control, game startup or shutdown.", Expected: requests.expected}
	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	err = observe(ctx, bridge.ProcessConfig{Executable: o.gabs, ConfigDir: o.config, GameID: o.game, Timeout: 30 * time.Second}, requests, &result)
	cancel()
	if err != nil {
		result.Error = err.Error()
		if result.ErrorKind == "" {
			result.ErrorKind = "acceptance"
		}
	} else {
		result.Passed = true
	}
	encoded, writeErr := json.MarshalIndent(result, "", "  ")
	if writeErr == nil {
		writeErr = os.WriteFile(filepath.Join(o.output, "report.json"), encoded, 0600)
	}
	if err != nil || writeErr != nil {
		fmt.Fprintln(stderr, "preview smoke failed:", err, writeErr)
		return 1
	}
	fmt.Fprintln(stdout, "typed read-only native preview smoke passed")
	return 0
}
func takeSample(ctx context.Context, client *bridge.Client) (sample, error) {
	reading, err := observation.Observe(ctx, client, clock{})
	snapshot := reading.Snapshot
	paused, known := snapshot.Status.Paused.Value()
	result := sample{snapshot.Before, snapshot.After, paused, known, reading.Receipts}
	if err != nil {
		return result, err
	}
	if !known || !paused || !snapshot.SameTick() {
		return result, errors.New("fixture is not paused at an unchanged tick")
	}

	return result, snapshot.CheckFresh(time.Now(), 30*time.Second, snapshot.After)
}
func observe(ctx context.Context, config bridge.ProcessConfig, requests fixture, result *report) (err error) {
	client, err := bridge.Open(ctx, config)
	if err != nil {
		result.ErrorKind = "sdk_connection"
		return err
	}
	defer func() {
		if closeErr := client.Close(); err == nil {
			err = closeErr
		}
	}()
	result.Connection, err = client.ConnectGame(ctx)
	if err != nil {
		result.ErrorKind = "sdk_connection"
		return err
	}
	result.Before, err = takeSample(ctx, client)
	if err != nil {
		return err
	}
	identity := result.Before.After
	request := &wire.PlacementRequest{Identity: &common.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}, Placements: requests.placements}
	var decoded *wire.PlacementReply
	decoded, result.Preview, err = client.PlacementPreviews(ctx, request)
	if err != nil {
		result.ErrorKind = "sdk_request"
		return err
	}
	result.After, err = takeSample(ctx, client)
	if err != nil {
		return err
	}
	if !result.Before.After.SameContext(result.After.After) || result.Before.Before.Tick != result.After.After.Tick {
		return errors.New("identity or paused tick changed across preview")
	}

	result.Actual, err = checkReply(decoded, requests, result.Before.After)
	return err
}
func checkReply(reply *wire.PlacementReply, requests fixture, identity observation.Identity) ([]string, error) {
	if reply.GetFailure() != nil {
		return nil, fmt.Errorf("request-level native refusal: %s", reply.GetFailure().GetDetail())
	}
	b := reply.GetBatch()
	if b == nil || b.Context == nil || b.Context.Identity == nil || domain.MapID(b.Context.Identity.GetMapId()) != identity.Map || domain.Tick(b.Context.GetTick()) != identity.Tick {
		return nil, errors.New("preview version/map/tick mismatch")
	}
	if len(b.Results) != len(requests.placements) {
		return nil, errors.New("preview order/count mismatch")
	}
	actual := make([]string, len(b.Results))
	for i, result := range b.Results {
		request := requests.placements[i]
		if result.GetFailure() != nil {
			actual[i] = "invalid_definition"
			if !strings.Contains(result.GetFailure().GetDetail(), request.GetDefName()) {
				return actual, fmt.Errorf("candidate %d failure does not identify requested definition", i)
			}
		} else {
			v := result.GetEvaluated()
			if v == nil || len(v.Rotations) != 1 || v.Rotations[0].GetRotation() != request.GetRotation() {
				return actual, fmt.Errorf("candidate %d rotation/order mismatch", i)
			}
			rotation := v.Rotations[0]
			if v.GetCanPlace() != rotation.GetAccepted() {
				return actual, fmt.Errorf("candidate %d aggregate/rotation disagreement", i)
			}
			if v.GetCanPlace() {
				actual[i] = "placeable"
			} else {
				actual[i] = "refused"
			}
			anchor := false
			for _, cell := range rotation.OccupiedCells {
				anchor = anchor || cell.GetX() == request.GetX() && cell.GetZ() == request.GetZ()
			}
			if !anchor {
				return actual, fmt.Errorf("candidate %d footprint/order mismatch", i)
			}
		}
		if actual[i] != requests.expected[i] {
			return actual, fmt.Errorf("candidate %d expected %s, observed %s", i, requests.expected[i], actual[i])
		}
	}
	return actual, nil
}

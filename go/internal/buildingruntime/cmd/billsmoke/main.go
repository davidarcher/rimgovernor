// billsmoke is a trusted native fixture entrypoint, not a player service.
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
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/bill"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
)

const planID domain.PlanID = "go-bill-smoke"
const actionID domain.ActionID = "go-bill-smoke-action"

type options struct {
	mode, gabs, config, profile, state, output, game, request, expectedOutcome string
	execute, forceTakeover                                                     bool
}
type realClock struct{}

func (realClock) Now() time.Time { return time.Now() }

type callRecord struct {
	Name    string
	Receipt bridge.Result
}
type report struct {
	ExpectedOutcome string         `json:"expectedOutcome"`
	Passed          bool           `json:"passed"`
	Mode            string         `json:"mode"`
	Scope           string         `json:"scope"`
	Error           string         `json:"error,omitempty"`
	Connection      bridge.Result  `json:"connection"`
	Identity        bridge.Result  `json:"identity"`
	Calls           []callRecord   `json:"calls"`
	Progress        progressReport `json:"progress"`
	NativeCalled    bool           `json:"nativeCalled"`
}

func parse(args []string) (options, error) {
	var out options
	flags := flag.NewFlagSet("billsmoke", flag.ContinueOnError)
	flags.SetOutput(io.Discard)
	flags.StringVar(&out.mode, "mode", "", "add or observe")
	flags.StringVar(&out.expectedOutcome, "expected-outcome", "completed", "observe: completed, unsuccessful, or absent")
	flags.StringVar(&out.gabs, "gabs", "", "absolute GABS executable")
	flags.StringVar(&out.config, "config", "", "absolute GABS configuration directory")
	flags.StringVar(&out.profile, "profile", "", "shared real game profile directory")
	flags.StringVar(&out.state, "state", "", "absolute Go SQLite path")
	flags.StringVar(&out.output, "output", "", "fresh absolute report directory")
	flags.StringVar(&out.game, "game", "rimgovernor-trial", "configured game ID")
	flags.StringVar(&out.request, "request", "", "absolute single bill fixture JSON file")
	flags.BoolVar(&out.execute, "execute", false, "explicit authorization for one fixture bill addition")
	flags.BoolVar(&out.forceTakeover, "force-takeover", false, "explicit controlled GABS handoff")
	if err := flags.Parse(args); err != nil {
		return out, err
	}
	if flags.NArg() != 0 || strings.TrimSpace(out.game) == "" {
		return out, errors.New("unexpected arguments or empty game")
	}
	if out.mode != "add" && out.mode != "observe" {
		return out, errors.New("mode must be add or observe")
	}
	for _, path := range []string{out.gabs, out.config, out.profile, out.state, out.output} {
		if !filepath.IsAbs(path) {
			return out, errors.New("gabs/config/profile/state/output require absolute paths")
		}
	}
	if out.mode == "add" && (!out.execute || !filepath.IsAbs(out.request)) {
		return out, errors.New("add requires --execute and absolute --request")
	}
	if out.mode == "observe" && (out.execute || out.request != "") {
		return out, errors.New("observe does not accept execute/request")
	}
	if out.expectedOutcome != "completed" && out.expectedOutcome != "unsuccessful" && out.expectedOutcome != "absent" {
		return out, errors.New("invalid expected outcome")
	}
	if out.mode == "add" && out.expectedOutcome != "completed" {
		return out, errors.New("add does not accept unsuccessful outcome expectations")
	}
	return out, nil
}

type billRequest struct {
	Bench  string `json:"bench"`
	Recipe string `json:"recipe"`
	Token  string `json:"token"`
	Mode   string `json:"mode"`
	Target int32  `json:"target"`
}

func fixture(data []byte) (domain.PlanSpec, error) {
	if len(data) > 1<<16 {
		return domain.PlanSpec{}, errors.New("fixture exceeds 64 KiB")
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	var req billRequest
	if err := d.Decode(&req); err != nil {
		return domain.PlanSpec{}, err
	}
	if d.More() {
		return domain.PlanSpec{}, errors.New("trailing bill fixture JSON")
	}
	mode, ok := map[string]domain.BillMode{"food_target": domain.FoodTarget, "butcher_forever": domain.ButcherForever}[req.Mode]
	if !ok {
		return domain.PlanSpec{}, errors.New("invalid bill mode")
	}
	bill, err := domain.NewProductionBill(req.Bench, req.Recipe, req.Token, mode, req.Target)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	action, err := domain.NewProductionBillAction(actionID, bill)
	if err != nil {
		return domain.PlanSpec{}, err
	}
	return domain.NewPlan(planID, 1, []domain.Action{action})
}

type session interface {
	Acquire(context.Context, domain.GenerationSnapshot) (domain.GenerationSnapshot, error)
	ObserveTarget(context.Context, domain.GenerationSnapshot) error
	Run(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error)
}

func advance(ctx context.Context, mode string, s session, snapshot domain.GenerationSnapshot) (executor.Result, error) {
	if mode == "add" {
		if _, err := s.Acquire(ctx, snapshot); err != nil {
			return executor.Result{}, err
		}
	} else {
		if err := s.ObserveTarget(ctx, snapshot); err != nil {
			return executor.Result{}, err
		}
	}
	return s.Run(ctx, planID, actionID)
}
func accepted(mode, expected string, result executor.Result) bool {
	v := result.Progress.View()
	if mode == "observe" {
		if result.NativeCalled || v.Unresolved {
			return false
		}
		switch expected {
		case "completed":
			return v.Stage == domain.Completed
		case "unsuccessful":
			reason, known := v.UnsuccessfulReason.Value()
			return v.Stage == domain.Unsuccessful && known && reason == domain.OutcomeNotAchieved
		case "absent":
			return v.Stage == domain.Pending || v.Stage == domain.Cancelled
		default:
			return false
		}
	}
	receipt, known := v.Receipt.Value()
	return result.NativeCalled && known && receipt == domain.ReceiptAccepted && v.Stage == domain.AwaitingObservation && v.Unresolved
}
func reserveState(path, mode string) error {
	if mode == "observe" {
		info, err := os.Stat(path)
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() {
			return errors.New("state must be existing regular file")
		}
		file, err := os.Open(path)
		if err != nil {
			return err
		}
		var header [16]byte
		_, readErr := io.ReadFull(file, header[:])
		closeErr := file.Close()
		if readErr != nil || closeErr != nil || string(header[:]) != "SQLite format 3\x00" {
			return errors.New("observe requires an existing SQLite database")
		}
		return nil
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	return file.Close()
}
func main() { os.Exit(run(os.Args[1:], os.Stdout, os.Stderr)) }
func run(args []string, stdout, stderr io.Writer) int {
	opts, err := parse(args)
	if err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	if err = os.Mkdir(opts.output, 0700); err != nil {
		fmt.Fprintln(stderr, err)
		return 2
	}
	out := report{ExpectedOutcome: opts.expectedOutcome, Mode: opts.mode, Scope: "One explicit fixture production bill addition on an existing bench, or one restart observation; no game startup, clock control, polling, or completion inferred from a receipt."}
	err = perform(opts, &out)
	if err != nil {
		out.Error = err.Error()
		out.Passed = false
	}
	data, writeErr := json.MarshalIndent(out, "", "  ")
	if writeErr == nil {
		writeErr = os.WriteFile(filepath.Join(opts.output, "report.json"), data, 0600)
	}
	if err != nil || writeErr != nil {
		fmt.Fprintln(stderr, errors.Join(err, writeErr))
		return 1
	}
	fmt.Fprintln(stdout, "bill fixture", opts.mode, "passed")
	return 0
}
func perform(opts options, out *report) (err error) {
	var spec domain.PlanSpec
	if opts.mode == "add" {
		file, readErr := os.Open(opts.request)
		if readErr != nil {
			return readErr
		}
		data, readErr := io.ReadAll(io.LimitReader(file, (1<<16)+1))
		closeErr := file.Close()
		if readErr != nil || closeErr != nil {
			return errors.Join(readErr, closeErr)
		}
		if err = os.WriteFile(filepath.Join(opts.output, "request.json"), data, 0600); err != nil {
			return err
		}
		spec, err = fixture(data)
		if err != nil {
			return err
		}
	}
	if err = reserveState(opts.state, opts.mode); err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()
	journal, err := store.Open(ctx, opts.state)
	if err != nil {
		return err
	}
	var client *bridge.Client
	var owner *buildingruntime.Session
	defer func() {
		if owner != nil {
			shutdown, stop := context.WithTimeout(context.Background(), 30*time.Second)
			closeErr := owner.Close(shutdown)
			stop()
			if closeErr != nil {
				err = errors.Join(err, closeErr)
				return
			}
		}
		if client != nil {
			err = errors.Join(err, client.Close())
		}
		err = errors.Join(err, journal.Close())
	}()
	if opts.mode == "add" {
		if err = journal.CreatePlan(ctx, spec); err != nil {
			return err
		}
	}
	state, err := journal.LoadPlan(ctx, planID)
	if err != nil {
		return err
	}
	if state.Spec.Revision() != 1 || len(state.Spec.Actions()) != 1 || state.Spec.Actions()[0].ID() != actionID || len(state.Progress) != 1 {
		return errors.New("not the fixed bill fixture plan")
	}
	view := state.Progress[0].View()
	out.Progress = project(view)
	if opts.mode == "observe" && !out.Progress.Unresolved {
		return errors.New("observe requires unresolved fixture attempt")
	}
	client, err = bridge.Open(ctx, bridge.ProcessConfig{Executable: opts.gabs, ConfigDir: opts.config, GameID: opts.game, Timeout: 20 * time.Second})
	if err != nil {
		return err
	}
	if opts.forceTakeover {
		out.Connection, err = client.ConnectGameWithTakeover(ctx)
	} else {
		out.Connection, err = client.ConnectGame(ctx)
	}
	if err != nil {
		return err
	}
	identity, raw, err := client.Identity(ctx)
	out.Identity = raw
	if err != nil {
		return err
	}
	observed, err := observation.DecodeIdentity(identity)
	if err != nil {
		return err
	}
	generation, known := observed.NativeGeneration.Value()
	if !known || generation == 0 {
		return errors.New("native authority generation unavailable")
	}
	snapshot := domain.GenerationSnapshot{Colony: observed.Colony, Map: observed.Map, Load: observed.Load, Native: generation, Plan: planID, Revision: 1}
	if opts.mode == "observe" {
		if snapshot.Colony != view.Snapshot.Colony || snapshot.Map != view.Snapshot.Map || snapshot.Load != view.Snapshot.Load {
			return errors.New("fixture world/load changed")
		}
	}
	authority, err := bridge.NewAuthorityControl(client)
	if err != nil {
		return err
	}
	buildingWriter, err := bridge.NewBuildingControl(client)
	if err != nil {
		return err
	}
	billWriter, err := bridge.NewBillControl(client)
	if err != nil {
		return err
	}
	native := &recordingNative{Client: client, AuthorityControl: authority, writer: billWriter, records: &out.Calls}
	owner, err = buildingruntime.NewSession(ctx, buildingruntime.SessionConfig{
		Bills:    &bill.BillCapabilities{Native: native, Writer: native},
		Control:  buildingruntime.ControlConfig{ProfileDirectory: opts.profile, CallTimeout: 20 * time.Second},
		Executor: executor.Limits{MaxAge: 20 * time.Second, RunTimeout: 60 * time.Second, JournalTimeout: 5 * time.Second},
	}, journal, native, native, buildingWriter, realClock{})
	if err != nil {
		return err
	}
	result, runErr := advance(ctx, opts.mode, owner, snapshot)
	out.NativeCalled = result.NativeCalled
	readCtx, readCancel := context.WithTimeout(context.Background(), 5*time.Second)
	state, loadErr := journal.LoadPlan(readCtx, planID)
	readCancel()
	if loadErr == nil {
		out.Progress = project(state.Progress[0].View())
	}
	if runErr != nil || loadErr != nil {
		return errors.Join(runErr, loadErr)
	}
	out.Passed = accepted(opts.mode, opts.expectedOutcome, result)
	if !out.Passed {
		return errors.New("fixture outcome not established; durable state retained")
	}
	return nil
}

type recordingNative struct {
	*bridge.Client
	*bridge.AuthorityControl
	writer  *bridge.BillControl
	records *[]callRecord
}

func (n *recordingNative) AddBill(ctx context.Context, pre *a.WritePrecondition, bill domain.ProductionBill) (*op.ExecuteReply, bridge.Result, error) {
	reply, raw, err := n.writer.AddBill(ctx, pre, bill)
	*n.records = append(*n.records, callRecord{"operations_execute", raw})
	return reply, raw, err
}
func (n *recordingNative) LookupBill(ctx context.Context, w bridge.BillAttempt) (*r.LookupReply, bridge.Result, error) {
	reply, raw, err := n.Client.LookupBill(ctx, w)
	*n.records = append(*n.records, callRecord{"receipts_lookup", raw})
	return reply, raw, err
}
func (n *recordingNative) ObserveBill(ctx context.Context, w bridge.BillAttempt, admitted *r.Receipt) (*r.ProgressReply, bridge.Result, error) {
	reply, raw, err := n.Client.ObserveBill(ctx, w, admitted)
	*n.records = append(*n.records, callRecord{"receipts_observe_progress", raw})
	return reply, raw, err
}

type snapshotReport struct {
	Colony           domain.ColonyID
	Map              domain.MapID
	Load             domain.LoadID
	Revision, Native string
	Plan             domain.PlanID
}
type progressReport struct {
	Action             domain.ActionID
	Attempt            string
	Plan               domain.PlanID
	Revision           string
	Stage              domain.Stage
	Snapshot           snapshotReport
	Tick               string
	Unresolved         bool
	Receipt            *domain.Receipt
	Effect             *domain.Effect
	UnsuccessfulReason *domain.UnsuccessfulReason
}

func project(v domain.ProgressView) progressReport {
	out := progressReport{Action: v.Action, Attempt: strconv.FormatUint(uint64(v.Attempt), 10), Plan: v.Plan, Revision: strconv.FormatUint(uint64(v.Revision), 10), Stage: v.Stage, Tick: strconv.FormatInt(int64(v.Tick), 10), Unresolved: v.Unresolved, Snapshot: snapshotReport{Colony: v.Snapshot.Colony, Map: v.Snapshot.Map, Load: v.Snapshot.Load, Plan: v.Snapshot.Plan, Revision: strconv.FormatUint(uint64(v.Snapshot.Revision), 10), Native: strconv.FormatUint(uint64(v.Snapshot.Native), 10)}}
	if value, known := v.Receipt.Value(); known {
		out.Receipt = &value
	}
	if value, known := v.Effect.Value(); known {
		out.Effect = &value
	}
	if value, known := v.UnsuccessfulReason.Value(); known {
		out.UnsuccessfulReason = &value
	}
	return out
}

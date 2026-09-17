// Command reuseaccept is the native acceptance for issue #22's reusable-game
// lifecycle (nativeaccept.GameReuse), the Go replacement for the removed
// game_reuse_acceptance.py. One RimWorld process is launched; the tribal8
// baseline is then loaded -cases times into it, each load a separate case
// that takes authority, drafts a colonist through operations_execute,
// releases the draft, revokes authority and -- when -rimgovernor is given --
// also launches and stops a per-case `rimgovernor serve` with its own SQLite
// state, asserting the stopped controller no longer answers. Between cases
// the lifecycle's own reset contract must hold: a load token never issued
// before, the game paused at the baseline tick, no active authority, no
// owned draft claim, and the sampled stock equal to the first load's. (The
// colony id is not compared: a fixture save the mod never wrote carries none,
// so native mints a fresh one per load.)
//
// A final negative case deliberately leaves a colonist owned-drafted and
// asserts EndCase refuses to hand the game on: it retires (games_stop) the
// process instead, so a dirty case can never leak into the next one.
//
// It says nothing about mod static state, which a reload does not reset (see
// contracts/native-static-state.md); cases asserting on those keep running in
// the ordinary fresh-process binaries.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-reuse-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "optional absolute path to a prebuilt rimgovernor binary; when set each case also launches and stops its own serve process")
	cases := flag.Int("cases", 3, "number of clean baseline reloads to run before the negative case")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *binary != "" && !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-rimgovernor must be an absolute path")
		os.Exit(2)
	}
	if *cases < 2 {
		fmt.Fprintln(os.Stderr, "-cases must be at least 2 so a second load token is compared against the first")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-reuse-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport(fmt.Sprintf("Reusable game (issue #22): one launch, %d baseline reloads each taking authority, "+
		"drafting and releasing a colonist, revoking authority and (with -rimgovernor) running a per-case controller; "+
		"every reload verified against the reset contract; a final unclean case must retire the game. "+
		"No mod static-state reset is claimed.", *cases), !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, *binary, !*rendered, *cases, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID, binary string, headless bool, cases int, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	reuse, err := na.OpenReusableGame(ctx, cfg, gabsExecutable)
	if err != nil {
		return err
	}
	defer func() {
		retireCtx, retireCancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer retireCancel()
		_ = reuse.Retire(retireCtx, "run complete")
		reuse.Record(report)
	}()

	h, err := reuse.Session(ctx)
	if err != nil {
		return err
	}
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	for _, tool := range []string{"rimgovernor/lifecycle_read_identity", "rimgovernor/authority_control",
		"rimgovernor/observations_list_pawns", "rimgovernor/observations_read_colony_facts", "rimgovernor/operations_execute",
		"rimgovernor/operations_release_owned_draft"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}

	var tokens []string
	for i := 1; i <= cases; i++ {
		name := fmt.Sprintf("case-%d", i)
		c, err := reuse.BeginCase(ctx, name, baselineSave, filepath.Join(output, name))
		if err != nil {
			return fmt.Errorf("%s: begin: %w", name, err)
		}
		tokens = append(tokens, c.Reset.LoadToken)
		if err := cleanCase(ctx, cfg, reuse, c, gabsExecutable, binary, report); err != nil {
			_ = reuse.EndCase(ctx, c, true)
			return fmt.Errorf("%s: %w", name, err)
		}
		if err := reuse.EndCase(ctx, c, false); err != nil {
			return fmt.Errorf("%s: end: %w", name, err)
		}
	}
	seen := map[string]bool{}
	for _, t := range tokens {
		if seen[t] {
			return fmt.Errorf("load token %q was issued twice", t)
		}
		seen[t] = true
	}
	report["load_tokens"] = tokens

	// Negative case: leave an owned draft behind. EndCase must retire.
	c, err := reuse.BeginCase(ctx, "case-unclean", baselineSave, filepath.Join(output, "case-unclean"))
	if err != nil {
		return fmt.Errorf("case-unclean: begin: %w", err)
	}
	if _, _, err := draftColonist(ctx, c.Harness, c.Identity, "unclean"); err != nil {
		_ = reuse.EndCase(ctx, c, true)
		return fmt.Errorf("case-unclean: %w", err)
	}
	endErr := reuse.EndCase(ctx, c, false)
	if !errors.Is(endErr, na.ErrReuseRetired) {
		return fmt.Errorf("case-unclean: EndCase should have retired the game, got %v", endErr)
	}
	retired, reason := reuse.Retired()
	if !retired {
		return fmt.Errorf("case-unclean: lifecycle does not report retirement")
	}
	report["unclean_case_retired"] = reason
	if _, err := reuse.BeginCase(ctx, "after-retire", baselineSave, filepath.Join(output, "after-retire")); !errors.Is(err, na.ErrReuseRetired) {
		return fmt.Errorf("BeginCase after retirement should refuse with ErrReuseRetired, got %v", err)
	}

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// cleanCase is one well-behaved case: authority on, draft, release, authority
// off, optionally a controller launched and stopped against this very load.
func cleanCase(ctx context.Context, cfg *na.Config, reuse *na.GameReuse, c *na.ReuseCase, gabsExecutable, binary string, report na.Report) error {
	h, identity := c.Harness, c.Identity
	row := map[string]any{"loadToken": c.Reset.LoadToken}
	_, row0, err := draftColonist(ctx, h, identity, c.Name)
	if err != nil {
		return err
	}
	claim, _ := na.AsMap(row0["draftClaim"])
	owned, _ := na.AsMap(claim["owned"])
	releaseReply, err := h.Wire(ctx, "release", "operations_release_owned_draft", map[string]any{
		"identity": identity, "pawn": na.Target(row0), "expectedClaimId": owned["claimId"],
	})
	if err != nil {
		return err
	}
	if _, released, err := na.Outcome(releaseReply, "released"); err != nil {
		return fmt.Errorf("release: %w", err)
	} else if observed, _ := na.AsMap(released["observed"]); observed["drafted"] != false {
		return fmt.Errorf("release did not clear drafted: %#v", observed)
	}
	// Every admitted write advances the native generation, so revoke against
	// the status read after the release, not the grant's generation.
	statusReply, err := h.Wire(ctx, "revoke-status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return err
	}
	statusContext, _ := na.AsMap(status["context"])
	revokeReply, err := h.Wire(ctx, "revoke", "authority_control", map[string]any{"revoke": map[string]any{
		"identity": identity, "expectedGeneration": statusContext["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return err
	}
	if _, _, err := na.Outcome(revokeReply, "revoked"); err != nil {
		return fmt.Errorf("revoke: %w", err)
	}
	row["drafted_and_released"] = na.AsString(na.Target(row0)["entityId"])

	if binary != "" {
		if err := reuse.ReleaseSession(); err != nil {
			return fmt.Errorf("release session for the controller: %w", err)
		}
		caseCfg := *cfg
		caseCfg.Output = c.Output
		serviceReport := na.Report{}
		service, err := na.LaunchService(ctx, &caseCfg, gabsExecutable, na.ServiceLaunch{Binary: binary}, serviceReport)
		if err != nil {
			return fmt.Errorf("launch controller: %w", err)
		}
		token, err := service.SessionToken()
		if err != nil {
			service.Stop()
			return err
		}
		if _, err := service.WaitAttached(identity, 90*time.Second); err != nil {
			service.Stop()
			return err
		}
		service.Stop()
		if err := service.AssertStopped(); err != nil {
			return err
		}
		if _, err := os.Stat(service.StatePath); err != nil {
			return fmt.Errorf("per-case state database missing: %w", err)
		}
		serviceReport["session_token_issued"] = token != ""
		serviceReport["state_path"] = service.StatePath
		serviceReport["stopped_and_unreachable"] = true
		row["controller"] = serviceReport
	}
	report[c.Name] = row
	return nil
}

// draftColonist sets authority to Auto and drafts the first standing colonist
// through operations_execute, returning the granted generation and the
// pawn's post-draft row (drafted, owned claim).
func draftColonist(ctx context.Context, h *na.Harness, identity map[string]any, label string) (any, map[string]any, error) {
	statusReply, err := h.Wire(ctx, label+"-status", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return nil, nil, err
	}
	_, status, err := na.Outcome(statusReply, "status")
	if err != nil {
		return nil, nil, err
	}
	statusContext, _ := na.AsMap(status["context"])
	grantReply, err := h.Wire(ctx, label+"-auto", "authority_control", map[string]any{"setMode": map[string]any{
		"identity": identity, "expectedGeneration": statusContext["nativeGeneration"], "mode": "MODE_AUTO",
	}})
	if err != nil {
		return nil, nil, err
	}
	_, grant, err := na.Outcome(grantReply, "granted")
	if err != nil {
		return nil, nil, fmt.Errorf("set Auto: %w", err)
	}
	grantContext, _ := na.AsMap(grant["context"])
	generation := grantContext["nativeGeneration"]

	listPawns := func(step string, ids []any) (map[string]any, error) {
		filter := map[string]any{"colonist": true, "downed": false}
		if ids != nil {
			filter["ids"] = ids
		}
		reply, err := h.Wire(ctx, label+"-"+step, "observations_list_pawns", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "filter": filter, "page": map[string]any{"limit": 64},
		})
		if err != nil {
			return nil, err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return nil, fmt.Errorf("%s: %w", step, err)
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) == 0 {
			return nil, fmt.Errorf("%s: no standing colonist", step)
		}
		row, _ := na.AsMap(rows[0])
		return row, nil
	}
	before, err := listPawns("pawns", nil)
	if err != nil {
		return nil, nil, err
	}
	if drafted, _ := before["drafted"].(bool); drafted {
		return nil, nil, fmt.Errorf("baseline colonist is already drafted")
	}
	pawn, _ := na.AsMap(before["pawn"])
	pawnID := na.AsString(pawn["id"])
	receiptReply, err := h.Wire(ctx, label+"-draft", "operations_execute", map[string]any{
		"precondition": map[string]any{
			"identity": identity, "expectedGeneration": generation,
			"attempt": map[string]any{"controllerSessionId": "reuseaccept", "actionId": label + "-draft", "attemptId": "1"},
		},
		"operation": map[string]any{"setDrafted": map[string]any{
			"pawn": na.Target(before), "drafted": true, "allowPersistentDraft": false,
		}},
	})
	if err != nil {
		return nil, nil, err
	}
	if _, _, err := na.Outcome(receiptReply, "receipt"); err != nil {
		return nil, nil, fmt.Errorf("draft: %w", err)
	}
	after, err := listPawns("drafted", []any{pawnID})
	if err != nil {
		return nil, nil, err
	}
	if drafted, _ := after["drafted"].(bool); !drafted {
		return nil, nil, fmt.Errorf("colonist %s is not drafted after the receipt", pawnID)
	}
	claim, _ := na.AsMap(after["draftClaim"])
	if _, owned := claim["owned"]; !owned {
		return nil, nil, fmt.Errorf("colonist %s has no owned draft claim after the receipt: %#v", pawnID, claim)
	}
	return generation, after, nil
}

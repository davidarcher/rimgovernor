// Command remoteaccept aggregates shard evidence or imports an authenticated
// GitHub Actions artifact for cmd/land. It never publishes source or dispatches.
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/remoteaccept"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "remoteaccept:", err)
		os.Exit(1)
	}
}
func run(args []string) error {
	if len(args) > 0 && args[0] == "progress" {
		// Display-only: API, startup and observation failures never change verdicts.
		if err := progressCommand(args[1:]); err != nil {
			fmt.Fprintln(os.Stderr, "remoteaccept: live progress unavailable; native verdict is unaffected")
		}
		return nil
	}
	if len(args) == 0 {
		return fmt.Errorf("usage: remoteaccept aggregate|import|export|soak|index|casetimes [flags]")
	}
	f := flag.NewFlagSet(args[0], flag.ContinueOnError)
	root := f.String("root", "", "evidence root for aggregation")
	shards := f.String("shards", "shards.json", "JSON array of shard id/status/attempts references, including cancelled jobs")
	output := f.String("output", "", "new local import directory")
	repo := f.String("repo", ".", "local task checkout (reads local Git trust pins)")
	id := f.Int64("artifact", 0, "GitHub artifact ID")
	runID := f.Int64("run", 0, "GitHub Actions run ID")
	attempt := f.Int("attempt", 1, "GitHub Actions run attempt")
	shard := f.String("shard", "", "planned shard ID for export")
	jobsFile := f.String("jobs", "", "JSON array of role/output/bootstrap paths for export")
	soak := f.String("soak", "", "soak repetition root (r<k>/<shard>/attempts.json) for soak")
	repeat := f.Int("repeat", 1, "soak repetitions, including the verdict run")
	if err := f.Parse(args[1:]); err != nil {
		return err
	}
	if f.NArg() != 0 {
		return fmt.Errorf("unexpected arguments")
	}
	switch args[0] {
	case "casetimes":
		times, err := remoteaccept.CaseTimes(*root)
		if err != nil {
			return err
		}
		return os.WriteFile(filepath.Join("internal", "remoteaccept", "casetimes.json"), remoteaccept.EncodeCaseTimes(times), 0o644)
	case "export":
		b, err := os.ReadFile(*jobsFile)
		if err != nil {
			return err
		}
		var jobs []remoteaccept.ExportJob
		if err = remoteaccept.Decode(b, &jobs); err != nil {
			return err
		}
		return remoteaccept.ExportShard(*root, *shard, jobs, []string{os.Getenv("GH_TOKEN"), os.Getenv("GITHUB_TOKEN")})
	case "aggregate":
		if *root == "" {
			return fmt.Errorf("-root is required")
		}
		r, err := remoteaccept.FileRef(*root, "run.json")
		if err != nil {
			return err
		}
		s, err := remoteaccept.FileRef(*root, "selection.json")
		if err != nil {
			return err
		}
		b, err := os.ReadFile(filepath.Join(*root, *shards))
		if err != nil {
			return err
		}
		var jobs []remoteaccept.Shard
		if err = remoteaccept.Decode(b, &jobs); err != nil {
			return err
		}
		e, evalErr := remoteaccept.Evaluate(*root, r, s, jobs)
		if evalErr != nil {
			msg := evalErr.Error()
			e.Aggregate.Passed = false
			e.Aggregate.Status = "incomplete"
			e.Aggregate.Error = &msg
		}
		ref, err := remoteaccept.WriteJSON(*root, "aggregate.json", e.Aggregate)
		if err != nil {
			return err
		}
		if evalErr != nil {
			e.Report.Passed = false
			e.Report.Error = e.Aggregate.Error
			e.Report.Remote.Aggregate = ref
			if _, err = remoteaccept.WriteJSON(*root, "result.json", e.Report); err != nil {
				return err
			}
			return evalErr
		}
		e.Report.Remote.Aggregate = ref
		if _, err = remoteaccept.WriteJSON(*root, "result.json", e.Report); err != nil {
			return err
		}
		if !e.Aggregate.Passed {
			return fmt.Errorf("%s: %s", e.Aggregate.Status, *e.Aggregate.Error)
		}
		fmt.Printf("%d cases passed; aggregate.json and result.json written (import required before landing)\n", len(e.Aggregate.Cases))
		return nil
	case "index":
		// Display only: the workflow step is continue-on-error.
		if *root == "" {
			return fmt.Errorf("-root is required")
		}
		x, err := remoteaccept.BuildIndex(*root)
		if err != nil {
			return err
		}
		if _, err = remoteaccept.WriteJSON(*root, "index.json", x); err != nil {
			return err
		}
		if s := os.Getenv("GITHUB_STEP_SUMMARY"); s != "" {
			f, err := os.OpenFile(s, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
			if err != nil {
				return err
			}
			defer f.Close()
			_, err = f.WriteString(x.Markdown())
			return err
		}
		fmt.Print(x.Markdown())
		return nil
	case "soak":
		rates, err := remoteaccept.SoakRates(*root, *soak, *repeat)
		if err != nil {
			return err
		}
		for _, r := range rates {
			fmt.Println(r)
		}
		return nil
	case "import":
		if *output == "" {
			return fmt.Errorf("-output is required")
		}
		t, err := remoteaccept.LocalTrust(*repo)
		if err != nil {
			return err
		}
		p := remoteaccept.Provenance{Trust: t, ArtifactID: *id, RunID: *runID, Attempt: *attempt}
		if err = remoteaccept.Download(remoteaccept.GitHub{}, p, *output, *repo); err != nil {
			return err
		}
		fmt.Printf("verified import: %s\n", filepath.Join(*output, "evidence", "result.json"))
		return nil
	default:
		return fmt.Errorf("unknown command %q", args[0])
	}
}

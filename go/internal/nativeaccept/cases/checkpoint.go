package cases

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// The checkpoint ring (issue #249): every case's run phase is bundled
// (save, store, clock journal, sidecar) every Options.CheckpointEvery at a
// natural pause into <root>/checkpoints/<area>/<case>/<label>/, the last
// na.CheckpointKeep entries kept, plus a final "failed" bundle when the
// run fails. The next `acceptance run` of a case whose last run in this
// root failed resumes from the ring's next entry unless -fresh; the ring is
// keyed on the root because the root is the worktree's private game copy.

// ringExcluded says why c never checkpoints, or "" when it may: a case
// that owns its process, opts out (NoCheckpoint), or measures the paused
// fraction the capture would perturb (speedmatrix, tickbudget; #200).
func ringExcluded(c Case) string {
	if _, owned := c.Start.(Owned); owned {
		return "the case owns its process lifecycle"
	}
	if c.NoCheckpoint {
		return "the case opts out (NoCheckpoint)"
	}
	for _, area := range []string{"speedmatrix/", "tickbudget/"} {
		if strings.HasPrefix(c.Name, area) {
			return "the area measures clock timing a capture would perturb"
		}
	}
	return ""
}

// RingDir is where c's checkpoint ring lives under the run's root.
func (o Options) RingDir(c Case) string {
	return filepath.Join(o.Root, "checkpoints", filepath.FromSlash(c.Name))
}

// configDir is the root's prepared GABS configuration for this run's
// profile, present once any run has prepared the root.
func (o Options) configDir(c Case) string {
	if o.Headless && !c.Rendered {
		return filepath.Join(o.Root, "config-headless")
	}
	return filepath.Join(o.Root, "config")
}

// fingerprint is what the current tree would stamp on a bundle of c: the
// installed native package, the case's Start and the environment's
// expansions. The package hash is "" (and the fingerprint unusable) until
// the root has been prepared once.
func fingerprint(c Case, configDir string) (na.Fingerprint, error) {
	game, err := na.InstalledGameDir(configDir)
	if err != nil {
		return na.Fingerprint{}, err
	}
	expansions, err := na.ExpansionsFromEnv()
	if err != nil {
		return na.Fingerprint{}, err
	}
	return na.Fingerprint{Package: na.PackageHash(game), Start: na.StartHash(c.Start.Describe()), Expansions: expansions}, nil
}

// resumption is what planResume decided: the bundle to resume from (Label
// empty when starting fresh) and the ring index it came from.
type resumption struct {
	entry    na.Checkpoint
	previous *na.Ring
}

func (r resumption) resuming() bool { return r.entry.Label != "" }

// planResume reads c's ring and decides whether this run resumes, printing
// its decision on log: the ring's pending note, `resuming <case> from
// t+7m (rev abc123, failed at t+8m); -fresh starts over`, or why a ring
// was discarded. -fresh removes the ring; -rewind steps back.
func planResume(c Case, opts Options, log io.Writer) (resumption, error) {
	dir := opts.RingDir(c)
	if opts.Fresh {
		if err := os.RemoveAll(dir); err != nil {
			return resumption{}, err
		}
		return resumption{}, nil
	}
	if opts.CheckpointEvery <= 0 || ringExcluded(c) != "" {
		return resumption{}, nil
	}
	previous, err := na.ReadRing(dir)
	if err != nil {
		return resumption{}, err
	}
	if previous == nil {
		return resumption{}, nil
	}
	if previous.Note != "" {
		fmt.Fprintln(log, previous.Note)
	}
	label := previous.Next
	if opts.Rewind > 0 && label != "" {
		rewound := previous.Rewind(opts.Rewind)
		if rewound == "" {
			fmt.Fprintf(log, "-rewind %d steps past the ring of %s; starting fresh\n", opts.Rewind, c.Name)
			return resumption{previous: previous}, nil
		}
		fmt.Fprintf(log, "-rewind %d: %s instead of %s\n", opts.Rewind, rewound, label)
		label = rewound
	}
	if label == "" {
		return resumption{previous: previous}, nil
	}
	entry, ok := previous.Entry(label)
	if !ok {
		fmt.Fprintf(log, "discarding checkpoint ring of %s: entry %s is missing; starting fresh\n", c.Name, label)
		return resumption{}, os.RemoveAll(dir)
	}
	if _, err := os.Stat(filepath.Join(entry.Path, na.CheckpointSidecar)); err != nil {
		fmt.Fprintf(log, "discarding checkpoint ring of %s: %v; starting fresh\n", c.Name, err)
		return resumption{}, os.RemoveAll(dir)
	}
	current, err := fingerprint(c, opts.configDir(c))
	if err != nil {
		fmt.Fprintf(log, "discarding checkpoint ring of %s: cannot fingerprint this root (%v); starting fresh\n", c.Name, err)
		return resumption{}, os.RemoveAll(dir)
	}
	if reason := current.Mismatch(entry); reason != "" {
		fmt.Fprintf(log, "discarding checkpoint ring of %s: %s; starting fresh\n", c.Name, reason)
		return resumption{}, os.RemoveAll(dir)
	}
	rev := previous.SourceRevision
	if rev == "" {
		rev = "unknown"
	} else if len(rev) > 8 {
		rev = rev[:8]
	}
	fmt.Fprintf(log, "resuming %s from %s (rev %s, failed at %s); -fresh starts over\n", c.Name, label, rev, na.OffsetLabel(time.Duration(previous.FailedOffsetMs)*time.Millisecond))
	return resumption{entry: entry, previous: previous}, nil
}

// newRing is the ring the run captures into, nil when c never checkpoints
// (report["checkpointing"] says why). s is consulted at every capture for
// whichever side holds the game.
func newRing(c Case, opts Options, s *session, cfg *na.Config, output string, resumed resumption, report na.Report) *na.CheckpointRing {
	if opts.CheckpointEvery <= 0 {
		report["checkpointing"] = "off"
		return nil
	}
	if reason := ringExcluded(c); reason != "" {
		report["checkpointing"] = reason
		return nil
	}
	fp, err := fingerprint(c, cfg.Configuration)
	if err != nil {
		report["checkpointing"] = "cannot fingerprint: " + err.Error()
		return nil
	}
	ring := &na.CheckpointRing{
		Dir: opts.RingDir(c), Case: c.Name, Every: opts.CheckpointEvery, Keep: na.CheckpointKeep,
		Config: cfg, StorePath: filepath.Join(output, "service.sqlite"),
		Fingerprint: fp, SourceRevision: na.SourceRevision(),
		Bridge: func() *na.Harness {
			if s.Session == nil || s.Session.Game.Released() {
				return nil
			}
			return s.Session.Harness
		},
		Service: func() *na.ServiceProcess {
			// A restarted service (ServiceProcess.Restart) is not on
			// s.services; the package tracks the latest launch.
			if p := na.LatestService(); p != nil {
				return p
			}
			for i := len(s.services) - 1; i >= 0; i-- {
				if p := s.services[i]; p.Running() {
					return p
				}
			}
			return nil
		},
	}
	if c.Serve != nil {
		ring.Serve = map[string]any{"families": c.Serve.Families, "extra": c.Serve.Extra, "resume": c.Serve.Resume}
	}
	if resumed.resuming() {
		ring.Base = time.Duration(resumed.entry.OffsetMs) * time.Millisecond
		ring.Prepared = resumed.entry.Prepared
		for _, e := range resumed.previous.Entries {
			if e.OffsetMs <= resumed.entry.OffsetMs {
				ring.Prior = append(ring.Prior, e)
			}
		}
	} else if s.Session != nil {
		ring.Prepared = s.Session.Prepared
	}
	report["checkpointing"] = fmt.Sprintf("every %s into %s", opts.CheckpointEvery, ring.Dir)
	return ring
}

// closeRing records the run's bundles on the report and leaves the ring
// for the next run: a passing run clears it; a failing run keeps the
// entries up to the resume point plus this run's, the failed bundle
// (taken here, before teardown, while the game is still open) and the
// index naming what the next run resumes from (Ring.Plan). reclaim, when
// set, takes the game back for the failed bundle after the case's own
// deferred service stop left nobody holding it.
func closeRing(ring *na.CheckpointRing, resumed resumption, runErr error, report na.Report, reclaim func(context.Context) error) {
	if ring == nil {
		return
	}
	ring.Deactivate()
	var failed *na.Checkpoint
	if runErr != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Minute)
		if !ring.Held() && reclaim != nil {
			if err := reclaim(ctx); err != nil {
				report["checkpoint_errors"] = append(ring.Errors(), "reclaim the game for the failed bundle: "+err.Error())
			}
		}
		failed = ring.Fail(ctx)
		cancel()
	}
	entries := ring.Entries()
	rows := make([]map[string]any, 0, len(entries))
	for _, e := range entries {
		rows = append(rows, map[string]any{"label": e.Label, "offset_ms": e.OffsetMs, "tick": e.Tick, "path": e.Path, "through": e.Through, "wall_ms": e.WallMs, "store": e.Store, "journal": e.Journal})
	}
	report["checkpoints"] = rows
	if errs := ring.Errors(); len(errs) > 0 {
		report["checkpoint_errors"] = errs
	}
	if runErr == nil {
		if err := os.RemoveAll(ring.Dir); err != nil {
			report["checkpoint_errors"] = append(ring.Errors(), "clear ring: "+err.Error())
		}
		return
	}
	next := &na.Ring{Case: ring.Case, SourceRevision: ring.SourceRevision, FailedOffsetMs: ring.Offset().Milliseconds(), Failed: failed}
	if failed != nil {
		next.FailedTick = failed.Tick
	}
	// The previous ring's entries up to the resume point are still this
	// timeline's past; those after it belong to the attempt that failed.
	kept := map[string]bool{}
	var merged []na.Checkpoint
	for _, e := range entries {
		if e.Label == na.FailedCheckpoint {
			continue
		}
		merged = append(merged, e)
		kept[e.Label] = true
	}
	if resumed.previous != nil {
		for _, e := range resumed.previous.Entries {
			switch {
			case kept[e.Label]:
			case resumed.resuming() && e.OffsetMs > resumed.entry.OffsetMs:
				_ = os.RemoveAll(e.Path)
			case !resumed.resuming():
				_ = os.RemoveAll(e.Path)
			default:
				merged = append(merged, e)
				kept[e.Label] = true
			}
		}
	}
	sort.SliceStable(merged, func(i, j int) bool { return merged[i].OffsetMs < merged[j].OffsetMs })
	for len(merged) > na.CheckpointKeep {
		_ = os.RemoveAll(merged[0].Path)
		merged = merged[1:]
	}
	next.Entries = merged
	next.Plan(resumed.previous, resumed.entry.Label, next.FailedTick)
	if err := next.Write(ring.Dir); err != nil {
		report["checkpoint_errors"] = append(ring.Errors(), "write ring: "+err.Error())
		return
	}
	report["checkpoint_next"] = next.Next
	if next.Note != "" {
		report["checkpoint_note"] = next.Note
	}
}

// rewindRing is closeRing for a resumed run whose game never opened on the
// bundle: the next run steps back one entry (fresh past the ring) rather
// than retrying the same bundle forever.
func rewindRing(dir string, resumed resumption, openErr error, report na.Report) {
	next := *resumed.previous
	next.Next = resumed.previous.Rewind(1)
	switch next.Next {
	case "":
		next.Note = fmt.Sprintf("%s did not open (%v) and nothing earlier is in the ring; starting fresh", resumed.entry.Label, openErr)
	default:
		next.Note = fmt.Sprintf("%s did not open (%v); rewinding to %s", resumed.entry.Label, openErr, next.Next)
	}
	if err := next.Write(dir); err != nil {
		report["checkpoint_errors"] = []string{"write ring: " + err.Error()}
		return
	}
	report["checkpoint_next"] = next.Next
	report["checkpoint_note"] = next.Note
}

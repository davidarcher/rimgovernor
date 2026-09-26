// Observation baseline (#642, head of #640). speedmatrix/observations is the
// reproducible rendered workload the observation-cost measurements are read
// against: the committed tribal8 baseline -- eight tribal colonists, the map's
// wildlife, its loose items and its standing buildings -- with the throughput
// stage applied on top (a Steel stockpile to haul to and a contiguous wood
// wall run to build), played through rimgovernor serve in a windowed launch.
// Three rows, in order: governor-off (native play at the same speed with no
// controller attached, the ungoverned update-interval ceiling), uncapped (the
// ordinary governed run) and viewer (the same governed run with one dashboard
// client streaming video). Every row asks for the same clock speed and the
// same useful work, so the difference between them is the governor's and the
// viewer's observation cost, not a different workload.
//
// The planning window the controller reads over that colony is the nontrivial
// one the issue asks for: the tribal colony's home area, not a bare debug
// patch, so planningWindow appears in the per-section split with a candidate
// count (the requested rectangle's cells) beside its returned rows.
//
// Nothing here is a benchmark framework of its own: it is one more
// registration in the matrix tier reusing the speed matrix's staging, rows,
// outcome comparison and reporting, with its own fixture start, its own stage
// save and its own row list.
package speedmatrix

import (
	"os"
	"path/filepath"
	"runtime"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// baselineSave is the committed tribal8 colony the workload starts from.
const baselineSave = "RimGovernor-tribal8-baseline"

// observationsProfile is the workload: the same stage size as the plain
// matrix (so the useful work is the work that matrix already measures) kept
// in its own save, and the three rows compared.
var observationsProfile = profile{items: items, segments: segments, ticks: ticks,
	save: "RimGovernor-observations-stage", speeds: "governor-off,uncapped,viewer,observation-load,player", observations: true}

func init() {
	cases.Register(cases.Case{
		Name: "speedmatrix/observations",
		Scope: "Observation baseline (#642): the committed tribal8 colony with the throughput stage applied, played rendered " +
			"governor-off, governed and governed with one viewer at one clock speed and one tick budget; each row reports the " +
			"companion's observation capture/format split, its per-section costs and its update-interval account beside the " +
			"clock and step phases, with the run's provenance.",
		Start: cases.Fixture{Op: prepareTool, Args: map[string]any{"itemCount": items, "wallSegments": segments},
			On: cases.Save{Name: baselineSave, From: cases.CommittedSaves()}},
		// A windowed launch: update intervals only mean what the issue asks
		// them to mean when the game is drawing, and the viewer row's video
		// capture needs Find.Camera.
		Rendered: true,
		Service:  true,
		Budget:   cases.MaxBudget,
		Matrix:   true,
		Run:      run(observationsProfile),
	})
}

// observationRow is the #642 half of a metrics row: the companion's own
// account of where an observation hop's main-thread time went, and of the
// update intervals it ran between. The nested blocks carry the full
// distributions (a Samples of 0 inside one means the recording carried none
// of that phase, which the report shows as unknown, never as zero work); the
// flat keys beside them are the headline numbers a reader compares across
// rows.
func observationRow(obs bridge.ObservationSample, frames bridge.FrameSample) map[string]any {
	return map[string]any{
		"observation": obs, "frames": frames,
		"observation_hops": obs.Hops, "capture_ms": obs.CaptureMs, "format_ms": obs.FormatMs,
		"capture_p95_ms": obs.Capture.P95, "format_p95_ms": obs.Format.P95,
		"queue_p95_ms": obs.Queue.P95, "execute_p95_ms": obs.Execute.P95,
		"format_passes": obs.FormatPasses, "payload_bytes": obs.PayloadBytes,
		"frame_updates": frames.Updates, "frame_updates_per_second": frames.UpdatesPerSecond(),
		"frame_max_interval_ms": frames.MaxIntervalMs,
		// Interval tails (#656): all updates, then only those that ran
		// main-thread observation work, so a tail is attributable.
		"frame_interval_samples": frames.Intervals.Samples, "frame_p95_ms": frames.Intervals.P95, "frame_p99_ms": frames.Intervals.P99,
		"observed_frame_samples": frames.Observed.Samples, "observed_frame_p95_ms": frames.Observed.P95,
		"observed_frame_p99_ms": frames.Observed.P99, "observed_frame_max_ms": frames.Observed.Max,
		"encode_queue_p95_ms": obs.EncodeQueue.P95, "encode_p95_ms": obs.Encode.P95, "frame_observation_share": frames.ObservationShare(),
		"frame_recorder_ms": frames.RecorderMs, "frame_cancelled_hops": frames.Cancelled,
		// Frame blocking and intentional pauses are separate measures: the
		// clock's own paused_ms/paused_fraction_native on this row is the
		// governor's deliberate stop time, never counted here.
		"frame_intervals_are": "Unity update-to-update wall on a monotonic clock, not GPU presentation intervals",
	}
}

// provenance is what the baseline was measured on (#642): what a rerun would
// have to match for the numbers to be comparable. The world (seed, save and
// its hash, fixture hash) and the installed build's hashes are the report's
// own "world" and "package_files" blocks; this names the rest.
func (m *matrix) provenance() map[string]any {
	cfg := m.s.Config()
	host, _ := os.Hostname()
	out := map[string]any{
		"revision": na.SourceRevision(),
		"headless": cfg.Headless,
		"host": map[string]any{"name": host, "os": runtime.GOOS, "arch": runtime.GOARCH,
			"cpus": runtime.NumCPU(), "gomaxprocs": runtime.GOMAXPROCS(0)},
		"speeds":      m.p.speeds,
		"tick_budget": m.p.ticks,
		"stage":       map[string]any{"save": m.p.save, "items": m.p.items, "wall_segments": m.p.segments},
		// The rows' fixed requirements: the same clock speed and the same
		// useful work, so only the observation load differs.
		"fixed":  "every row asks for the same clock speed and the same staged work; rows differ only by the controller and the viewer",
		"camera": "the stage save's own camera position, restored by the reload; no row commands the camera",
		"warmup": "the reload, the needs freeze, the plan submission and the resume precede the measured interval",
		"measured_interval": "per row: wall_seconds from resume to the tick budget; the frames and observation blocks are " +
			"differenced over the service's flight recording, which starts when the service attaches",
		"viewer": "the viewer row holds one dashboard video lease at the default cadence for the whole row; the observation-load row " +
			"adds a stalled second video lease and concurrent /api/state readers (#656); the other rows hold none",
		"readers": map[string]any{"count": na.ObservationLoadReaders, "interval_ms": na.ObservationLoadInterval.Milliseconds()},
	}
	// The mod set the launch activated: the headless profile when the run is
	// headless, the windowed profile otherwise.
	dir := "profile"
	if cfg.Headless {
		dir = "headless-profile"
	}
	if mods, err := na.ActiveMods(filepath.Join(cfg.Root, dir, "Config", "ModsConfig.xml")); err == nil {
		out["mods"] = mods
	}
	if game, err := cfg.GameSection(); err == nil {
		// The launch arguments carry the resolution and window mode of a
		// rendered run, and batch mode of a headless one.
		out["launch_args"] = game["args"]
	}
	return out
}

// observationRowProblems rejects an observation report that cannot support
// a comparison (#656): every row must carry update-interval samples, and
// every governed row observation hops, so an absent frame hook or an
// unaccounted controller reads as a failure rather than as zero cost.
func observationRowProblems(rows []map[string]any) []string {
	var problems []string
	for _, row := range rows {
		name, _ := row["case"].(string)
		if samples, _ := row["frame_interval_samples"].(uint64); samples == 0 {
			problems = append(problems, name+": no update-interval samples (frame histogram absent)")
		}
		if off, _ := row["governor_off"].(bool); !off {
			if hops, _ := row["observation_hops"].(uint64); hops == 0 {
				problems = append(problems, name+": no observation hops recorded")
			}
		}
	}
	if len(rows) == 0 {
		problems = append(problems, "no rows")
	}
	return problems
}

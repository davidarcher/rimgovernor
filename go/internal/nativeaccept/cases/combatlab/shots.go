package combatlab

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// ShotsFile is the bundle file next to ReadsFile holding the shot tap
// (scripts/fixtures/DebugStartFixture.cs, LabShotLedger; #2917): one JSON
// object per line, in game order, each tagged by "k". test/lab_stage
// action shots replies the rows; the dataset under calibration/shots stamps
// each with its run seed.
const ShotsFile = "combat_shots.jsonl"

// ShotsDir is the committed shot datasets' directory relative to the Go
// module root.
const ShotsDir = "internal/nativeaccept/cases/combatlab/calibration/shots"

// Row kinds the tap records.
const (
	KindShot   = "shot"
	KindImpact = "impact"
	KindJob    = "job"
	KindStance = "stance"
	KindDraft  = "draft"
	KindThink  = "think"
)

// Shot is one projectile launch by a pawn or building on the lab map. ID is
// the projectile's thing id number (the key of its Impact), X,Z the shooter's
// cell, Target/TX/TZ the intended target (thing id and its cell), DX,DZ the
// projectile's destination cell after the aim scatter, Burst the zero-based
// position in the verb's burst of Of, TTI the ticks to impact the launch set.
type Shot struct {
	Seed    int    `json:"seed"`
	K       string `json:"k"`
	Tick    int    `json:"tick"`
	ID      int    `json:"id"`
	Shooter string `json:"shooter"`
	Side    string `json:"side"`
	X       int    `json:"x"`
	Z       int    `json:"z"`
	Target  string `json:"target"`
	TX      int    `json:"tx"`
	TZ      int    `json:"tz"`
	DX      int    `json:"dx"`
	DZ      int    `json:"dz"`
	Burst   int    `json:"burst"`
	Of      int    `json:"of"`
	Weapon  string `json:"weapon"`
	Proj    string `json:"proj"`
	TTI     int    `json:"tti"`
}

// Impact is a projectile's Projectile.Impact call: Hit is the thing it hit
// (empty when it landed on ground), X,Z the cell it stopped in. Dealt is not
// in the tap's bundle: the dataset build adds the damage the damage ledger
// attributes to this projectile (see StampShots).
type Impact struct {
	Seed  int     `json:"seed"`
	K     string  `json:"k"`
	Tick  int     `json:"tick"`
	ID    int     `json:"id"`
	Hit   string  `json:"hit"`
	X     int     `json:"x"`
	Z     int     `json:"z"`
	Dealt float64 `json:"dealt"`
}

// Job is a Pawn_JobTracker.StartJob on a lab pawn: the job def, whether the
// player forced it (a Hands order does) and its first target.
type Job struct {
	Seed   int    `json:"seed"`
	K      string `json:"k"`
	Tick   int    `json:"tick"`
	Pawn   string `json:"pawn"`
	Side   string `json:"side"`
	Job    string `json:"job"`
	Forced bool   `json:"forced"`
	Thing  string `json:"thing"`
	X      int    `json:"x"`
	Z      int    `json:"z"`
}

// Stance is a busy stance a lab pawn entered (Stance_Warmup when it starts
// aiming, Stance_Cooldown between burst shots and after a burst) with its
// ticks left and focus target.
type Stance struct {
	Seed   int    `json:"seed"`
	K      string `json:"k"`
	Tick   int    `json:"tick"`
	Pawn   string `json:"pawn"`
	Side   string `json:"side"`
	Stance string `json:"stance"`
	Left   int    `json:"left"`
	Thing  string `json:"thing"`
	X      int    `json:"x"`
	Z      int    `json:"z"`
}

// Draft is a change of a lab pawn's drafted state.
type Draft struct {
	Seed    int    `json:"seed"`
	K       string `json:"k"`
	Tick    int    `json:"tick"`
	Pawn    string `json:"pawn"`
	Drafted bool   `json:"drafted"`
}

// Think is a pawn's first Pawn_JobTracker.JobTrackerTickInterval ticks (up to
// eight) and how many calls it had in all: the off-camera think cadence.
type Think struct {
	Seed  int    `json:"seed"`
	K     string `json:"k"`
	Pawn  string `json:"pawn"`
	Ticks []int  `json:"ticks"`
	Count int    `json:"count"`
}

// ShotSet is the rows of one or more runs, by kind, in file order.
type ShotSet struct {
	Shots   []Shot
	Impacts []Impact
	Jobs    []Job
	Stances []Stance
	Drafts  []Draft
	Thinks  []Think
}

// ParseShots reads shot rows (a bundle's combat_shots.jsonl or the dataset)
// strictly: an unknown kind or field is an error, so a schema change fails
// the committed dataset's test rather than reading as zeros.
func ParseShots(r io.Reader) (ShotSet, error) {
	var set ShotSet
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 1<<20), 1<<24)
	for line := 1; sc.Scan(); line++ {
		raw := sc.Bytes()
		if len(bytes.TrimSpace(raw)) == 0 {
			continue
		}
		var head struct {
			K string `json:"k"`
		}
		if err := json.Unmarshal(raw, &head); err != nil {
			return set, fmt.Errorf("line %d: %w", line, err)
		}
		var target any
		switch head.K {
		case KindShot:
			set.Shots = append(set.Shots, Shot{})
			target = &set.Shots[len(set.Shots)-1]
		case KindImpact:
			set.Impacts = append(set.Impacts, Impact{})
			target = &set.Impacts[len(set.Impacts)-1]
		case KindJob:
			set.Jobs = append(set.Jobs, Job{})
			target = &set.Jobs[len(set.Jobs)-1]
		case KindStance:
			set.Stances = append(set.Stances, Stance{})
			target = &set.Stances[len(set.Stances)-1]
		case KindDraft:
			set.Drafts = append(set.Drafts, Draft{})
			target = &set.Drafts[len(set.Drafts)-1]
		case KindThink:
			set.Thinks = append(set.Thinks, Think{})
			target = &set.Thinks[len(set.Thinks)-1]
		default:
			return set, fmt.Errorf("line %d: unknown kind %q", line, head.K)
		}
		dec := json.NewDecoder(bytes.NewReader(raw))
		dec.DisallowUnknownFields()
		if err := dec.Decode(target); err != nil {
			return set, fmt.Errorf("line %d (%s): %w", line, head.K, err)
		}
	}
	return set, sc.Err()
}

// writeShots saves the rows of a test/lab_stage shots reply to the bundle,
// one line each. A reply with dropped rows (the tap is bounded) is an error:
// a truncated ledger would read as fewer shots.
func writeShots(dir string, reply map[string]any) error {
	if dropped := int(na.AsNumber(reply["shotsDropped"])); dropped > 0 {
		return fmt.Errorf("shot tap dropped %d rows", dropped)
	}
	f, err := os.Create(filepath.Join(dir, ShotsFile))
	if err != nil {
		return err
	}
	enc := json.NewEncoder(f)
	for _, row := range na.AsSlice(reply["shots"]) {
		if err := enc.Encode(row); err != nil {
			f.Close()
			return err
		}
	}
	return f.Close()
}

// BundleDamage returns the damage ledger of a run bundle: the last read of
// its combat_reads.jsonl, which holds every hit since the staging.
func BundleDamage(dir string) ([]combatmetrics.Hit, error) {
	data, err := os.ReadFile(filepath.Join(dir, ReadsFile))
	if err != nil {
		return nil, err
	}
	lines := bytes.Split(bytes.TrimSpace(data), []byte("\n"))
	var last map[string]any
	if err := json.Unmarshal(lines[len(lines)-1], &last); err != nil {
		return nil, fmt.Errorf("%s: %w", ReadsFile, err)
	}
	return readRows(last).Damage, nil
}

// StampShots copies a bundle's shot rows to w in order, each with the run
// seed added, and each impact with the damage its projectile dealt: the
// total of the damage ledger rows at the impact's tick whose victim is the
// thing hit and whose instigator is the shooter of the projectile.
func StampShots(w io.Writer, seed int, bundle []byte, damage []combatmetrics.Hit) error {
	set, err := ParseShots(bytes.NewReader(bundle))
	if err != nil {
		return err
	}
	shooter := map[int]string{}
	for _, s := range set.Shots {
		shooter[s.ID] = s.Shooter
	}
	sc := bufio.NewScanner(bytes.NewReader(bundle))
	sc.Buffer(make([]byte, 1<<20), 1<<24)
	enc := json.NewEncoder(w)
	// ParseShots appended each kind in file order, so walking the lines with
	// per-kind cursors pairs every line with its parsed row.
	var shot, impact, job, stance, draft, think int
	for sc.Scan() {
		raw := bytes.TrimSpace(sc.Bytes())
		if len(raw) == 0 {
			continue
		}
		var head struct {
			K string `json:"k"`
		}
		if err := json.Unmarshal(raw, &head); err != nil {
			return err
		}
		var row any
		switch head.K {
		case KindShot:
			set.Shots[shot].Seed = seed
			row, shot = set.Shots[shot], shot+1
		case KindImpact:
			im := set.Impacts[impact]
			im.Seed, impact = seed, impact+1
			if im.Hit != "" {
				for _, h := range damage {
					if h.Tick == im.Tick && h.Victim == im.Hit && h.Instigator == shooter[im.ID] {
						im.Dealt += h.Dealt
					}
				}
			}
			row = im
		case KindJob:
			set.Jobs[job].Seed = seed
			row, job = set.Jobs[job], job+1
		case KindStance:
			set.Stances[stance].Seed = seed
			row, stance = set.Stances[stance], stance+1
		case KindDraft:
			set.Drafts[draft].Seed = seed
			row, draft = set.Drafts[draft], draft+1
		case KindThink:
			set.Thinks[think].Seed = seed
			row, think = set.Thinks[think], think+1
		}
		if err := enc.Encode(row); err != nil {
			return err
		}
	}
	return sc.Err()
}

// BuildShotDataset writes the shot dataset of a sweep: for each seed the
// shot rows of the bundle directory caseDir(seed) names, stamped by
// StampShots. A seed without a shot file is an error: the dataset covers
// every seed.
func BuildShotDataset(w io.Writer, seeds []int, caseDir func(seed int) string) error {
	for _, seed := range seeds {
		dir := caseDir(seed)
		data, err := os.ReadFile(filepath.Join(dir, ShotsFile))
		if err != nil {
			return fmt.Errorf("seed %d: %w", seed, err)
		}
		damage, err := BundleDamage(dir)
		if err != nil {
			return fmt.Errorf("seed %d: %w", seed, err)
		}
		if err := StampShots(w, seed, data, damage); err != nil {
			return fmt.Errorf("seed %d: %w", seed, err)
		}
	}
	return nil
}

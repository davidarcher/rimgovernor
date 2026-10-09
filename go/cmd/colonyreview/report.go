package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// The report reads what the run already records: result.json's timeline
// (sustainedfood.Watch: the colony census from /api/player/colony and each
// sampled concern's state, every in-game hour) and the recorder's screenshots
// under review/ (colony-<tick>.jpg, map-<tick>.jpg).

// richText matches the game's markup tags (<color=#fff>, </color>, <b>, <size=12>)
// that its labels carry for the in-game UI; the page shows plain text.
var richText = regexp.MustCompile("</?[a-zA-Z]+(=[^>]*)?>")

func plain(s string) string { return strings.TrimSpace(richText.ReplaceAllString(s, "")) }

// Pawn is one census pawn.
type Pawn struct {
	Label  string   `json:"label"`
	Downed *bool    `json:"downed"`
	Mood   *float64 `json:"mood"`
	Food   *float64 `json:"food"`
}

// Stockpile is one role kind's owned zones in a census (general, yard,
// medicine, meals ...): how many, their cells and the cells holding things.
type Stockpile struct {
	Role  string `json:"role"`
	Zones int    `json:"zones"`
	Cells int    `json:"cells"`
	Used  int    `json:"used"`
}

// Census is the timeline sample's colony block.
type Census struct {
	Error          string   `json:"error"`
	Colonists      *int64   `json:"colonists"`
	FoodRunwayDays *float64 `json:"foodRunwayDays"`
	WealthTotal    *float64 `json:"wealthTotal"`
	MoodMean       *float64 `json:"moodMean"`
	Downed         int      `json:"downed"`
	TechTier       *string  `json:"techTier"`
	// Stockpiles and ForbiddenSupplies are null until a review has filed.
	Stockpiles        []Stockpile `json:"stockpiles"`
	ForbiddenSupplies *bool       `json:"forbiddenSupplies"`
	Pawns             []Pawn      `json:"pawns"`
}

// Concern is one sampled concern's state.
type Concern struct {
	ID     string
	Need   string
	Status string
}

// Row is one hour of the report: a timeline sample and its screenshot.
type Row struct {
	Tick       int
	Day, Hour  int
	Label      string
	Census     Census
	Concerns   []Concern
	Changes    []string // concern need transitions since the previous row
	Sampled    string   // label of the timeline sample shown, when not this hour's
	ColonyShot string
	MapShot    string
	Flags      []Flag
	Anchor     string
}

// Summary is the run at a glance, saved in run.json for the site index.
type Summary struct {
	Meta           map[string]string `json:"meta"`
	Hours          int               `json:"hours"`
	FirstColonists int               `json:"first_colonists"`
	LastColonists  int               `json:"last_colonists"`
	MinMood        float64           `json:"min_mood"`
	MinFoodDays    float64           `json:"min_food_days"`
	FinalWealth    float64           `json:"final_wealth"`
	Flags          int               `json:"flags"`
	// Zones is the last reading's owned stockpile zones per role kind and
	// SuppliesForbidden whether the last reading still had starting supplies
	// forbidden.
	Zones             []Stockpile `json:"zones,omitempty"`
	SuppliesForbidden bool        `json:"supplies_forbidden"`
	Score             Score       `json:"score"`
	// Delta is the score against the previous run of the same seed;
	// nil when no baselines dir was given.
	Delta *Delta `json:"delta,omitempty"`
	Thumb string `json:"thumb"`
	Error string `json:"error,omitempty"`
}

// result is the slice of result.json the report reads.
type result struct {
	Error    string                       `json:"error"`
	Review   map[string]any               `json:"review"`
	Timeline []map[string]json.RawMessage `json:"timeline"`
}

// Load reads the case output directory: result.json and review/*.jpg.
func Load(dir string) ([]Row, result, error) {
	var res result
	data, err := os.ReadFile(filepath.Join(dir, "result.json"))
	if err != nil {
		return nil, res, err
	}
	if err := json.Unmarshal(data, &res); err != nil {
		return nil, res, fmt.Errorf("result.json: %w", err)
	}
	shots := map[string]map[int]string{"colony": {}, "map": {}}
	entries, _ := os.ReadDir(filepath.Join(dir, "review"))
	for _, e := range entries {
		var kind string
		var tick int
		name := strings.TrimSuffix(e.Name(), ".jpg")
		if k, t, ok := strings.Cut(name, "-"); ok && (k == "colony" || k == "map") {
			if n, err := strconv.Atoi(t); err == nil {
				kind, tick = k, n
			}
		}
		if kind != "" {
			shots[kind][tick] = e.Name()
		}
	}
	var rows []Row
	seen := map[int]bool{}
	for _, sample := range res.Timeline {
		var tick int
		if json.Unmarshal(sample["tick"], &tick) != nil || seen[tick] {
			continue // a sample without a live tick, or a woken duplicate
		}
		seen[tick] = true
		r := Row{Tick: tick, Day: tick/60000 + 1, Hour: tick % 60000 / 2500, Anchor: fmt.Sprintf("t%d", tick)}
		r.Label = fmt.Sprintf("Day %d, %02dh", r.Day, r.Hour)
		json.Unmarshal(sample["colony"], &r.Census)
		for i := range r.Census.Pawns {
			r.Census.Pawns[i].Label = plain(r.Census.Pawns[i].Label)
		}
		for key, raw := range sample {
			var g struct {
				Need   string `json:"need"`
				Status string `json:"status"`
			}
			if strings.HasPrefix(key, "Ensure") || strings.HasPrefix(key, "Maintain") {
				if json.Unmarshal(raw, &g) == nil && g.Need != "" {
					r.Concerns = append(r.Concerns, Concern{key, g.Need, g.Status})
				}
			}
		}
		var need, status string
		json.Unmarshal(sample["need"], &need)
		json.Unmarshal(sample["status"], &status)
		if need != "" {
			r.Concerns = append(r.Concerns, Concern{"EnsureFoodSupply", need, status})
		}
		sort.Slice(r.Concerns, func(i, j int) bool { return r.Concerns[i].ID < r.Concerns[j].ID })
		rows = append(rows, r)
	}
	sort.Slice(rows, func(i, j int) bool { return rows[i].Tick < rows[j].Tick })
	// The screenshots set the report's hours: the timeline samples when the
	// harness gets an answer, which a fast game outruns, so each hourly shot
	// is a row carrying the last sample at or before it.
	if len(shots["colony"]) > 0 && len(rows) > 0 {
		ticks := make([]int, 0, len(shots["colony"]))
		for tick := range shots["colony"] {
			ticks = append(ticks, tick)
		}
		sort.Ints(ticks)
		var hourly []Row
		for _, tick := range ticks {
			r := rows[max(sort.Search(len(rows), func(i int) bool { return rows[i].Tick > tick })-1, 0)]
			if r.Tick != tick {
				r.Sampled = r.Label
			}
			r.Tick, r.Day, r.Hour, r.Anchor = tick, tick/60000+1, tick%60000/2500, fmt.Sprintf("t%d", tick)
			r.Label = fmt.Sprintf("Day %d, %02dh", r.Day, r.Hour)
			r.ColonyShot = shots["colony"][tick]
			hourly = append(hourly, r)
		}
		rows = hourly
	}
	for tick, name := range shots["map"] {
		i := max(sort.Search(len(rows), func(i int) bool { return rows[i].Tick > tick })-1, 0)
		if i < len(rows) && rows[i].MapShot == "" {
			rows[i].MapShot = name
		}
	}
	return rows, res, nil
}

// Derive fills each row's concern changes and flags and returns the summary.
func Derive(rows []Row, meta map[string]string) Summary {
	s := Summary{Meta: meta, Hours: len(rows), MinMood: 1, MinFoodDays: -1}
	for i := range rows {
		r := &rows[i]
		var prev *Row
		if i > 0 {
			prev = &rows[i-1]
			before := map[string]string{}
			for _, g := range prev.Concerns {
				before[g.ID] = g.Need
			}
			for _, g := range r.Concerns {
				if b, ok := before[g.ID]; ok && b != g.Need {
					r.Changes = append(r.Changes, fmt.Sprintf("%s %s → %s", g.ID, b, g.Need))
				}
			}
		}
		r.Flags = flags(rows[:i+1], prev)
		s.Flags += len(r.Flags)
		c := r.Census
		if c.Stockpiles != nil {
			s.Zones = c.Stockpiles
		}
		if c.ForbiddenSupplies != nil {
			s.SuppliesForbidden = *c.ForbiddenSupplies
		}
		if c.MoodMean != nil && *c.MoodMean < s.MinMood {
			s.MinMood = *c.MoodMean
		}
		if c.FoodRunwayDays != nil && (s.MinFoodDays < 0 || *c.FoodRunwayDays < s.MinFoodDays) {
			s.MinFoodDays = *c.FoodRunwayDays
		}
		if r.ColonyShot != "" {
			s.Thumb = "review/" + r.ColonyShot
		}
	}
	if len(rows) > 0 {
		for _, r := range rows {
			if r.Census.Colonists != nil {
				if s.FirstColonists == 0 {
					s.FirstColonists = colonists(r)
				}
				s.LastColonists = colonists(r)
			}
			if r.Census.WealthTotal != nil {
				s.FinalWealth = *r.Census.WealthTotal
			}
		}
	}
	s.Score = ComputeScore(rows)
	return s
}

func colonists(r Row) int {
	if r.Census.Colonists == nil {
		return 0
	}
	return int(*r.Census.Colonists)
}

// Report reads the case output in and writes the run report to out.
func Report(in, out string, meta map[string]string, baselines string) error {
	rows, res, err := Load(in)
	if errors.Is(err, fs.ErrNotExist) {
		// The case never wrote a result (bootstrap or launch failed): the
		// page says so instead of the workflow having nothing to publish.
		res.Error = "the case left no result.json"
		if o := meta["outcome"]; o != "" {
			res.Error += " (play step: " + o + ")"
		}
		res.Error += "; see the run log"
	} else if err != nil {
		return err
	}
	merged := map[string]string{}
	for k, v := range res.Review {
		merged[k] = fmt.Sprint(v)
	}
	for k, v := range meta {
		merged[k] = v
	}
	summary := Derive(rows, merged)
	summary.Error = res.Error
	CompareToStore(&summary, baselines, filepath.Base(out))
	if err := os.MkdirAll(filepath.Join(out, "review"), 0755); err != nil {
		return err
	}
	for _, r := range rows {
		for _, shot := range []string{r.ColonyShot, r.MapShot} {
			if shot == "" {
				continue
			}
			if err := copyFile(filepath.Join(in, "review", shot), filepath.Join(out, "review", shot)); err != nil {
				return err
			}
		}
	}
	data, _ := json.MarshalIndent(summary, "", "  ")
	if err := os.WriteFile(filepath.Join(out, "run.json"), data, 0644); err != nil {
		return err
	}
	f, err := os.Create(filepath.Join(out, "index.html"))
	if err != nil {
		return err
	}
	defer f.Close()
	return renderRun(f, rows, summary)
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

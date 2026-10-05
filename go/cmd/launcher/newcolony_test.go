package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

type fakeColonyHost struct {
	mu       sync.Mutex
	observe  bool
	base     string
	saves    string
	closes   int
	starts   int
	startErr error
	saved    []NewColonySpec
}

func (h *fakeColonyHost) Observe() bool { return h.observe }
func (h *fakeColonyHost) CloseGame()    { h.mu.Lock(); h.closes++; h.mu.Unlock() }
func (h *fakeColonyHost) StartController() error {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.starts++
	return h.startErr
}
func (h *fakeColonyHost) BaseURL() string  { return h.base }
func (h *fakeColonyHost) SavesDir() string { return h.saves }
func (h *fakeColonyHost) SaveSpec(s NewColonySpec) error {
	h.mu.Lock()
	h.saved = append(h.saved, s)
	h.mu.Unlock()
	return nil
}
func (h *fakeColonyHost) Logf(string, ...any) {}
func (h *fakeColonyHost) counts() (int, int) {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.closes, h.starts
}

// colonyFixture serves the player session and hands lifecycle requests to
// the test's handler.
func colonyFixture(t *testing.T, lifecycle func(w http.ResponseWriter, r *http.Request)) (*colonyRunner, *fakeColonyHost) {
	t.Helper()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/player/session" {
			w.Write([]byte(`{"token":"tok"}`))
			return
		}
		if r.Method == http.MethodPost && r.Header.Get("X-RimGovernor-Player") != "tok" {
			w.WriteHeader(403)
			return
		}
		lifecycle(w, r)
	}))
	t.Cleanup(srv.Close)
	host := &fakeColonyHost{base: srv.URL, saves: t.TempDir()}
	r := newColonyRunner(host, DefaultNewColonySpec())
	r.poll = time.Millisecond
	return r, host
}

func waitColony(t *testing.T, r *colonyRunner, states ...string) NewColonyView {
	t.Helper()
	for deadline := time.Now().Add(10 * time.Second); time.Now().Before(deadline); time.Sleep(2 * time.Millisecond) {
		v := r.View()
		for _, s := range states {
			if v.Progress.State == s {
				return v
			}
		}
	}
	t.Fatalf("never reached %v: %+v", states, r.View().Progress)
	return NewColonyView{}
}

func pending(phase string, reroll int) string {
	b, _ := json.Marshal(map[string]any{"status": "pending", "phase": phase, "detail": "d-" + phase, "elapsedMs": 1500, "rerollCount": reroll})
	return string(b)
}

const completedBody = `{"status":"completed","requestId":"x","saveName":"RimGovernor-tribal8","seed":"used-seed","tick":"0","paused":true,"byteLength":"10"}`

func TestNewColonyDefaultsAreValid(t *testing.T) {
	if err := ValidateNewColonySpec(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	d := DefaultNewColonySpec()
	if d.Scenario != "LostTribe" || d.ColonistCount != 8 || d.Biomes[0] != "TemperateForest" || d.Difficulty != "Medium" || d.WorldTemperature != "LittleBitColder" || d.MapSize != 250 || d.PlanetCoverage != 0.3 {
		t.Fatalf("%+v", d)
	}
	has := func(opts []Option, v string) bool {
		for _, o := range opts {
			if o.Value == v {
				return true
			}
		}
		return false
	}
	o := newColonyOptions
	if !has(o.Storytellers, "RimGovernorQuiet") || !has(o.Scenarios, d.Scenario) || !has(o.Biomes, d.Biomes[0]) || !has(o.Difficulties, d.Difficulty) || !has(o.Storytellers, d.Storyteller) || !has(o.WorldTemperatures, d.WorldTemperature) {
		t.Fatalf("defaults not in the lists: %+v", o)
	}
}

func TestNewColonyListedValuesAreValid(t *testing.T) {
	for _, m := range newColonyOptions.MapSizes {
		n, err := strconv.Atoi(m.Value)
		if err != nil {
			t.Fatal(err)
		}
		s := DefaultNewColonySpec()
		s.MapSize = n
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("map size %s: %v", m.Value, err)
		}
	}
	for _, c := range newColonyOptions.PlanetCoverages {
		f, err := strconv.ParseFloat(c.Value, 32)
		if err != nil {
			t.Fatal(err)
		}
		s := DefaultNewColonySpec()
		s.PlanetCoverage = float32(f)
		if err := ValidateNewColonySpec(s); err != nil {
			t.Errorf("coverage %s: %v", c.Value, err)
		}
	}
	d := DefaultNewColonySpec()
	has := func(opts []Option, v string) bool {
		for _, o := range opts {
			if o.Value == v {
				return true
			}
		}
		return false
	}
	if !has(newColonyOptions.MapSizes, strconv.Itoa(d.MapSize)) || !has(newColonyOptions.PlanetCoverages, strconv.FormatFloat(float64(d.PlanetCoverage), 'g', -1, 32)) {
		t.Fatal("defaults are not in the lists")
	}
}

func TestDeriveSaveName(t *testing.T) {
	pattern := regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)
	spec := DefaultNewColonySpec()
	name := DeriveSaveName(spec, "abc 123/../x", nil)
	if name != "RimGovernor-LostTribe-TemperateForest-abc123x" || !pattern.MatchString(name) {
		t.Fatal(name)
	}
	if DeriveSaveName(spec, "abc 123/../x", nil) != name {
		t.Fatal("not deterministic")
	}
	spec.Biomes = nil
	if n := DeriveSaveName(spec, "s", nil); n != "RimGovernor-LostTribe-any-s" {
		t.Fatal(n)
	}
	spec.Biomes = []string{"Tundra", "Desert"}
	if n := DeriveSaveName(spec, "", nil); n != "RimGovernor-LostTribe-multi-x" {
		t.Fatal(n)
	}
	long := DeriveSaveName(NewColonySpec{Scenario: strings.Repeat("S", 80), Biomes: []string{strings.Repeat("B", 80)}}, strings.Repeat("9", 80), []string{"x"})
	if !pattern.MatchString(long) {
		t.Fatal(long)
	}
	// existing saves, case-insensitively, push the suffix up
	taken := []string{name, strings.ToLower(name) + "-2"}
	if n := DeriveSaveName(DefaultNewColonySpec(), "abc 123/../x", taken); n != name+"-3" || !pattern.MatchString(n) {
		t.Fatal(n)
	}
}

func TestNewColonyGenerateNeverOverwritesASave(t *testing.T) {
	r, host := colonyFixture(t, func(w http.ResponseWriter, r *http.Request) { w.Write([]byte(pending("generating world", 0))) })
	spec := DefaultNewColonySpec()
	spec.Seed = "fixed"
	first := "RimGovernor-LostTribe-TemperateForest-fixed"
	if err := os.WriteFile(filepath.Join(host.saves, first+".rws"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := r.Generate(spec); err != nil {
		t.Fatal(err)
	}
	v := waitColony(t, r, ColonyGenerating)
	if v.Progress.SaveName != first+"-2" || host.saved[0].SaveName != first+"-2" {
		t.Fatalf("%+v %+v", v.Progress, host.saved)
	}
	r.Cancel()
}

func TestNewColonyValidationBeforeAnyStop(t *testing.T) {
	r, host := colonyFixture(t, func(http.ResponseWriter, *http.Request) { t.Error("served") })
	hot := float32(300)
	for name, mutate := range map[string]func(*NewColonySpec){
		"colonists": func(s *NewColonySpec) { s.ColonistCount = 11 },
		"zero":      func(s *NewColonySpec) { s.ColonistCount = 0 },
		"map":       func(s *NewColonySpec) { s.MapSize = 50 },
		"coverage":  func(s *NewColonySpec) { s.PlanetCoverage = 1.5 },
		"temp":      func(s *NewColonySpec) { s.MaxTemperature = &hot },
		"scenario":  func(s *NewColonySpec) { s.Scenario = "" },
	} {
		spec := DefaultNewColonySpec()
		mutate(&spec)
		if err := r.Generate(spec); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
	if closes, starts := host.counts(); closes != 0 || starts != 0 || r.View().Progress.State != ColonyIdle {
		t.Fatalf("stopped before validating: %d %d", closes, starts)
	}
}

func TestNewColonyObserveIsANotice(t *testing.T) {
	r, host := colonyFixture(t, func(http.ResponseWriter, *http.Request) {})
	host.observe = true
	if v := r.View(); v.Unavailable == "" {
		t.Fatal("no notice")
	}
	if err := r.Generate(DefaultNewColonySpec()); err == nil || strings.Contains(err.Error(), "\n") {
		t.Fatal(err)
	}
	if closes, _ := host.counts(); closes != 0 {
		t.Fatal("stopped under Observe")
	}
}

func TestNewColonyRouteUnavailable(t *testing.T) {
	r, _ := colonyFixture(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(404)
		w.Write([]byte(`{"code":"not_found","detail":"Lifecycle is unavailable"}`))
	})
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	v := waitColony(t, r, ColonyFailed)
	if !strings.Contains(v.Progress.Error, "Observe") {
		t.Fatalf("%+v", v.Progress)
	}
}

func TestNewColonySuccessWithPhaseProgression(t *testing.T) {
	var seen []string
	var mu sync.Mutex
	var r *colonyRunner
	polls := 0
	r, host := colonyFixture(t, func(w http.ResponseWriter, req *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		if req.Method == http.MethodPost {
			var body map[string]any
			json.NewDecoder(req.Body).Decode(&body)
			spec := body["spec"].(map[string]any)
			if body["requestId"] == "" || spec["seed"] == "" || !strings.HasPrefix(spec["saveName"].(string), "RimGovernor-LostTribe-TemperateForest-") || body["timeoutMs"].(float64) < 1000 {
				t.Errorf("body %v", body)
			}
			w.WriteHeader(202)
			w.Write([]byte(pending("generating_world", 0)))
			return
		}
		polls++
		switch polls {
		case 1:
			w.Write([]byte(pending("choosing_tile", 0)))
		case 2:
			w.Write([]byte(pending("rolling_colonists", 37)))
		default:
			seen = append(seen, "")
			w.Write([]byte(completedBody))
		}
		p := r.View().Progress
		seen = append(seen, p.State+"/"+p.Phase)
	})
	spec := DefaultNewColonySpec() // random seed
	if err := r.Generate(spec); err != nil {
		t.Fatal(err)
	}
	v := waitColony(t, r, ColonyCompleted, ColonyFailed)
	if v.Progress.State != ColonyCompleted || v.Progress.SaveName != "RimGovernor-tribal8" || v.Progress.Seed != "used-seed" || v.Progress.Stale || v.Progress.ElapsedMs < 0 {
		t.Fatalf("%+v", v.Progress)
	}
	if closes, starts := host.counts(); closes != 1 || starts != 1 {
		t.Fatalf("restart %d %d", closes, starts)
	}
	if len(host.saved) != 1 || host.saved[0].Seed != "" || v.Spec.Seed != "" {
		t.Fatalf("the persisted spec keeps the seed blank for random: %+v", host.saved)
	}
	mu.Lock()
	defer mu.Unlock()
	// The handler observes the progress as of the previous reply.
	if len(seen) < 3 || !strings.HasSuffix(seen[0], "generating_world") || !strings.HasSuffix(seen[1], "choosing_tile") {
		t.Fatalf("phases %v", seen)
	}
}

func TestNewColonyKeepsRerollCountAndSeed(t *testing.T) {
	r, _ := colonyFixture(t, func(w http.ResponseWriter, req *http.Request) {
		if req.Method == http.MethodPost {
			w.WriteHeader(202)
			w.Write([]byte(pending("rolling_colonists", 12)))
			return
		}
		w.Write([]byte(completedBody))
	})
	spec := DefaultNewColonySpec()
	spec.Seed = " my seed "
	if err := r.Generate(spec); err != nil {
		t.Fatal(err)
	}
	waitColony(t, r, ColonyCompleted)
}

func TestNewColonyFailure(t *testing.T) {
	r, _ := colonyFixture(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(502)
		w.Write([]byte(`{"code":"native_invalid_request","detail":"unknown scenario Foo; valid: Crashlanded"}`))
	})
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	v := waitColony(t, r, ColonyFailed)
	if !strings.Contains(v.Progress.Error, "unknown scenario") {
		t.Fatalf("%+v", v.Progress)
	}
}

func TestNewColonyUncertainPostRetriesWithTheSameRequestID(t *testing.T) {
	var ids []string
	var mu sync.Mutex
	r, _ := colonyFixture(t, func(w http.ResponseWriter, req *http.Request) {
		var body map[string]any
		json.NewDecoder(req.Body).Decode(&body)
		mu.Lock()
		ids = append(ids, body["requestId"].(string))
		n := len(ids)
		mu.Unlock()
		if n == 1 { // the reply is lost
			hj, _ := w.(http.Hijacker)
			conn, _, _ := hj.Hijack()
			conn.Close()
			return
		}
		w.WriteHeader(201)
		w.Write([]byte(completedBody))
	})
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	waitColony(t, r, ColonyCompleted)
	mu.Lock()
	defer mu.Unlock()
	if len(ids) != 2 || ids[0] != ids[1] || ids[0] != r.View().Progress.RequestID {
		t.Fatalf("ids %v", ids)
	}
}

func TestNewColonyStaleProgressOnAFailedPoll(t *testing.T) {
	var mu sync.Mutex
	polls := 0
	proceed := make(chan struct{})
	r, _ := colonyFixture(t, func(w http.ResponseWriter, req *http.Request) {
		if req.Method == http.MethodPost {
			w.WriteHeader(202)
			w.Write([]byte(pending("rolling_colonists", 9)))
			return
		}
		mu.Lock()
		polls++
		n := polls
		mu.Unlock()
		switch {
		case n < 3:
			w.WriteHeader(503)
		case n == 3:
			<-proceed
			w.Write([]byte(completedBody))
		default:
			w.Write([]byte(completedBody))
		}
	})
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	var v NewColonyView
	for deadline := time.Now().Add(10 * time.Second); time.Now().Before(deadline); time.Sleep(2 * time.Millisecond) {
		if v = r.View(); v.Progress.Stale {
			break
		}
	}
	if !v.Progress.Stale || v.Progress.Phase != "rolling_colonists" || v.Progress.RerollCount != 9 || v.Progress.StaleError == "" || v.Progress.State != ColonyGenerating {
		t.Fatalf("%+v", v.Progress)
	}
	close(proceed)
	v = waitColony(t, r, ColonyCompleted)
	if v.Progress.Stale {
		t.Fatalf("stale after recovery: %+v", v.Progress)
	}
}

func TestNewColonyCancelMidGeneration(t *testing.T) {
	r, host := colonyFixture(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(202)
		w.Write([]byte(pending("generating_map", 0)))
	})
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	waitColony(t, r, ColonyGenerating)
	r.Cancel()
	v := r.View()
	if v.Progress.State != ColonyCancelled || v.Progress.Error != "" {
		t.Fatalf("%+v", v.Progress)
	}
	if closes, _ := host.counts(); closes != 2 { // the restart, then the cancel
		t.Fatalf("closes %d", closes)
	}
	time.Sleep(20 * time.Millisecond)
	if r.View().Progress.State != ColonyCancelled {
		t.Fatal("the run overwrote the cancel")
	}
	// A new generation can start afterwards.
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	r.Cancel()
}

func TestNewColonyControllerWillNotStart(t *testing.T) {
	r, host := colonyFixture(t, func(http.ResponseWriter, *http.Request) { t.Error("served") })
	host.startErr = http.ErrAbortHandler
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	v := waitColony(t, r, ColonyFailed)
	if !strings.Contains(v.Progress.Error, "controller did not start") {
		t.Fatalf("%+v", v.Progress)
	}
}

func TestNewColonyRefusesASecondGeneration(t *testing.T) {
	r, _ := colonyFixture(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(202)
		w.Write([]byte(pending("generating_map", 0)))
	})
	if err := r.Generate(DefaultNewColonySpec()); err != nil {
		t.Fatal(err)
	}
	if err := r.Generate(DefaultNewColonySpec()); err == nil {
		t.Fatal("second accepted")
	}
	r.Cancel()
}

func TestSettingsKeepTheNewColonySpec(t *testing.T) {
	s := DefaultSettings()
	spec := DefaultNewColonySpec()
	spec.Seed = "abc"
	s.NewColony = &spec
	path := t.TempDir() + "/launcher.json"
	if err := SaveSettings(path, s); err != nil {
		t.Fatal(err)
	}
	got, err := LoadSettings(path)
	if err != nil || got.NewColony == nil || got.NewColony.Seed != "abc" || got.NewColony.ColonistCount != 8 {
		t.Fatal(got.NewColony, err)
	}
}

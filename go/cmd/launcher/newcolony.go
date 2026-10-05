package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	bridgepkg "github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// New colony (#2025): the launcher's orchestration of POST /api/lifecycle/new
// (#2020). The page holds no labels, ordering or ranges: it renders
// NewColonyView and calls Generate / Cancel.

// NewColonySpec is the form: the wire spec with the seed optional ("" asks
// for a random one, drawn at Generate) and the temperature band optional.
type NewColonySpec struct {
	Scenario         string   `json:"scenario"`
	ColonistCount    int      `json:"colonistCount"`
	Seed             string   `json:"seed"`
	Biomes           []string `json:"biomes"`
	FlatTile         bool     `json:"flatTile"`
	Difficulty       string   `json:"difficulty"`
	Storyteller      string   `json:"storyteller"`
	MinTemperature   *float32 `json:"minTemperature,omitempty"`
	MaxTemperature   *float32 `json:"maxTemperature,omitempty"`
	WorldTemperature string   `json:"worldTemperature"`
	MapSize          int      `json:"mapSize"`
	PlanetCoverage   float32  `json:"planetCoverage"`
	SaveName         string   `json:"saveName"`
}

// DefaultNewColonySpec is the tribal-8 spec. SaveName is empty: Generate
// derives it (DeriveSaveName) and replaces any name a caller sends.
func DefaultNewColonySpec() NewColonySpec {
	return NewColonySpec{
		Scenario: "LostTribe", ColonistCount: 8, Biomes: []string{"TemperateForest"},
		Difficulty: "Medium", Storyteller: "Cassandra", WorldTemperature: "LittleBitColder",
		MapSize: 250, PlanetCoverage: 0.3,
	}
}

// Option is one choice in a list. Requires names the DLC it needs ("" for
// the base game); native refuses a def the install lacks.
type Option struct {
	Value    string `json:"value"`
	Label    string `json:"label"`
	Requires string `json:"requires,omitempty"`
}

// NewColonyOptions are the curated lists, in display order. Verified
// against the Core and DLC Defs of the game install (scenarios, biomes,
// difficulties, storytellers); the world temperature names are the
// OverallTemperature enum, which no Def lists.
type NewColonyOptions struct {
	Scenarios         []Option `json:"scenarios"`
	Biomes            []Option `json:"biomes"`
	Difficulties      []Option `json:"difficulties"`
	Storytellers      []Option `json:"storytellers"`
	WorldTemperatures []Option `json:"worldTemperatures"`
	// MapSizes and PlanetCoverages are the values RimWorld's own pages offer
	// (Dialog_AdvancedGameConfig.MapSizes, Page_CreateWorldParams.PlanetCoverages),
	// with native's labels. Values are the numbers as text.
	MapSizes        []Option `json:"mapSizes"`
	PlanetCoverages []Option `json:"planetCoverages"`
}

var newColonyOptions = NewColonyOptions{
	Scenarios: []Option{
		{"Crashlanded", "Crashlanded", ""},
		{"LostTribe", "Lost Tribe", ""},
		{"TheRichExplorer", "The Rich Explorer", ""},
		{"NakedBrutality", "Naked Brutality", ""},
		{"Mechanitor", "Mechanitor", "Biotech"},
		{"Sanguophage", "Sanguophage", "Biotech"},
		{"TheAnomaly", "The Anomaly", "Anomaly"},
		{"TheGravship", "The Gravship", "Odyssey"},
	},
	Biomes: []Option{
		{"TemperateForest", "Temperate forest", ""},
		{"TemperateSwamp", "Temperate swamp", ""},
		{"BorealForest", "Boreal forest", ""},
		{"Tundra", "Tundra", ""},
		{"ColdBog", "Cold bog", ""},
		{"IceSheet", "Ice sheet", ""},
		{"TropicalRainforest", "Tropical rainforest", ""},
		{"TropicalSwamp", "Tropical swamp", ""},
		{"AridShrubland", "Arid shrubland", ""},
		{"Desert", "Desert", ""},
		{"ExtremeDesert", "Extreme desert", ""},
		{"Grasslands", "Grassland", "Odyssey"},
		{"Glowforest", "Glowforest", "Odyssey"},
		{"GlacialPlain", "Glacial plain", "Odyssey"},
		{"Scarlands", "Scarlands", "Odyssey"},
		{"LavaField", "Lava field", "Odyssey"},
	},
	Difficulties: []Option{
		{"Peaceful", "Peaceful", ""},
		{"Easy", "Easy", ""},
		{"Medium", "Medium", ""},
		{"Rough", "Rough", ""},
		{"Hard", "Hard", ""},
		{"Extreme", "Extreme", ""},
	},
	Storytellers: []Option{
		{"Cassandra", "Cassandra Classic", ""},
		{"Phoebe", "Phoebe Chillax", ""},
		{"Randy", "Randy Random", ""},
		{"RimGovernorQuiet", "Quiet (no incidents)", ""},
	},
	WorldTemperatures: []Option{
		{"VeryCold", "Very cold", ""},
		{"Cold", "Cold", ""},
		{"LittleBitColder", "A little bit colder", ""},
		{"Normal", "Normal", ""},
		{"LittleBitWarmer", "A little bit warmer", ""},
		{"Hot", "Hot", ""},
		{"VeryHot", "Very hot", ""},
	},
	// Native MapSizes are 200 to 325 in steps of 25, labelled by the
	// MapSizeDesc key ({0}x{0} ({1} cells)); 350 and 400 are test-only.
	MapSizes: []Option{
		{"200", "200x200 (40000 cells)", ""},
		{"225", "225x225 (50625 cells)", ""},
		{"250", "250x250 (62500 cells)", ""},
		{"275", "275x275 (75625 cells)", ""},
		{"300", "300x300 (90000 cells)", ""},
		{"325", "325x325 (105625 cells)", ""},
	},
	// Native PlanetCoverages are 0.3, 0.5 and 1, labelled ToStringPercent;
	// 0.05 is dev mode only.
	PlanetCoverages: []Option{
		{"0.3", "30%", ""},
		{"0.5", "50%", ""},
		{"1", "100%", ""},
	},
}

// NewColonyRanges are the validation ranges the form shows.
type NewColonyRanges struct {
	ColonistsMin   int     `json:"colonistsMin"`
	ColonistsMax   int     `json:"colonistsMax"`
	TemperatureAbs float64 `json:"temperatureAbs"`
}

var newColonyRanges = NewColonyRanges{
	ColonistsMin: 1, ColonistsMax: bridgepkg.NewColonyMaxColonists,
	TemperatureAbs: bridgepkg.NewColonyMaxTemperature,
}

// Generation states. Active ones: restarting, connecting, generating.
const (
	ColonyIdle       = "idle"
	ColonyRestarting = "restarting" // closing the game, starting the controller
	ColonyConnecting = "connecting" // waiting for the controller and game
	ColonyGenerating = "generating" // native phases (Progress.Phase)
	ColonyCompleted  = "completed"
	ColonyFailed     = "failed"
	ColonyCancelled  = "cancelled"
)

// NewColonyProgress is the last good progress. Phase, Detail, NativeElapsedMs
// and RerollCount are the newest native pending reply; ElapsedMs is the
// launcher's own clock since Generate, so a page timer keeps ticking while
// native blocks. A failed poll marks it Stale with StaleError and keeps the
// values; Error is the terminal failure or notice.
type NewColonyProgress struct {
	State           string `json:"state"`
	RequestID       string `json:"requestId,omitempty"`
	Phase           string `json:"phase,omitempty"`
	Detail          string `json:"detail,omitempty"`
	NativeElapsedMs uint64 `json:"nativeElapsedMs"`
	RerollCount     uint32 `json:"rerollCount"`
	ElapsedMs       int64  `json:"elapsedMs"`
	Stale           bool   `json:"stale"`
	StaleError      string `json:"staleError,omitempty"`
	SaveName        string `json:"saveName,omitempty"`
	Seed            string `json:"seed,omitempty"`
	Error           string `json:"error,omitempty"`
}

// NewColonyView is what the page polls.
type NewColonyView struct {
	Options  NewColonyOptions  `json:"options"`
	Ranges   NewColonyRanges   `json:"ranges"`
	Defaults NewColonySpec     `json:"defaults"`
	Spec     NewColonySpec     `json:"spec"` // the last one generated, else the defaults
	Progress NewColonyProgress `json:"progress"`
	// Unavailable is a one-line notice when generation cannot run (Observe mode).
	Unavailable string `json:"unavailable,omitempty"`
}

// ValidateNewColonySpec refuses a spec the wire would refuse, before any
// process is stopped. Whether a defName exists is native's call.
func ValidateNewColonySpec(s NewColonySpec) error {
	if s.SaveName == "" {
		s.SaveName = savePrefix + "x"
	}
	_, err := wireSpec(s, "x")
	return err
}

func wireSpec(s NewColonySpec, seed string) (*l.NewColonySpec, error) {
	if s.ColonistCount < 0 || s.MapSize < 0 {
		return nil, errors.New("colonist count and map size must be positive")
	}
	spec := &l.NewColonySpec{
		Scenario: proto.String(s.Scenario), ColonistCount: proto.Uint32(uint32(s.ColonistCount)),
		Seed: proto.String(seed), Biomes: s.Biomes, FlatTile: proto.Bool(s.FlatTile),
		Difficulty: proto.String(s.Difficulty), Storyteller: proto.String(s.Storyteller),
		MinTemperature: s.MinTemperature, MaxTemperature: s.MaxTemperature,
		MapSize: proto.Uint32(uint32(s.MapSize)), PlanetCoverage: proto.Float32(s.PlanetCoverage),
		SaveName: proto.String(s.SaveName),
	}
	if s.WorldTemperature != "" {
		spec.WorldTemperature = proto.String(s.WorldTemperature)
	}
	if err := bridgepkg.ValidateNewColonySpec(spec); err != nil {
		return nil, errors.New(strings.TrimPrefix(err.Error(), bridgepkg.ErrContract.Error()+": "))
	}
	return spec, nil
}

const savePrefix = "RimGovernor-"

// DeriveSaveName is the save name for spec and seed: RimGovernor-<scenario>-
// <biome, "any" for none, "multi" for several>-<seed slug>, within the wire's
// save-name pattern. It is deterministic for a spec; when existing (the save
// names in the saves folder, compared case-insensitively) holds that name it
// appends -2, -3, ... so a generate never overwrites a save.
func DeriveSaveName(spec NewColonySpec, seed string, existing []string) string {
	biome := "any"
	switch len(spec.Biomes) {
	case 0:
	case 1:
		biome = spec.Biomes[0]
	default:
		biome = "multi"
	}
	slug := func(s string, max int) string {
		var b strings.Builder
		for _, c := range s {
			if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9') && b.Len() < max {
				b.WriteRune(c)
			}
		}
		if b.Len() == 0 {
			return "x"
		}
		return b.String()
	}
	base := savePrefix + slug(spec.Scenario, 16) + "-" + slug(biome, 18) + "-" + slug(seed, 12)
	taken := make(map[string]bool, len(existing))
	for _, e := range existing {
		taken[strings.ToLower(e)] = true
	}
	name := base
	for n := 2; taken[strings.ToLower(name)]; n++ {
		name = base + "-" + strconv.Itoa(n)
	}
	return name
}

// colonyHost is what the generation flow needs of the launcher app.
type colonyHost interface {
	Observe() bool
	// CloseGame stops the controller and the game.
	CloseGame()
	// StartController starts the controller afresh (no load, no continued
	// state) and returns once it answers, or why it did not.
	StartController() error
	BaseURL() string
	// SavesDir is the saves folder the launcher lists.
	SavesDir() string
	SaveSpec(NewColonySpec) error
	Logf(format string, args ...any)
}

// colonyRunner runs one generation at a time.
type colonyRunner struct {
	host colonyHost
	http *http.Client
	now  func() time.Time
	// poll is the wait between requests; timeout bounds the generation after
	// the controller answers (it is also the wire's timeoutMs).
	poll    time.Duration
	timeout time.Duration

	mu       sync.Mutex
	spec     NewColonySpec
	progress NewColonyProgress
	started  time.Time
	ended    time.Time
	cancel   context.CancelFunc
}

func newColonyRunner(host colonyHost, spec NewColonySpec) *colonyRunner {
	return &colonyRunner{host: host, http: &http.Client{Timeout: 15 * time.Second}, now: time.Now,
		poll: time.Second, timeout: time.Duration(bridgepkg.NewColonyMaxTimeoutMs) * time.Millisecond,
		spec: spec, progress: NewColonyProgress{State: ColonyIdle}}
}

func (r *colonyRunner) active() bool {
	switch r.progress.State {
	case ColonyRestarting, ColonyConnecting, ColonyGenerating:
		return true
	}
	return false
}

// View is the page's snapshot.
func (r *colonyRunner) View() NewColonyView {
	r.mu.Lock()
	defer r.mu.Unlock()
	v := NewColonyView{Options: newColonyOptions, Ranges: newColonyRanges, Defaults: DefaultNewColonySpec(), Spec: r.spec, Progress: r.progress}
	if r.progress.State != ColonyIdle {
		end := r.now()
		if !r.active() {
			end = r.ended
		}
		v.Progress.ElapsedMs = end.Sub(r.started).Milliseconds()
	}
	if r.host.Observe() {
		v.Unavailable = observeNotice
	}
	return v
}

const observeNotice = "generating a colony needs control: turn off Observe mode"

// Generate validates spec, then (in the background) restarts the game and
// generates the colony. It returns a refusal before anything is stopped.
func (r *colonyRunner) Generate(spec NewColonySpec) error {
	if r.host.Observe() {
		return errors.New(observeNotice)
	}
	seed := strings.TrimSpace(spec.Seed)
	spec.Seed = seed
	if seed == "" {
		var b [6]byte
		rand.Read(b[:])
		seed = hex.EncodeToString(b[:])
	}
	spec.SaveName = DeriveSaveName(spec, seed, ListSaves(r.host.SavesDir()))
	if _, err := wireSpec(spec, seed); err != nil {
		return err
	}
	r.mu.Lock()
	if r.active() {
		r.mu.Unlock()
		return errors.New("a colony is already being generated")
	}
	ctx, cancel := context.WithCancel(context.Background())
	id := "launcher-new-" + strconv.FormatInt(r.now().UnixNano(), 36)
	r.cancel, r.spec, r.started = cancel, spec, r.now()
	r.progress = NewColonyProgress{State: ColonyRestarting, RequestID: id, Seed: seed, SaveName: spec.SaveName}
	r.mu.Unlock()
	if err := r.host.SaveSpec(spec); err != nil {
		r.host.Logf("new colony: save the spec: %v", err)
	}
	r.host.Logf("new colony: generating %s (scenario %s, seed %s, request %s)", spec.SaveName, spec.Scenario, seed, id)
	go r.run(ctx, spec, seed, id)
	return nil
}

// Cancel ends the controller and the game (native has no cancel op) and
// reports cancelled. It is a no-op when nothing is generating.
func (r *colonyRunner) Cancel() {
	r.mu.Lock()
	if !r.active() {
		r.mu.Unlock()
		return
	}
	r.progress.State, r.progress.Error = ColonyCancelled, ""
	r.ended = r.now()
	cancel := r.cancel
	r.mu.Unlock()
	cancel()
	r.host.Logf("new colony: cancelled; closing the game")
	r.host.CloseGame()
}

// update applies f unless the run already ended (Cancel wins).
func (r *colonyRunner) update(f func(p *NewColonyProgress)) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.active() {
		return false
	}
	f(&r.progress)
	return true
}

func (r *colonyRunner) fail(msg string) {
	if r.update(func(p *NewColonyProgress) { p.State, p.Error, p.Stale = ColonyFailed, msg, false }) {
		r.mu.Lock()
		r.ended = r.now()
		r.mu.Unlock()
		r.host.Logf("new colony: failed: %s", msg)
	}
}

func (r *colonyRunner) run(ctx context.Context, spec NewColonySpec, seed, id string) {
	r.host.CloseGame()
	if ctx.Err() != nil {
		return
	}
	if err := r.host.StartController(); err != nil {
		if ctx.Err() == nil {
			r.fail("The controller did not start: " + err.Error())
		}
		return
	}
	if !r.update(func(p *NewColonyProgress) { p.State = ColonyConnecting }) {
		return
	}
	ctx, stop := context.WithTimeout(ctx, r.timeout)
	defer stop()
	timeoutMs := uint32(r.timeout / time.Millisecond)
	body, _ := json.Marshal(newColonyBody{RequestID: id, TimeoutMs: timeoutMs, Spec: wireBody(spec, seed)})
	base := strings.TrimRight(r.host.BaseURL(), "/")
	token, posted := "", false
	for ; ; r.sleep(ctx) {
		if ctx.Err() != nil {
			if errors.Is(ctx.Err(), context.DeadlineExceeded) {
				r.fail("Timed out waiting for the colony.")
			}
			return
		}
		var status int
		var data []byte
		var err error
		switch {
		case token == "":
			status, data, err = r.call(ctx, http.MethodGet, base+"/api/player/session", nil, "")
			if err == nil && status == http.StatusOK {
				var session struct {
					Token string `json:"token"`
				}
				if json.Unmarshal(data, &session) == nil && session.Token != "" {
					token = session.Token
					continue
				}
			}
			if err == nil && status == http.StatusNotFound {
				r.fail("This controller does not serve new colonies (Observe mode).")
				return
			}
			r.uncertain(err, status)
			continue
		case !posted:
			status, data, err = r.call(ctx, http.MethodPost, base+"/api/lifecycle/new", body, token)
		default:
			status, data, err = r.call(ctx, http.MethodGet, base+"/api/lifecycle/new?requestId="+url.QueryEscape(id), nil, token)
		}
		if ctx.Err() != nil {
			continue
		}
		var failure httpapi.Failure
		switch {
		case err != nil:
			r.uncertain(err, 0) // an unresolved POST is retried with the same request id
		case status == http.StatusOK || status == http.StatusCreated || status == http.StatusAccepted:
			var reply colonyReply
			if json.Unmarshal(data, &reply) != nil {
				r.uncertain(errors.New("undecodable reply"), status)
				continue
			}
			posted = true
			if reply.Status == "completed" {
				r.update(func(p *NewColonyProgress) {
					p.State, p.Stale, p.StaleError, p.SaveName = ColonyCompleted, false, "", reply.SaveName
					if reply.Seed != "" {
						p.Seed = reply.Seed
					}
					p.Phase, p.Detail = "", ""
				})
				r.mu.Lock()
				r.ended = r.now()
				r.mu.Unlock()
				r.host.Logf("new colony: %s saved (seed %s)", reply.SaveName, reply.Seed)
				return
			}
			r.update(func(p *NewColonyProgress) {
				p.State, p.Stale, p.StaleError = ColonyGenerating, false, ""
				p.Phase, p.Detail, p.NativeElapsedMs, p.RerollCount = reply.Phase, reply.Detail, reply.ElapsedMs, reply.RerollCount
			})
		case status == http.StatusForbidden:
			token = "" // the controller restarted under us; read a fresh session
		default:
			json.Unmarshal(data, &failure)
			terminal := status == http.StatusConflict || (status >= 400 && status < 500) || (status == http.StatusBadGateway && strings.HasPrefix(failure.Code, "native_"))
			switch {
			case status == http.StatusNotFound && failure.Code == "not_found":
				r.fail("This controller does not serve new colonies (Observe mode).")
				return
			case terminal:
				msg := failure.Detail
				if msg == "" {
					msg = "HTTP " + strconv.Itoa(status)
				}
				r.fail(msg)
				return
			}
			r.uncertain(nil, status)
		}
	}
}

// uncertain keeps the last good progress and marks it stale; the caller
// retries (a POST with the same request id, a GET again).
func (r *colonyRunner) uncertain(err error, status int) {
	msg := "HTTP " + strconv.Itoa(status)
	if err != nil {
		msg = err.Error()
	}
	r.update(func(p *NewColonyProgress) { p.Stale, p.StaleError = true, msg })
}

func (r *colonyRunner) sleep(ctx context.Context) {
	select {
	case <-ctx.Done():
	case <-time.After(r.poll):
	}
}

func (r *colonyRunner) call(ctx context.Context, method, target string, body []byte, token string) (int, []byte, error) {
	req, err := http.NewRequestWithContext(ctx, method, target, bytes.NewReader(body))
	if err != nil {
		return 0, nil, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if token != "" && method == http.MethodPost {
		req.Header.Set("X-RimGovernor-Player", token)
	}
	resp, err := r.http.Do(req)
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	return resp.StatusCode, data, err
}

// The request and reply bodies of /api/lifecycle/new (httpapi's DTOs are unexported).
type newColonyBody struct {
	RequestID string        `json:"requestId"`
	TimeoutMs uint32        `json:"timeoutMs"`
	Spec      newColonyWire `json:"spec"`
}
type newColonyWire struct {
	Scenario         string   `json:"scenario"`
	ColonistCount    int      `json:"colonistCount"`
	Seed             string   `json:"seed"`
	Biomes           []string `json:"biomes"`
	FlatTile         bool     `json:"flatTile"`
	Difficulty       string   `json:"difficulty"`
	Storyteller      string   `json:"storyteller"`
	MinTemperature   *float32 `json:"minTemperature"`
	MaxTemperature   *float32 `json:"maxTemperature"`
	WorldTemperature string   `json:"worldTemperature"`
	MapSize          int      `json:"mapSize"`
	PlanetCoverage   float32  `json:"planetCoverage"`
	SaveName         string   `json:"saveName"`
}

func wireBody(s NewColonySpec, seed string) newColonyWire {
	biomes := s.Biomes
	if biomes == nil {
		biomes = []string{}
	}
	return newColonyWire{s.Scenario, s.ColonistCount, seed, biomes, s.FlatTile, s.Difficulty, s.Storyteller,
		s.MinTemperature, s.MaxTemperature, s.WorldTemperature, s.MapSize, s.PlanetCoverage, s.SaveName}
}

type colonyReply struct {
	Status      string `json:"status"`
	Phase       string `json:"phase"`
	Detail      string `json:"detail"`
	ElapsedMs   uint64 `json:"elapsedMs"`
	RerollCount uint32 `json:"rerollCount"`
	SaveName    string `json:"saveName"`
	Seed        string `json:"seed"`
}

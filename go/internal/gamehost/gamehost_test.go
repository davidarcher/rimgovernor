package gamehost

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

// The test binary doubles as the game and as a short-lived controller:
//
//	gamehost-game <envfile>          write GABP_*/GABS_*/probe env, then park
//	gamehost-launch <stateDir> <envfile>  Launch the game role and exit
func TestMain(m *testing.M) {
	if len(os.Args) >= 3 {
		switch os.Args[1] {
		case "gamehost-game":
			var lines []string
			for _, kv := range os.Environ() {
				u := strings.ToUpper(kv)
				if strings.HasPrefix(u, "GABP_") || strings.HasPrefix(u, "GABS_") || strings.HasPrefix(u, "GAMEHOST_PROBE") {
					lines = append(lines, kv)
				}
			}
			_ = os.WriteFile(os.Args[2], []byte(strings.Join(lines, "\n")), 0o644)
			for {
				time.Sleep(time.Hour)
			}
		case "gamehost-exit":
			time.Sleep(200 * time.Millisecond)
			code, _ := strconv.Atoi(os.Args[2])
			os.Exit(code)
		case "gamehost-launch":
			g, err := Launch(context.Background(), gameSpec(os.Args[2], os.Args[3]))
			if err != nil {
				fmt.Println("error:", err)
				os.Exit(2)
			}
			fmt.Println(g.PID())
			os.Exit(0)
		}
	}
	os.Exit(m.Run())
}

func gameSpec(stateDir, envFile string) Spec {
	exe, _ := os.Executable()
	return Spec{
		GameID:     "fixture",
		Executable: exe,
		Args:       []string{"gamehost-game", envFile},
		Env:        map[string]string{"GAMEHOST_PROBE": "override"},
		StateDir:   stateDir,
	}
}

func launch(t *testing.T) (*Game, string, string) {
	t.Helper()
	dir := t.TempDir()
	envFile := filepath.Join(dir, "env.txt")
	g, err := Launch(context.Background(), gameSpec(dir, envFile))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = g.Stop(context.Background()) })
	return g, dir, envFile
}

func waitFile(t *testing.T, path string) string {
	t.Helper()
	deadline := time.Now().Add(60 * time.Second)
	for time.Now().Before(deadline) {
		if data, err := os.ReadFile(path); err == nil && len(data) > 0 {
			return string(data)
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatalf("%s never written", path)
	return ""
}

func TestLaunchPassesEnvAndWritesEndpoint(t *testing.T) {
	t.Setenv("GABP_TOKEN", "inherited")
	t.Setenv("GABS_CONFIG_DIR", "inherited")
	t.Setenv("GAMEHOST_PROBE", "parent")
	g, dir, envFile := launch(t)

	env := waitFile(t, envFile)
	for _, want := range []string{
		fmt.Sprintf("GABP_SERVER_PORT=%d", g.Endpoint().Port),
		"GABP_TOKEN=" + g.Token(),
		"GABS_GAME_ID=fixture",
		"GAMEHOST_PROBE=override",
	} {
		if !strings.Contains(env, want) {
			t.Errorf("child env lacks %q:\n%s", want, env)
		}
	}
	if strings.Contains(env, "inherited") {
		t.Errorf("inherited GABS_/GABP_ leaked:\n%s", env)
	}

	var ep Endpoint
	data, err := os.ReadFile(endpointPath(dir, "fixture"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(data, &ep); err != nil {
		t.Fatal(err)
	}
	if ep != g.Endpoint() || ep.SchemaVersion != SchemaVersion || ep.Generation != 1 || len(ep.Token) != 64 || ep.PIDStartTime == 0 {
		t.Errorf("endpoint = %+v, game = %+v", ep, g.Endpoint())
	}
	if !strings.HasPrefix(g.Addr(), "127.0.0.1:") {
		t.Errorf("Addr = %q", g.Addr())
	}
}

func TestAttachWhileAliveThenNotAfterStop(t *testing.T) {
	g, dir, _ := launch(t)
	a, err := Attach(dir, "fixture")
	if err != nil {
		t.Fatal(err)
	}
	if a.Endpoint() != g.Endpoint() || !a.Alive() {
		t.Fatalf("attached %+v alive=%v", a.Endpoint(), a.Alive())
	}
	if err := a.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if g.Alive() {
		t.Fatal("game alive after Stop")
	}
	select {
	case <-g.Done():
	case <-time.After(10 * time.Second):
		t.Fatal("Done not closed")
	}
	if _, err := Attach(dir, "fixture"); !errors.Is(err, ErrNotRunning) {
		t.Fatalf("Attach after Stop = %v", err)
	}
	if _, err := os.Stat(endpointPath(dir, "fixture")); !os.IsNotExist(err) {
		t.Fatalf("endpoint.json survives Stop: %v", err)
	}
	g2, err := Launch(context.Background(), gameSpec(dir, filepath.Join(dir, "env2.txt")))
	if err != nil {
		t.Fatal(err)
	}
	defer g2.Stop(context.Background())
	if g2.Generation() != 2 {
		t.Fatalf("generation = %d, want 2", g2.Generation())
	}
}

func TestAttachRejectsReusedPID(t *testing.T) {
	g, dir, _ := launch(t)
	ep := g.Endpoint()
	ep.PIDStartTime++
	if err := writeJSONAtomic(endpointPath(dir, "fixture"), ep); err != nil {
		t.Fatal(err)
	}
	if _, err := Attach(dir, "fixture"); !errors.Is(err, ErrNotRunning) {
		t.Fatalf("Attach with tampered start time = %v", err)
	}
	if _, err := os.Stat(endpointPath(dir, "fixture")); !os.IsNotExist(err) {
		t.Fatalf("stale endpoint.json kept: %v", err)
	}
}

func TestLaunchRefusesLiveGame(t *testing.T) {
	g, dir, _ := launch(t)
	_, err := Launch(context.Background(), gameSpec(dir, filepath.Join(dir, "env2.txt")))
	var running *AlreadyRunningError
	if !errors.As(err, &running) || !errors.Is(err, ErrAlreadyRunning) || running.PID != g.PID() {
		t.Fatalf("second Launch = %v", err)
	}
}

// TestGameOutlivesController launches from a separate controller process
// that exits at once; the game must still be attachable afterwards.
func TestGameOutlivesController(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	dir := t.TempDir()
	envFile := filepath.Join(dir, "env.txt")
	exe, _ := os.Executable()
	out, err := exec.Command(exe, "gamehost-launch", dir, envFile).CombinedOutput()
	if err != nil {
		t.Fatalf("controller: %v\n%s", err, out)
	}
	waitFile(t, envFile)
	g, err := Attach(dir, "fixture")
	if err != nil {
		t.Fatalf("Attach after controller exit: %v (controller said %s)", err, out)
	}
	defer g.Stop(context.Background())
	if strings.TrimSpace(string(out)) != fmt.Sprint(g.PID()) {
		t.Fatalf("controller launched %s, endpoint names %d", out, g.PID())
	}
}

// A closed game exits 0 and a crashed one does not; LastExit tells the
// two apart for the most recent launch only.
func TestLastExitRecordsTheLaunchExitCode(t *testing.T) {
	exe, _ := os.Executable()
	dir := t.TempDir()
	for _, want := range []int{0, 3} {
		g, err := Launch(context.Background(), Spec{GameID: "fixture", Executable: exe, Args: []string{"gamehost-exit", strconv.Itoa(want)}, StateDir: dir})
		if err != nil {
			t.Fatal(err)
		}
		deadline := time.Now().Add(60 * time.Second)
		code, ok := LastExit(dir, "fixture")
		for !ok && time.Now().Before(deadline) {
			time.Sleep(20 * time.Millisecond)
			code, ok = LastExit(dir, "fixture")
		}
		if !ok || code != want {
			t.Fatalf("LastExit = %d, %v; want %d", code, ok, want)
		}
		_ = g.Stop(context.Background())
	}
	if err := os.WriteFile(filepath.Join(dir, "fixture", "generation"), []byte("9"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, ok := LastExit(dir, "fixture"); ok {
		t.Fatal("LastExit reported an older launch's exit")
	}
}

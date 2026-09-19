package nativeaccept

import (
	"context"
	"fmt"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
)

// TestMain doubles as the fake `rimgovernor serve` the launch tests spawn:
// under RIMGOVERNOR_FAKE_SERVE it prints the startup line and then waits
// to be killed (or exits at once with the requested status).
func TestMain(m *testing.M) {
	if mode := os.Getenv("RIMGOVERNOR_FAKE_SERVE"); mode != "" {
		switch mode {
		case "exit":
			fmt.Fprintln(os.Stderr, "[clock-worker] step failed: Fields: context deadline exceeded")
			os.Exit(3)
		case "garbage":
			fmt.Println("not a service")
			os.Exit(0)
		case "pprof":
			// A real listener with the controller's /debug/pprof routes, so
			// the launch's profile captures have something to collect.
			listener, err := net.Listen("tcp", "127.0.0.1:0")
			if err != nil {
				os.Exit(2)
			}
			fmt.Println("RimGovernor Go player service: http://" + listener.Addr().String())
			_ = (&http.Server{Handler: httpapi.PprofHandler(), WriteTimeout: time.Second}).Serve(listener)
			os.Exit(0)
		}
		fmt.Println("RimGovernor Go player service: http://127.0.0.1:1")
		fmt.Fprintln(os.Stderr, "fake serve: "+strings.Join(os.Args[1:], " "))
		time.Sleep(time.Minute)
		os.Exit(0)
	}
	os.Exit(m.Run())
}

func TestServeArgsFixesTheSharedFlags(t *testing.T) {
	t.Setenv(ClockSpeedEnv, "Ultrafast")
	cfg := &Config{Configuration: "C:/cfg", GameID: "rimworld"}
	argv := ServeArgs(cfg, "C:/gabs.exe", "C:/out/service-profile", "C:/out/service.sqlite", "C:/out/flight.jsonl", ServeSpec{Resume: true, Extra: []string{"--routine-project-limit", "2"}})
	joined := strings.Join(argv, " ")
	for _, want := range []string{
		"serve --profile C:/out/service-profile --gabs C:/gabs.exe --config C:/cfg --game rimworld --state C:/out/service.sqlite",
		"--listen 127.0.0.1:0", "--refresh 1s", "--timeout 15s", "--flight-recorder C:/out/flight.jsonl",
		"--clock-speed Ultrafast --clock-test-acceleration", "--pprof", "--resume", "--routine-project-limit 2",
	} {
		if !strings.Contains(joined, want) {
			t.Errorf("argv %q lacks %q", joined, want)
		}
	}
	if strings.Index(joined, "--clock-speed") > strings.Index(joined, "--routine-project-limit") {
		t.Errorf("Extra must follow the shared flags so a harness can override them: %q", joined)
	}
	if got := ServeArgs(cfg, "g", "p", "s", "f", ServeSpec{NativeTimeout: 45 * time.Second}); !strings.Contains(strings.Join(got, " "), "--timeout 45s") {
		t.Errorf("NativeTimeout not applied: %q", got)
	}
	// RIMGOVERNOR_ACCEPT_CLOCK_SPEED is the only knob (#128): without it the
	// shared Ultrafast default applies, and exactly one --clock-speed is
	// emitted either way. A harness naming its own speed in Extra gets no
	// shared pair at all, so the boost never rides beside a slower speed.
	t.Setenv(ClockSpeedEnv, "")
	if got := strings.Join(ServeArgs(cfg, "g", "p", "s", "f", ServeSpec{}), " "); !strings.Contains(got, "--clock-speed Ultrafast --clock-test-acceleration") || strings.Count(got, "--clock-speed") != 1 {
		t.Errorf("default speed: %q", got)
	}
	if got := strings.Join(ServeArgs(cfg, "g", "p", "s", "f", ServeSpec{Extra: []string{"--clock-speed", "Normal"}}), " "); !strings.Contains(got, "--clock-speed Normal") || strings.Count(got, "--clock-speed") != 1 || strings.Contains(got, "--clock-test-acceleration") {
		t.Errorf("harness speed: %q", got)
	}
	t.Setenv(PprofEnv, "0")
	if got := strings.Join(ServeArgs(cfg, "g", "p", "s", "f", ServeSpec{}), " "); strings.Contains(got, "--pprof") {
		t.Errorf("%s=0 must drop --pprof: %q", PprofEnv, got)
	}
}

// A launch profiles the service for the report's budget and Stop ends the
// CPU profile and takes the heap snapshot before the kill, both on disk
// and on the launch's entry (#301).
func TestLaunchServeCapturesProfilesAtStop(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	cfg := &Config{Output: t.TempDir(), Configuration: "cfg", GameID: "g"}
	report := NewReport("test", true)
	report.SetBudget(5 * time.Minute)
	p, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "pprof"), 1, report)
	if err != nil {
		t.Fatal(err)
	}
	defer p.Stop()
	// Past the fake's WriteTimeout the capture must still be in flight.
	time.Sleep(1500 * time.Millisecond)
	p.Stop()
	entry := report["service"].(map[string]any)
	pprof, ok := entry["pprof"].(map[string]any)
	if !ok || pprof["seconds"] != 300 {
		t.Fatalf("pprof entry %#v", entry["pprof"])
	}
	for _, name := range []string{"cpu", "heap"} {
		want := filepath.Join(cfg.Output, "service", name+".pprof")
		if pprof[name] != want {
			t.Fatalf("%s: %v", name, pprof[name])
		}
		data, err := os.ReadFile(want)
		if err != nil || len(data) < 2 || data[0] != 0x1f || data[1] != 0x8b {
			t.Fatalf("%s: %d bytes, %v", want, len(data), err)
		}
	}
}

// A service that is gone before Stop skips its captures without failing.
func TestLaunchServeSkipsProfilesWhenTheServiceIsGone(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	cfg := &Config{Output: t.TempDir(), Configuration: "cfg", GameID: "g"}
	report := NewReport("test", true)
	p, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "run"), 1, report)
	if err != nil {
		t.Fatal(err)
	}
	p.Stop()
	pprof, _ := report["service"].(map[string]any)["pprof"].(map[string]any)
	if pprof["seconds"] != defaultProfileSeconds {
		t.Fatalf("seconds %v", pprof["seconds"])
	}
	for _, name := range []string{"cpu", "heap"} {
		if outcome, _ := pprof[name].(string); !strings.HasPrefix(outcome, "skipped: ") {
			t.Fatalf("%s: %v", name, pprof[name])
		}
	}
}

func fakeServeSpec(t *testing.T, mode string) ServeSpec {
	t.Helper()
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	return ServeSpec{Binary: exe, Env: []string{"RIMGOVERNOR_FAKE_SERVE=" + mode}, Families: []string{"haul", "work"}}
}

func TestLaunchServeRecordsTheServiceAndItsExit(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	cfg := &Config{Output: t.TempDir(), Configuration: "cfg", GameID: "g"}
	report := NewReport("test", true)
	p, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "run"), 1, report)
	if err != nil {
		t.Fatal(err)
	}
	defer p.Stop()
	if p.URL != "http://127.0.0.1:1" || p.PID == 0 || p.Launch != 1 {
		t.Fatalf("handle %+v", p)
	}
	entry, ok := report["service"].(map[string]any)
	if !ok {
		t.Fatalf("report[service] = %#v", report["service"])
	}
	if entry["url"] != p.URL || entry["pid"] != p.PID || entry["state"] != "running" || entry["families"] != "haul,work" {
		t.Fatalf("entry %#v", entry)
	}
	if argv, _ := entry["argv"].([]string); len(argv) < 2 || argv[1] != "serve" {
		t.Fatalf("argv %#v", entry["argv"])
	}
	if p.StatePath != filepath.Join(cfg.Output, "service.sqlite") || p.StderrPath() != filepath.Join(cfg.Output, "service", "stderr.log") {
		t.Fatalf("paths %s %s", p.StatePath, p.StderrPath())
	}
	if err := p.Exited(); err != nil {
		t.Fatalf("running service reported exited: %v", err)
	}
	if p.Stop() != nil {
		t.Fatal("Stop without KeepAuthority must return no keep-alive counters")
	}
	if entry["state"] != "stopped" {
		t.Fatalf("state after stop %v", entry["state"])
	}
	if err := p.Exited(); err == nil {
		t.Fatal("stopped service must be terminal")
	}
	if err := p.AssertStopped(); err != nil {
		t.Fatal(err)
	}
	// A second launch on the same run gets its own directory and entry.
	q, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "run"), 2, report)
	if err != nil {
		t.Fatal(err)
	}
	defer q.Stop()
	if q.StderrPath() != filepath.Join(cfg.Output, "service-2", "stderr.log") || report["service_2"] == nil {
		t.Fatalf("restart paths %s %#v", q.StderrPath(), report["service_2"])
	}
}

func TestLaunchServeExitIsTerminalAndNamesTheStepFailure(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	cfg := &Config{Output: t.TempDir(), Configuration: "cfg", GameID: "g"}
	report := NewReport("test", true)
	if _, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "exit"), 1, report); err == nil {
		t.Fatal("a service that exits before its startup line must fail the launch")
	}
	if _, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "garbage"), 1, report); err == nil || !strings.Contains(err.Error(), "unexpected service startup line") {
		t.Fatalf("garbage startup line: %v", err)
	}
	p, err := launchServe(ctx, cfg, "gabs", fakeServeSpec(t, "run"), 1, report)
	if err != nil {
		t.Fatal(err)
	}
	defer p.Stop()
	_ = p.cmd.Process.Kill()
	deadline := time.Now().Add(10 * time.Second)
	for p.Exited() == nil {
		if time.Now().After(deadline) {
			t.Fatal("killed service never reported exited")
		}
		time.Sleep(50 * time.Millisecond)
	}
	if state, _ := report["service"].(map[string]any)["state"].(string); !strings.HasPrefix(state, "exited: ") {
		t.Fatalf("state %q", state)
	}
	// The stall error names the last step failure in the stderr log.
	if err := os.WriteFile(p.StderrPath(), []byte("[clock-worker] step failed: Fields: context deadline exceeded\n"), 0644); err != nil {
		t.Fatal(err)
	}
	stall := &StepStallError{Stall: time.Minute, Families: "haul,work", LastFailure: lastStepFailure(p.StderrPath())}
	if !strings.Contains(stall.Error(), "Fields: context deadline exceeded") {
		t.Fatalf("stall error %q", stall)
	}
}

func TestClockSpeedFlagsAddTheBoostAtUltrafast(t *testing.T) {
	if got := strings.Join(ClockSpeedFlags("Fast"), " "); got != "--clock-speed Fast" {
		t.Fatalf("Fast: %q", got)
	}
	if got := strings.Join(ClockSpeedFlags("Ultrafast"), " "); got != "--clock-speed Ultrafast --clock-test-acceleration" {
		t.Fatalf("Ultrafast: %q", got)
	}
}

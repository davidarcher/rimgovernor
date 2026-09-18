package nativeaccept

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
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
		"--clock-speed Ultrafast --clock-test-acceleration", "--resume", "--routine-project-limit 2",
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
	// shared Superfast default applies, and exactly one --clock-speed is
	// emitted either way.
	t.Setenv(ClockSpeedEnv, "")
	if got := strings.Join(ServeArgs(cfg, "g", "p", "s", "f", ServeSpec{}), " "); !strings.Contains(got, "--clock-speed Superfast") || strings.Count(got, "--clock-speed") != 1 {
		t.Errorf("default speed: %q", got)
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

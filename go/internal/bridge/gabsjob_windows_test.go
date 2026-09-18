package bridge

import (
	"bufio"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"golang.org/x/sys/windows"
)

// gabsJobHelperMain is the TestMain hook for the two helper roles of
// TestGABSDiesWithControllerButNotItsGame: "gabs-controller <configDir>"
// opens a Client against the test binary's own fake GABS, prints that GABS's
// PID and parks; "park" parks. The fake GABS itself, when the
// RIMGOVERNOR_TEST_GABS_CHILD environment variable names a file, starts a
// parked child (the game stand-in) and writes its PID there.
func gabsJobHelperMain(args []string) bool {
	if len(args) < 2 {
		return false
	}
	switch args[1] {
	case "park":
		for {
			time.Sleep(time.Hour)
		}
	case "gabs-controller":
		executable, _ := os.Executable()
		client, err := Open(context.Background(), ProcessConfig{Executable: executable, ConfigDir: args[2], GameID: "fixture", Timeout: 10 * time.Second, Spawned: func(pid int) { fmt.Println(pid) }})
		if err != nil {
			fmt.Println("error:", err)
			os.Exit(2)
		}
		defer client.Close()
		for {
			time.Sleep(time.Hour)
		}
	}
	return false
}

func gabsJobFakeChild() {
	path := os.Getenv("RIMGOVERNOR_TEST_GABS_CHILD")
	if path == "" {
		return
	}
	executable, _ := os.Executable()
	child := exec.Command(executable, "park")
	if err := child.Start(); err != nil {
		return
	}
	_ = os.WriteFile(path, []byte(strconv.Itoa(child.Process.Pid)), 0o644)
}

func processAlive(pid int) bool {
	handle, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if err != nil {
		return false
	}
	defer windows.CloseHandle(handle)
	var code uint32
	if err := windows.GetExitCodeProcess(handle, &code); err != nil {
		return false
	}
	return code == 259 // STILL_ACTIVE
}

func awaitProcessGone(pid int, within time.Duration) bool {
	deadline := time.Now().Add(within)
	for time.Now().Before(deadline) {
		if !processAlive(pid) {
			return true
		}
		time.Sleep(50 * time.Millisecond)
	}
	return !processAlive(pid)
}

func TestGABSDiesWithControllerButNotItsGame(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	childFile := filepath.Join(t.TempDir(), "game.pid")
	controller := exec.Command(executable, "gabs-controller", t.TempDir())
	controller.Env = append(os.Environ(), "RIMGOVERNOR_TEST_GABS_CHILD="+childFile)
	stdout, err := controller.StdoutPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err := controller.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { _ = controller.Process.Kill(); _ = controller.Wait() }()
	line, err := bufio.NewReader(stdout).ReadString('\n')
	if err != nil {
		t.Fatal(err)
	}
	gabsPID, err := strconv.Atoi(strings.TrimSpace(line))
	if err != nil {
		t.Fatalf("controller reported %q", line)
	}
	var gamePID int
	for deadline := time.Now().Add(5 * time.Second); time.Now().Before(deadline) && gamePID == 0; time.Sleep(50 * time.Millisecond) {
		if raw, err := os.ReadFile(childFile); err == nil {
			gamePID, _ = strconv.Atoi(strings.TrimSpace(string(raw)))
		}
	}
	if gamePID == 0 {
		t.Fatal("fake GABS never reported its game child")
	}
	defer func() {
		if p, err := os.FindProcess(gamePID); err == nil {
			_ = p.Kill()
		}
	}()
	if !processAlive(gabsPID) || !processAlive(gamePID) {
		t.Fatal("GABS or its game not running before the controller died")
	}
	// The controller dies without Close, as a killed serve or harness does.
	if err := controller.Process.Kill(); err != nil {
		t.Fatal(err)
	}
	_ = controller.Wait()
	if !awaitProcessGone(gabsPID, 5*time.Second) {
		t.Fatal("GABS outlived its controller")
	}
	if !processAlive(gamePID) {
		t.Fatal("the game GABS launched died with the controller")
	}
}

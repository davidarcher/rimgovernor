//go:build windows

// Command launcher is the double-click RimGovernor launcher: it brings the
// controller, native mod and game layout up to date, then
// starts and stops `rimgovernor serve` from a small WebView2 window.
// Build it once from go/:
//
//	go build -ldflags -H=windowsgui -o ..\RimGovernorLauncher.exe ./cmd/launcher
package main

import (
	"context"
	_ "embed"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"syscall"
	"unsafe"

	"github.com/jchv/go-webview2"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

//go:embed ui/index.html
var indexHTML string

func init() { runtime.LockOSThread() }

var (
	kernel32 = syscall.NewLazyDLL("kernel32.dll")
	user32   = syscall.NewLazyDLL("user32.dll")
)

// hideConsole gives this GUI process a hidden console: every console child
// it (or the setup package it calls) spawns inherits it instead of
// flashing a window of its own.
func hideConsole() {
	kernel32.NewProc("AllocConsole").Call()
	if h, _, _ := kernel32.NewProc("GetConsoleWindow").Call(); h != 0 {
		user32.NewProc("ShowWindow").Call(h, 0)
	}
}

func fatal(msg string) {
	text, _ := syscall.UTF16PtrFromString(msg)
	title, _ := syscall.UTF16PtrFromString("RimGovernor launcher")
	user32.NewProc("MessageBoxW").Call(0, uintptr(unsafe.Pointer(text)), uintptr(unsafe.Pointer(title)), 0x10)
	os.Exit(1)
}

// findRepo is the checkout the launcher belongs to: the one enclosing its
// executable, else the working directory's.
func findRepo() (string, bool) {
	if exe, err := os.Executable(); err == nil {
		if repo, ok := na.FindRepo(filepath.Dir(exe)); ok {
			return repo, true
		}
	}
	wd, _ := os.Getwd()
	return na.FindRepo(wd)
}

func main() {
	hideConsole()
	repo, ok := findRepo()
	if !ok {
		fatal("RimGovernorLauncher.exe must sit inside the RimGovernor checkout (no .git found above it).")
	}
	a := newApp(repo)
	w := webview2.NewWithOptions(webview2.WebViewOptions{
		DataPath:      filepath.Join(repo, ".rimgovernor", "launcher-webview"),
		AutoFocus:     true,
		WindowOptions: webview2.WindowOptions{Title: "RimGovernor", Width: 860, Height: 760, Center: true},
	})
	if w == nil {
		fatal("Could not open a WebView2 window. Install the Microsoft Edge WebView2 Runtime and try again.")
	}
	defer w.Destroy()
	problems := newProblemsRefresh(a.recorder)
	for name, f := range map[string]any{
		"getState":          a.view,
		"saveSettings":      a.saveSettings,
		"focused":           func() { go a.focus() },
		"play":              func() { go a.play() },
		"stopController":    func() { go a.stop() },
		"restart":           func() { go a.restart() },
		"closeGame":         func() { go a.closeGame() },
		"getEvents":         a.tail.rows,
		"getProblems":       problems.poll,
		"allProblems":       problems.allProblems,
		"getNewColony":      a.colony.View,
		"generateNewColony": a.colony.Generate,
		"cancelNewColony":   func() { go a.colony.Cancel() },
		"getAcceptCases":    a.accept.Cases,
		"getAcceptRun":      a.accept.View,
		"runAcceptCase":     a.accept.Run,
		"stopAcceptCase":    a.accept.Stop,
		"openLink":          openLink,
	} {
		if err := w.Bind(name, f); err != nil {
			fatal(err.Error())
		}
	}
	// The Now tab: the page polls these at the serve client's
	// cadences while the tab is visible; no timer lives here.
	serve := NewServeClient(a.serveURL)
	for name, f := range map[string]any{
		"getNowHeader": func() HeaderView { return headerView(serve.State(context.Background())) },
		"getLedger":    func() LedgerPage { return ledgerPage(serve.Ledger(context.Background())) },
		"getNowReport": func() ReportView {
			return reportView(serve.Now(context.Background()), serve.Routines(context.Background()))
		},
	} {
		if err := w.Bind(name, f); err != nil {
			fatal(err.Error())
		}
	}
	if hwnd := uintptr(w.Window()); hwnd != 0 {
		placement := filepath.Join(repo, ".rimgovernor", "launcher-window.json")
		restoreWindow(hwnd, placement)
		go rememberWindow(hwnd, placement)
		go watchGameWindow(filepath.Join(repo, ".rimgovernor", "game-window.json"))
	}
	w.SetHtml(indexHTML)
	go a.focus()
	go a.monitor()
	w.Run()
}

// openLink opens an allowlisted URL in the default browser.
func openLink(url string) error {
	if err := allowedLink(url); err != nil {
		return err
	}
	return exec.Command("rundll32", "url.dll,FileProtocolHandler", url).Start()
}

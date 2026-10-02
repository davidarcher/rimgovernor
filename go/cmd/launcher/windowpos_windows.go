package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"syscall"
	"time"
	"unsafe"
)

// windowRect is the launcher window's last outer bounds in screen pixels.
type windowRect struct {
	X, Y, W, H int32
}

var (
	user32DLL        = syscall.NewLazyDLL("user32.dll")
	procGetWindowRec = user32DLL.NewProc("GetWindowRect")
	procSetWindowPos = user32DLL.NewProc("SetWindowPos")
	procMetrics      = user32DLL.NewProc("GetSystemMetrics")
	procIsIconic     = user32DLL.NewProc("IsIconic")
)

func metric(i uintptr) int32 { r, _, _ := procMetrics.Call(i); return int32(r) }

// onScreen: enough of r lies inside the virtual desktop to grab and drag.
func (r windowRect) onScreen() bool {
	vx, vy, vw, vh := metric(76), metric(77), metric(78), metric(79)
	return r.W >= 200 && r.H >= 200 && r.X+r.W > vx+80 && r.X < vx+vw-80 && r.Y+80 < vy+vh && r.Y >= vy-20
}

func loadWindowRect(path string) (windowRect, bool) {
	var r windowRect
	data, err := os.ReadFile(path)
	if err != nil || json.Unmarshal(data, &r) != nil {
		return r, false
	}
	return r, r.onScreen()
}

// restoreWindow moves hwnd to the saved bounds; no file leaves it centred.
func restoreWindow(hwnd uintptr, path string) {
	if r, ok := loadWindowRect(path); ok {
		const noZOrder, noActivate = 0x4, 0x10
		procSetWindowPos.Call(hwnd, 0, uintptr(r.X), uintptr(r.Y), uintptr(r.W), uintptr(r.H), noZOrder|noActivate)
	}
}

// rememberWindow saves the window's bounds whenever they change.
func rememberWindow(hwnd uintptr, path string) {
	var last windowRect
	for range time.Tick(time.Second) {
		if iconic, _, _ := procIsIconic.Call(hwnd); iconic != 0 {
			continue
		}
		var rc struct{ L, T, R, B int32 }
		if ok, _, _ := procGetWindowRec.Call(hwnd, uintptr(unsafe.Pointer(&rc))); ok == 0 {
			return
		}
		r := windowRect{rc.L, rc.T, rc.R - rc.L, rc.B - rc.T}
		if r == last || !r.onScreen() {
			continue
		}
		last = r
		if data, err := json.Marshal(r); err == nil {
			os.MkdirAll(filepath.Dir(path), 0755)
			os.WriteFile(path, data, 0644)
		}
	}
}

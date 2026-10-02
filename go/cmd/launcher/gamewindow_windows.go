package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"syscall"
	"time"
	"unsafe"
)

// RimWorld is a Unity player and does not remember where its window was, so
// the launcher does: whenever a Unity window appears it is moved to the
// bounds it last had, and the bounds are saved as the player moves it.
// Fullscreen or screen-sized windows are neither saved nor restored.

var (
	procEnumWindows     = user32DLL.NewProc("EnumWindows")
	procGetClassName    = user32DLL.NewProc("GetClassNameW")
	procIsWindowVisible = user32DLL.NewProc("IsWindowVisible")
)

// unityWindows lists the visible top-level Unity player windows.
func unityWindows() []uintptr {
	var found []uintptr
	cb := syscall.NewCallback(func(hwnd, _ uintptr) uintptr {
		if vis, _, _ := procIsWindowVisible.Call(hwnd); vis == 0 {
			return 1
		}
		buf := make([]uint16, 64)
		n, _, _ := procGetClassName.Call(hwnd, uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
		if syscall.UTF16ToString(buf[:n]) == "UnityWndClass" {
			found = append(found, hwnd)
		}
		return 1
	})
	procEnumWindows.Call(cb, 0)
	return found
}

// windowed reports bounds that are a real window, not a screen-sized one.
func (r windowRect) windowed() bool {
	return r.onScreen() && (r.W < metric(0) || r.H < metric(1))
}

// watchGameWindow restores and remembers the game window for as long as the
// launcher runs.
func watchGameWindow(path string) {
	var known uintptr
	var last windowRect
	for range time.Tick(time.Second) {
		wins := unityWindows()
		if len(wins) == 0 {
			known = 0
			continue
		}
		hwnd := wins[0]
		if hwnd != known {
			known, last = hwnd, windowRect{}
			if r, ok := loadWindowRect(path); ok && r.windowed() {
				const noZOrder, noActivate = 0x4, 0x10
				procSetWindowPos.Call(hwnd, 0, uintptr(r.X), uintptr(r.Y), uintptr(r.W), uintptr(r.H), noZOrder|noActivate)
				last = r
			}
			continue
		}
		if iconic, _, _ := procIsIconic.Call(hwnd); iconic != 0 {
			continue
		}
		var rc struct{ L, T, R, B int32 }
		if ok, _, _ := procGetWindowRec.Call(hwnd, uintptr(unsafe.Pointer(&rc))); ok == 0 {
			continue
		}
		r := windowRect{rc.L, rc.T, rc.R - rc.L, rc.B - rc.T}
		if r == last || !r.windowed() {
			continue
		}
		last = r
		if data, err := json.Marshal(r); err == nil {
			os.MkdirAll(filepath.Dir(path), 0755)
			os.WriteFile(path, data, 0644)
		}
	}
}

//go:build !windows

package main

import "syscall"

// detachedAttr starts a child in its own session, so it survives the
// caller's terminal closing.
func detachedAttr() *syscall.SysProcAttr { return &syscall.SysProcAttr{Setsid: true} }

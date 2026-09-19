package main

import "syscall"

// detachedAttr starts a child in its own process group with no console,
// so it survives the caller's console closing.
func detachedAttr() *syscall.SysProcAttr {
	const detachedProcess = 0x00000008
	return &syscall.SysProcAttr{CreationFlags: syscall.CREATE_NEW_PROCESS_GROUP | detachedProcess}
}

// Package runtimeowner enforces one controller process per configured game profile.
package runtimeowner

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
)

var ErrOwned = errors.New("game profile already has a controller owner")

// Owner must remain open until all native writes and owned workers have stopped.
// Kernel ownership ends on process exit, including a crash. The lock file remains
// in place: deleting it would let another process lock a different inode.
type Owner struct {
	mu   sync.Mutex
	file *os.File
}

// Acquire uses the existing, shared game profile directory, not a task worktree or
// per-controller database directory. Every writer for a profile must use this lock.
// Acquisition is nonblocking; readers do not acquire ownership.
func Acquire(ctx context.Context, profileDirectory string) (*Owner, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if profileDirectory == "" {
		return nil, errors.New("game profile directory is required")
	}
	abs, err := filepath.Abs(profileDirectory)
	if err != nil {
		return nil, err
	}
	canonical, err := filepath.EvalSymlinks(abs)
	if err != nil {
		return nil, err
	}
	info, err := os.Stat(canonical)
	if err != nil {
		return nil, err
	}
	if !info.IsDir() {
		return nil, errors.New("game profile must be a directory")
	}
	path := filepath.Join(canonical, "rimgovernor-controller.lock")
	if info, err := os.Lstat(path); err == nil && !info.Mode().IsRegular() {
		return nil, errors.New("controller lock must be a regular file")
	} else if err != nil && !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	if err = lock(file); err != nil {
		file.Close()
		return nil, fmt.Errorf("controller ownership: %w", err)
	}
	owner := &Owner{file: file}
	if err = ctx.Err(); err != nil {
		owner.Close()
		return nil, err
	}
	return owner, nil
}

func (o *Owner) Close() error {
	o.mu.Lock()
	defer o.mu.Unlock()
	if o.file == nil {
		return nil
	}
	file := o.file
	o.file = nil
	return file.Close()
}

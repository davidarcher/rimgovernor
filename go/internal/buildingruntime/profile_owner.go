// Profile ownership: one controller process per configured game profile. Every
// writer for a profile takes this lock; readers never do.
package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"sync"
)

var ErrProfileOwned = errors.New("game profile already has a controller owner")

// ProfileOwnerPIDFile names, inside the profile directory, the file holding
// the pid of the process that took the lock. The launcher reads it by this
// name (cmd/launcher keeps its own copy of the constant).
const ProfileOwnerPIDFile = "rimgovernor-controller.pid"

// ProfileOwner must remain open until all native writes and owned workers have stopped.
// Kernel ownership ends on process exit, including a crash. The lock file remains
// in place: deleting it would let another process lock a different inode.
type ProfileOwner struct {
	mu   sync.Mutex
	file *os.File
}

// AcquireProfile uses the existing, shared game profile directory, not a task worktree or
// per-controller database directory. Every writer for a profile must use this lock.
// Acquisition is nonblocking; readers do not acquire ownership.
func AcquireProfile(ctx context.Context, profileDirectory string) (*ProfileOwner, error) {
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
	if err = lockProfile(file); err != nil {
		file.Close()
		return nil, fmt.Errorf("controller ownership: %w", err)
	}
	// Record who holds the lock beside it, so a launcher starting a
	// replacement can stop an owner that no longer listens on any port (a
	// controller hung after its bridge dropped kept the profile for good).
	// Best effort: the lock, not this file, is the ownership.
	_ = os.WriteFile(filepath.Join(canonical, ProfileOwnerPIDFile), []byte(strconv.Itoa(os.Getpid())), 0600)
	owner := &ProfileOwner{file: file}
	if err = ctx.Err(); err != nil {
		owner.Close()
		return nil, err
	}
	return owner, nil
}

func (o *ProfileOwner) Close() error {
	o.mu.Lock()
	defer o.mu.Unlock()
	if o.file == nil {
		return nil
	}
	file := o.file
	o.file = nil
	return file.Close()
}

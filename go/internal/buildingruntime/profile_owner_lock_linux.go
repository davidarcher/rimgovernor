package buildingruntime

import (
	"errors"
	"golang.org/x/sys/unix"
	"os"
)

func lockProfile(file *os.File) error {
	err := unix.Flock(int(file.Fd()), unix.LOCK_EX|unix.LOCK_NB)
	if errors.Is(err, unix.EWOULDBLOCK) || errors.Is(err, unix.EAGAIN) {
		return ErrProfileOwned
	}
	return err
}

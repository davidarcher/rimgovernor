package snapshotshm

import (
	"encoding/binary"
	"fmt"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// OpenFileMappingW is not wrapped by x/sys/windows.
var procOpenFileMapping = windows.NewLazySystemDLL("kernel32.dll").NewProc("OpenFileMappingW")

type windowsMapping struct {
	file, ready windows.Handle
	addr        uintptr
	view        []byte
}

// openMapping maps a named ring (Local\RimGovernorSnapshot-<id>) read-only
// and, when ready is set, opens its <name>-ready event, both created by the
// native side.
func openMapping(name string, ready bool) (mapping, error) {
	mappingName, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return nil, err
	}
	readyName, err := windows.UTF16PtrFromString(name + "-ready")
	if err != nil {
		return nil, err
	}
	handle, _, callErr := procOpenFileMapping.Call(uintptr(windows.FILE_MAP_READ), 0, uintptr(unsafe.Pointer(mappingName)))
	if handle == 0 {
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, callErr)
	}
	m := &windowsMapping{file: windows.Handle(handle)}
	if ready {
		if m.ready, err = windows.OpenEvent(windows.SYNCHRONIZE, false, readyName); err != nil {
			_ = m.close()
			return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
		}
	}
	// Map the header first to learn the ring's size, then the whole ring.
	header, err := windows.MapViewOfFile(m.file, windows.FILE_MAP_READ, 0, 0, headerBytes)
	if err != nil {
		_ = m.close()
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	head := unsafe.Slice((*byte)(*(*unsafe.Pointer)(unsafe.Pointer(&header))), headerBytes)
	size := uintptr(headerBytes) + uintptr(binary.LittleEndian.Uint32(head[8:12]))*uintptr(binary.LittleEndian.Uint32(head[12:16]))
	_ = windows.UnmapViewOfFile(header)
	if m.addr, err = windows.MapViewOfFile(m.file, windows.FILE_MAP_READ, 0, 0, size); err != nil {
		_ = m.close()
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	// The view is memory Windows owns for the life of the mapping, never a Go
	// allocation, so converting the raw address is the intended use here.
	m.view = unsafe.Slice((*byte)(*(*unsafe.Pointer)(unsafe.Pointer(&m.addr))), size)
	return m, nil
}

func (m *windowsMapping) bytes() []byte { return m.view }

func (m *windowsMapping) wait(d time.Duration) bool {
	if m.ready == 0 {
		return false
	}
	event, err := windows.WaitForSingleObject(m.ready, uint32(max(d.Milliseconds(), 1)))
	return err == nil && event == windows.WAIT_OBJECT_0
}

func (m *windowsMapping) close() error {
	var first error
	if m.addr != 0 {
		first = windows.UnmapViewOfFile(m.addr)
		m.addr, m.view = 0, nil
	}
	for _, h := range []*windows.Handle{&m.ready, &m.file} {
		if *h != 0 {
			if err := windows.CloseHandle(*h); err != nil && first == nil {
				first = err
			}
			*h = 0
		}
	}
	return first
}

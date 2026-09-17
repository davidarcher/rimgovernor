package videoshm

import (
	"fmt"
	"unsafe"

	"golang.org/x/sys/windows"
)

// OpenFileMappingW is not wrapped by x/sys/windows.
var procOpenFileMapping = windows.NewLazySystemDLL("kernel32.dll").NewProc("OpenFileMappingW")

type windowsMapping struct {
	file, mutex windows.Handle
	addr        uintptr
	view        []byte
}

// Open maps the named buffer (Local\RimGovernorVideo-<id>) read-only and opens
// its companion <name>-lock mutex, both created by the native driver.
func Open(name string) (Reader, error) {
	mappingName, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return nil, err
	}
	mutexName, err := windows.UTF16PtrFromString(name + "-lock")
	if err != nil {
		return nil, err
	}
	handle, _, callErr := procOpenFileMapping.Call(uintptr(windows.FILE_MAP_READ), 0, uintptr(unsafe.Pointer(mappingName)))
	if handle == 0 {
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, callErr)
	}
	m := &windowsMapping{file: windows.Handle(handle)}
	m.mutex, err = windows.OpenMutex(windows.SYNCHRONIZE|windows.MUTEX_MODIFY_STATE, false, mutexName)
	if err != nil {
		_ = m.close()
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	m.addr, err = windows.MapViewOfFile(m.file, windows.FILE_MAP_READ, 0, 0, Capacity)
	if err != nil {
		_ = m.close()
		return nil, fmt.Errorf("%w: %v", ErrUnavailable, err)
	}
	// The view is memory Windows owns for the life of the mapping, never a Go
	// allocation, so converting the raw address is the intended use here.
	m.view = unsafe.Slice((*byte)(*(*unsafe.Pointer)(unsafe.Pointer(&m.addr))), Capacity)
	return &reader{m: m}, nil
}

func (m *windowsMapping) bytes() []byte { return m.view }

func (m *windowsMapping) lock() bool {
	event, err := windows.WaitForSingleObject(m.mutex, uint32(lockWait.Milliseconds()))
	// An abandoned mutex (producer died mid-publish) still grants ownership;
	// the header check in parseFrame bounds what a torn write can do.
	return err == nil && (event == windows.WAIT_OBJECT_0 || event == windows.WAIT_ABANDONED)
}

func (m *windowsMapping) unlock() { _ = windows.ReleaseMutex(m.mutex) }

func (m *windowsMapping) close() error {
	var first error
	if m.addr != 0 {
		first = windows.UnmapViewOfFile(m.addr)
		m.addr, m.view = 0, nil
	}
	if m.mutex != 0 {
		if err := windows.CloseHandle(m.mutex); err != nil && first == nil {
			first = err
		}
		m.mutex = 0
	}
	if m.file != 0 {
		if err := windows.CloseHandle(m.file); err != nil && first == nil {
			first = err
		}
		m.file = 0
	}
	return first
}

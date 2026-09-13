package clock

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"math"
	"sort"
	"strconv"
	"strings"
)

type clockSequenceHead struct {
	LastAllocated, RetiredThrough uint64
	Retained                      []uint64
}

func ClockRequestID(namespace ControllerSessionID, sequence uint64) (string, error) {
	b, err := hex.DecodeString(string(namespace))
	if err != nil || len(b) != 32 || hex.EncodeToString(b) != string(namespace) || sequence == 0 {
		return "", errors.New("invalid clock namespace or sequence")
	}
	return "clock-" + string(namespace) + "-" + strconv.FormatUint(sequence, 10), nil
}
func (s SequenceState) NextRequestID() (string, error) {
	if s.RetiredThrough > s.LastAllocated || s.LastAllocated == math.MaxUint64 {
		return "", errors.New("invalid or exhausted clock sequence")
	}
	return ClockRequestID(s.Namespace, s.LastAllocated+1)
}
func parseClockRequestID(namespace ControllerSessionID, id string) (uint64, error) {
	prefix := "clock-" + string(namespace) + "-"
	if !strings.HasPrefix(id, prefix) {
		return 0, errors.New("clock request namespace mismatch")
	}
	n, err := strconv.ParseUint(strings.TrimPrefix(id, prefix), 10, 64)
	expected, e := ClockRequestID(namespace, n)
	if err != nil || e != nil || expected != id {
		return 0, errors.New("noncanonical clock request sequence")
	}
	return n, nil
}
func validClockSequence(h clockSequenceHead) error {
	if h.RetiredThrough > h.LastAllocated || h.Retained == nil || len(h.Retained) > 4096 {
		return errors.New("invalid clock sequence head")
	}
	var previous uint64
	for _, n := range h.Retained {
		if n == 0 || n <= previous || n > h.LastAllocated {
			return errors.New("invalid retained clock sequence")
		}
		previous = n
	}
	// Every allocated sequence beyond the retirement watermark must remain present.
	live := h.LastAllocated - h.RetiredThrough
	if live > uint64(len(h.Retained)) {
		return errors.New("missing live clock sequence")
	}
	for n := uint64(0); n < live; n++ {
		if h.Retained[len(h.Retained)-1-int(n)] != h.LastAllocated-n {
			return errors.New("missing live clock sequence")
		}
	}
	return nil
}
func InitializeSequence(ctx context.Context, tx *sql.Tx) error {
	if _, err := tx.ExecContext(ctx, "CREATE TABLE clock_sequence(singleton INTEGER PRIMARY KEY CHECK(singleton=1),payload BLOB NOT NULL) STRICT"); err != nil {
		return err
	}
	b, _ := json.Marshal(clockSequenceHead{Retained: []uint64{}})
	_, err := tx.ExecContext(ctx, "INSERT INTO clock_sequence VALUES(1,?)", b)
	return err
}
func CheckSequenceSchema(ctx context.Context, tx *sql.Tx) error {
	_, err := tx.ExecContext(ctx, "SELECT singleton,payload FROM clock_sequence LIMIT 0")
	return err
}
func saveClockSequence(ctx context.Context, tx *sql.Tx, h clockSequenceHead) error {
	if err := validClockSequence(h); err != nil {
		return err
	}
	b, err := json.Marshal(h)
	if err != nil {
		return err
	}
	result, err := tx.ExecContext(ctx, "UPDATE clock_sequence SET payload=? WHERE singleton=1", b)
	if err != nil {
		return err
	}
	n, err := result.RowsAffected()
	if err != nil {
		return err
	}
	if n != 1 {
		return errors.New("missing clock sequence metadata")
	}
	return nil
}
func loadClockSequence(ctx context.Context, tx *sql.Tx) (ControllerSessionID, clockSequenceHead, error) {
	var h clockSequenceHead
	ns, err := identity(ctx, tx)
	if err != nil {
		return ns, h, err
	}
	var data []byte
	if err = tx.QueryRowContext(ctx, "SELECT payload FROM clock_sequence WHERE singleton=1").Scan(&data); err != nil {
		return ns, h, err
	}
	if len(data) > 100000 {
		return ns, h, ErrCapacity
	}
	d := json.NewDecoder(bytes.NewReader(data))
	d.DisallowUnknownFields()
	if err = d.Decode(&h); err != nil {
		return ns, h, err
	}
	canonical, _ := json.Marshal(h)
	if !bytes.Equal(canonical, data) {
		return ns, h, errors.New("noncanonical clock sequence metadata")
	}
	if err = validClockSequence(h); err != nil {
		return ns, h, err
	}
	rows, err := tx.QueryContext(ctx, "SELECT request_id FROM clock_attempts")
	if err != nil {
		return ns, h, err
	}
	var actual []uint64
	for rows.Next() {
		var id string
		if err = rows.Scan(&id); err != nil {
			break
		}
		var n uint64
		n, err = parseClockRequestID(ns, id)
		if err != nil {
			break
		}
		actual = append(actual, n)
		if len(actual) > 4096 {
			err = ErrCapacity
			break
		}
	}
	err = errors.Join(err, rows.Err(), rows.Close())
	if err != nil {
		return ns, h, err
	}
	sort.Slice(actual, func(i, j int) bool { return actual[i] < actual[j] })
	if len(actual) != len(h.Retained) {
		return ns, h, errors.New("clock sequence catalog mismatch")
	}
	for i, n := range actual {
		if n != h.Retained[i] {
			return ns, h, errors.New("clock sequence catalog mismatch")
		}
	}
	return ns, h, nil
}
func ReadClockSequence(ctx context.Context, tx *sql.Tx) (SequenceState, error) {
	ns, h, err := loadClockSequence(ctx, tx)
	if err != nil {
		return SequenceState{}, err
	}
	return SequenceState{Namespace: ns, LastAllocated: h.LastAllocated, RetiredThrough: h.RetiredThrough}, nil
}

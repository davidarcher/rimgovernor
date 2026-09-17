package store

import (
	"context"
	"database/sql"
	"testing"
)

func openCachingDB(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open(DriverName, memoryURI())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	db.SetMaxOpenConns(1)
	db.SetMaxIdleConns(1)
	if _, err = db.Exec("CREATE TABLE t(n INTEGER)"); err != nil {
		t.Fatal(err)
	}
	return db
}

func TestCachingDriverReusesStatementsAcrossCalls(t *testing.T) {
	t.Parallel()
	db := openCachingDB(t)
	ctx := context.Background()
	const insert = "INSERT INTO t(n) VALUES (?)"
	for i := 1; i <= 3; i++ {
		if _, err := db.ExecContext(ctx, insert, i); err != nil {
			t.Fatal(err)
		}
	}
	var sum int
	for i := 0; i < 2; i++ {
		if err := db.QueryRowContext(ctx, "SELECT sum(n) FROM t").Scan(&sum); err != nil || sum != 6 {
			t.Fatal(sum, err)
		}
	}
	conn, err := db.Conn(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if err = conn.Raw(func(raw any) error {
		c := raw.(*cachingConn)
		c.mu.Lock()
		defer c.mu.Unlock()
		if len(c.stmts) != 3 {
			t.Errorf("cached %d statements, want 3", len(c.stmts))
		}
		for sql, s := range c.stmts {
			if s.busy {
				t.Errorf("%q still busy", sql)
			}
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
}

// A statement whose rows are still open must not be handed out again: the
// second user would reset the cursor under the first.
func TestCachingDriverNestedUseGetsOwnStatement(t *testing.T) {
	t.Parallel()
	db := openCachingDB(t)
	ctx := context.Background()
	for i := 1; i <= 3; i++ {
		if _, err := db.ExecContext(ctx, "INSERT INTO t(n) VALUES (?)", i); err != nil {
			t.Fatal(err)
		}
	}
	const query = "SELECT n FROM t ORDER BY n"
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	outer, err := tx.QueryContext(ctx, query)
	if err != nil {
		t.Fatal(err)
	}
	defer outer.Close()
	var seen []int
	for outer.Next() {
		var n, inner int
		if err = outer.Scan(&n); err != nil {
			t.Fatal(err)
		}
		if err = tx.QueryRowContext(ctx, query).Scan(&inner); err != nil || inner != 1 {
			t.Fatal(inner, err)
		}
		seen = append(seen, n)
	}
	if err = outer.Err(); err != nil || len(seen) != 3 {
		t.Fatal(seen, err)
	}
	if err = outer.Close(); err != nil {
		t.Fatal(err)
	}
	if err = tx.Commit(); err != nil {
		t.Fatal(err)
	}
	var count int
	if err = db.QueryRowContext(ctx, "SELECT count(*) FROM t").Scan(&count); err != nil || count != 3 {
		t.Fatal(count, err)
	}
}

func TestCachingDriverSurvivesSchemaChange(t *testing.T) {
	t.Parallel()
	db := openCachingDB(t)
	ctx := context.Background()
	const query = "SELECT * FROM t"
	if _, err := db.ExecContext(ctx, query); err != nil {
		t.Fatal(err)
	}
	if _, err := db.ExecContext(ctx, "ALTER TABLE t ADD COLUMN m INTEGER DEFAULT 7"); err != nil {
		t.Fatal(err)
	}
	if _, err := db.ExecContext(ctx, "INSERT INTO t(n) VALUES (1)"); err != nil {
		t.Fatal(err)
	}
	rows, err := db.QueryContext(ctx, query)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	cols, err := rows.Columns()
	if err != nil || len(cols) != 2 {
		t.Fatal(cols, err)
	}
	var n, m int
	if !rows.Next() {
		t.Fatal("no row")
	}
	if err = rows.Scan(&n, &m); err != nil || n != 1 || m != 7 {
		t.Fatal(n, m, err)
	}
}

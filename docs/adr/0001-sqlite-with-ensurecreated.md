# ADR 0001 — SQLite with `EnsureCreated`, no migrations

**Status**: accepted

## Context

Atlas is a single-service backend with no external infrastructure. The
priorities are a zero-setup developer experience and an integration test
suite that runs the real database engine, not a fake.

## Decision

Use **EF Core + SQLite** as the only persistence, with
`Database.EnsureCreated()` (Development and tests) instead of migrations.
The connection string defaults to a local file (`Data Source=atlas.db`);
integration tests bind each fixture to its own in-memory connection
(`DataSource=:memory:`) held open for the fixture's lifetime.

## Consequences

- `dotnet run` and `dotnet test` work with no installed services; resetting
  dev data is "delete `atlas.db`".
- Tests exercise real SQL (unique indexes, FKs, concurrency tokens) rather
  than the EF in-memory provider's approximations.
- No migration history: schema changes require recreating the database.
  Acceptable for this project; a production deployment would switch to
  migrations and likely PostgreSQL.
- SQLite's type limitations forced one explicit workaround —
  see [ADR 0004](0004-datetimeoffset-as-utc-ticks.md).

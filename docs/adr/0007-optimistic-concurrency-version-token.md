# ADR 0007 — Optimistic concurrency via an integer `Version` token

**Status**: accepted

## Context

Approval-style aggregates (leave requests, expense claims, amendments,
termination requests, contracts, payroll runs, benefit enrollments) can be
decided by two callers at once. Sequential double decisions are caught by
the state machine, but two writers who both *loaded* a pending entity could
otherwise both persist, silently overwriting each other. Pessimistic locks
don't fit a stateless HTTP API (and SQLite offers little to lock with).

## Decision

Entities whose updates can race implement `IVersioned` — a plain
`int Version` property. `AtlasDbContext` registers it as an EF **concurrency
token** for every implementing entity and bumps it automatically in
`SaveChanges` for each modified entity. A stale write (the row's version
changed since load) throws `DbUpdateConcurrencyException`, which a global
`ConcurrencyExceptionHandler` converts to **409** ProblemDetails
("reload and retry").

SQLite has no `rowversion` type, so an application-managed integer (rather
than a database-generated token) is the portable choice; centralizing the
bump in `SaveChanges` means no endpoint can forget it.

## Consequences

- Lost updates become impossible on versioned aggregates: the loser gets
  409, the winner's decision stands (asserted by `ConcurrencyEdgeTests` and
  `ProductionReadinessTests`).
- Two 409 flavors share a status code but differ in `detail`: domain-state
  conflicts name the winning state; token conflicts say the resource was
  modified. Clients handle both the same way — reload, re-evaluate, retry.
- The token costs one integer column and travels free in responses if ever
  needed for ETag-style preconditions later.

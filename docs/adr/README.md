# Architecture decision records

Short records of the load-bearing decisions in this codebase, reverse-engineered
from the code they shaped. Format: context → decision → consequences.

| # | Decision |
| --- | --- |
| [0001](0001-sqlite-with-ensurecreated.md) | SQLite with `EnsureCreated`, no migrations |
| [0002](0002-api-key-authentication.md) | API-key header auth with client-scoped roles |
| [0003](0003-cross-client-404-policy.md) | Cross-client access returns 404, insufficient role returns 403 |
| [0004](0004-datetimeoffset-as-utc-ticks.md) | `DateTimeOffset` stored as UTC ticks in SQLite |
| [0005](0005-decimal-money-rounding-policy.md) | Decimal money with a single round-once, away-from-zero policy |
| [0006](0006-append-only-salary-history.md) | Append-only salary history with effective-for-month selection |
| [0007](0007-optimistic-concurrency-version-token.md) | Optimistic concurrency via an integer `Version` token |
| [0008](0008-payroll-run-snapshot.md) | Payroll runs are immutable snapshots with one-way completion |
| [0009](0009-problemdetails-and-header-pagination.md) | RFC 7807 everywhere; pagination in headers, arrays in bodies |

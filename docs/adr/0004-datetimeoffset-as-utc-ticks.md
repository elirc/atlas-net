# ADR 0004 — `DateTimeOffset` stored as UTC ticks in SQLite

**Status**: accepted

## Context

SQLite has no native date-time type. EF Core's default mapping stores
`DateTimeOffset` as `TEXT`, and SQLite cannot order or compare those strings
as instants — `ORDER BY CreatedAtUtc` and range filters translate to wrong
SQL semantics or client-side evaluation.

## Decision

Register a model-wide value converter in `AtlasDbContext` that persists every
`DateTimeOffset` as its **UTC ticks** in an `INTEGER` column:

```csharp
value => value.UtcTicks
ticks => new DateTimeOffset(ticks, TimeSpan.Zero)
```

Pair it with a naming/typing convention: timestamps are `DateTimeOffset`
properties suffixed `AtUtc`; calendar dates (start/end/effective/expiry) are
`DateOnly` and never touch the converter.

## Consequences

- Ordering and comparisons execute correctly in SQL (integers), proven by
  `DateTimeOffsetConverterTests` (`OrderBy`/`Where` translation tests).
- The original offset is discarded: a value written at `+08:00` reads back
  at offset zero. It is the **same instant**, and since all Atlas timestamps
  are semantically UTC, no information the domain cares about is lost. A
  round-trip test documents this intentionally.
- Tick precision (100 ns) is preserved exactly — no string parsing, no
  sub-second truncation.
- Portable: if the database moves to PostgreSQL, dropping the converter
  restores native `timestamptz` behavior without touching the domain.

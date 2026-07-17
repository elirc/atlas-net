# ADR 0006 — Append-only salary history with effective-for-month selection

**Status**: accepted

## Context

Contract terms change over time (amendments), but payroll must pay the terms
that were in force for each period, and compensation history must be
auditable. Storing only the contract's current salary makes both impossible;
letting payroll read the amendment table couples two lifecycles.

## Decision

`SalaryRecord` is an **append-only** snapshot of terms from an effective
date: one `Initial` record written at contract creation, one `Amendment`
record written when an amendment is approved. Records are never updated or
deleted. Payroll selects the record **effective for the month**: the latest
`EffectiveDate` on or before the month's end, ties broken by `CreatedAtUtc`,
falling back to the contract's current salary when no record qualifies.
`FxRate` deliberately uses the *same* rule, so period selection behaves
identically for salaries and currency conversion.

## Consequences

- Full compensation history per contract, exposed read-only at
  `GET /api/contracts/{id}/salary-history`.
- Future-dated raises cannot leak into earlier months: approving an
  amendment updates the contract's current terms immediately, but payroll
  for prior periods keeps reading the older records.
- Simplification, accepted and documented: a change effective mid-month owns
  that **whole** month (no intra-month proration). The boundary cases —
  effective exactly on the month's first/last day — are pinned by tests.
- Two sources of truth (contract current terms vs. history) that must be
  written together; the amendment-approval endpoint is the single place
  that does both.

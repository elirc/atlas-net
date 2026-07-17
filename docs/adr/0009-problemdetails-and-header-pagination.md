# ADR 0009 — RFC 7807 everywhere; pagination in headers, arrays in bodies

**Status**: accepted

## Context

Consumers need machine-readable errors with stable shapes, and list
endpoints need bounding without breaking clients that already parse plain
JSON arrays.

## Decision

**Errors.** Every failure is an RFC 7807 ProblemDetails body:

- 400 validation problems carry per-field `errors`;
- domain-rule violations surface as 409 with the `DomainException` message
  as `detail` — endpoints catch it at the call site, and two global
  `IExceptionHandler`s (`DomainExceptionHandler`,
  `ConcurrencyExceptionHandler`) are the safety net for anything that
  escapes;
- `AddProblemDetails()` + `UseStatusCodePages()` give bare status codes
  (unknown routes, 401s) a problem body too.

The domain stays HTTP-free: it throws `DomainException` with a
human-readable message; the API layer owns the mapping to status codes.

**Pagination.** Flat lists accept `?page=` (1-based) and `?pageSize=`
(default 50, max 200; out-of-range → 400). Response bodies stay **plain JSON
arrays**; the metadata travels in `X-Total-Count`, `X-Page`, and
`X-Page-Size` headers. Bounded sub-resources (checklists, salary history,
balances, worker documents) are not paged.

## Consequences

- One error contract to parse; integration tests assert exact `detail`
  strings, which in turn pins the domain's messages as API surface.
- Existing consumers of the arrays kept working when pagination arrived;
  clients that ignore the headers simply get the first page of 50.
- Envelope-style responses (`{ items, total }`) were rejected to avoid a
  breaking change; the trade-off is that pagination is invisible to clients
  that never look at headers.

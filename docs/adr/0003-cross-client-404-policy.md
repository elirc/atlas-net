# ADR 0003 — Cross-client access returns 404, insufficient role returns 403

**Status**: accepted

## Context

Client-scoped API keys must never learn anything about other clients — not
even that a particular contract, invoice, or request id exists. At the same
time, a caller acting on its *own* data with a read-only key should get an
actionable error.

## Decision

Two distinct signals, applied uniformly by every endpoint:

- **404** whenever the resource belongs to another client (or doesn't
  exist) — for reads *and* writes. `CanViewClient` guards the load, and the
  not-found and not-yours branches are deliberately indistinguishable.
- **403** when the caller can see the resource but lacks the role to act:
  viewers attempting any write, client admins hitting platform-only
  endpoints, or a client admin writing to a *different* client's id passed
  in a request body.

List endpoints never 403 or 404 by ownership: they silently scope the query
to the caller's client, whatever filters are passed.

## Consequences

- Existence of other tenants' data is not revealed by status-code
  differences; enumeration of ids yields uniform 404s.
- Body-level references to foreign resources (e.g. submitting a leave
  request against another client's contract) return the same validation
  error as a nonexistent id ("does not exist") — again indistinguishable.
- Tests can and do assert the matrix explicitly
  (`AuthorizationMatrixTests`): platform-only × role → 403, own-data write
  as viewer → 403, cross-client anything → 404.

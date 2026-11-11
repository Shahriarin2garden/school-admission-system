# AI Usage

## Tools used

- **OpenCode CLI with an AI coding assistant** was used for this remediation:
  repository inspection, code changes, test authoring, documentation, Docker
  configuration and command execution.
- **GitHub Copilot in VS Code** assisted the earlier commits in this repository
  (API scaffold, initial React UI and first tests), as recorded in the previous
  version of this file.
- Supporting command-line tools used to verify the result: Git, Docker and
  Docker Compose, Node.js/npm, curl, and `psql` inside the PostgreSQL container.

No result in this document is fabricated. Items that were only reviewed
statically are identified as such.

## Areas that received assistance

- Restructuring the minimal API into endpoints, repository, middleware, schema
  bootstrap and background worker.
- Concurrency-safe state transitions using PostgreSQL advisory locks and
  affected-row checks.
- Distributed-safe background expiration.
- Versioned schema migration with a session-level initialization lock.
- Input validation, JSON error envelopes, health endpoint and privacy fixes.
- Two-instance Docker Compose topology and Docker-based test runners.
- React identity switching, request-generation guards, countdown and error
  handling.
- xUnit integration/concurrency tests, React Testing Library tests, CI workflow
  and documentation.

## Mistakes detected during review and how they were corrected

1. **Confirmation could report `CONFIRMED` without changing the row.** The old
   handler ran an update and ignored the affected-row count. It now re-reads the
   hold under the class lock, checks expiration, performs a conditional update,
   verifies exactly one row changed, and returns the actual resulting state.
2. **Cancellation ran outside a transaction and could overwrite `EXPIRED`.**
   Cancellation now uses the same class lock, expires overdue holds first, and
   only converts an unexpired `HELD` row to `CANCELLED`. `EXPIRED` and
   `CONFIRMED` return `409`.
3. **Expiration used the API host clock and updated every class.** All
   expiration decisions now use PostgreSQL `clock_timestamp()` and are scoped
   to one locked class.
4. **Expiration depended on incoming traffic.** A `BackgroundService` now
   sweeps overdue holds automatically in every instance.
5. **Seat counts counted overdue holds.** The count query now only counts
   `HELD` rows whose expiration is in the future.
6. **Schema initialization could race between instances.** Migrations now run
   behind a PostgreSQL session advisory lock, and a second migration upgrades
   legacy tables that lacked `updated_at`.
7. **The public hold-status response exposed the student registration number.**
   `GET /holds/{holdId}` no longer returns `studentId`.
8. **100 concurrent cross-instance requests produced HTTP 500 responses.** The
   cause was PostgreSQL connection exhaustion: each backend could pool up to
   100 connections. Pools are now capped at 20 per process and the Compose
   database allows 200 connections.
9. **Concurrency tests disposed HTTP requests before sending completed.** The
   test lambdas now await the send before the `using` scope ends.
10. **The frontend kept the previous student's applications after an identity
    change.** The UI now clears them immediately, aborts stale requests and uses
    a generation counter to reject late responses.
11. **Frontend tests treated a leading-space header as invalid.** HTTP trims
    optional whitespace around header values, so that case was not a valid
    negative test; it was replaced with an ASCII-invalid value.

## Verification actually performed

Executed locally during this remediation:

- `docker compose build` — all API and frontend images built successfully.
- `docker compose up -d` — PostgreSQL, two API instances and the frontend
  started; both `/health` endpoints returned `{"status":"healthy"}`.
- `docker compose --profile test run --build --rm backend-tests` — **28 tests
  passed, 0 failed**, including the 100-request cross-instance concurrency test.
- `docker compose --profile test run --build --rm frontend-tests` — **16 tests
  passed, 0 failed**.
- `npm test` and `npm run build` on the host — 16 tests passed and the
  production bundle built.
- `docker compose config` — configuration validated.
- A manual 15-check business workflow (two classes, capacity limit, independent
  day class, confirmation, repeated confirmation, ownership rejection,
  cancellation, confirmed-cancellation rejection) — 15/15 passed.
- A three-run cross-instance probe with 100 concurrent applications — each run
  produced exactly 5 × `201`, 95 × `409`, and zero `5xx`.
- Forced-expiration check — the background worker changed the row to `EXPIRED`,
  released the seat, and confirmation returned `409`.
- Persistence check — records survived restarting both backend containers and
  survived `docker compose down` followed by `docker compose up -d` without
  `-v`.

Environment limitation: on the verification host, port `8080` was already used
by an unrelated Jenkins process. The committed Compose file keeps the required
`8080` mapping; verification used a temporary, uncommitted Compose override
that published backend 1 on host port `18080`. The Docker test suite itself
uses Compose-internal DNS (`backend1:8080`, `backend2:8080`), so it exercised
the real two-instance topology without relying on the host port.

## Manual checks

- Reviewed endpoint status codes and JSON error shapes against the assignment.
- Reviewed SQL for parameterization and transaction scope.
- Checked that student identifiers and other personal data are absent from logs
  and public hold responses.
- Checked that the frontend clears stale identity data and disables actions on
  expiration.
- Checked that both backends run the expiration worker and share one database.

## Not independently verified

- The GitHub Actions workflow was authored and its commands were exercised
  locally, but it has not been observed running on GitHub's hosted runners in
  this session.
- A real upgrade from a database volume created by the original pre-remediation
  code was not reproduced from a fresh old-version checkout; the upgrade
  migration was reviewed statically and runs against the current schema.

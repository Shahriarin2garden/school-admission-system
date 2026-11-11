# School Admission System

A first-come, first-served (FCFS) school admission portal. Students can create
admission applications, reserve a seat for a limited time, pay a simulated
admission fee (confirm), or cancel. Seat capacity is enforced by PostgreSQL, so
the system stays correct under concurrent requests and when multiple API
instances run at the same time.

The full stack starts with one command and does not require .NET, Node.js or
PostgreSQL on the host.

## Architecture

```text
                       Browser
                          |
                          | http://localhost:3001
                          v
                +-------------------+
                |  React 18 + Vite  |
                |  served by Nginx  |
                +---------+---------+
                          |
                          | VITE_API_URL (default http://localhost:8080)
                          v
          +---------------+----------------+
          |                                |
   +------+-------+                 +------+-------+
   |  backend1    |                 |  backend2    |
   |  .NET 8 API  |<-- same image ->|  .NET 8 API  |
   |  port 8080   |                 |  port 8081   |
   +------+-------+                 +------+-------+
          |                                |
          +---------------+----------------+
                          |
                          v
                +-------------------+
                |  PostgreSQL 16    |
                |  pgdata volume    |
                +-------------------+
```

Both API instances run the same image, use the same database and coordinate
through PostgreSQL transaction-scoped advisory locks. A background expiration
worker runs in each instance; the same class-level locks make it safe for both
workers to run simultaneously.

## Technology stack

| Layer | Technology |
|---|---|
| API | .NET 8 Minimal API, Npgsql |
| Database | PostgreSQL 16 |
| Frontend | React 18, Vite 5 |
| Styling | Plain responsive CSS |
| Web server | Nginx |
| Containers | Docker, Docker Compose |
| Backend tests | xUnit, Npgsql |
| Frontend tests | Vitest, React Testing Library, user-event, jsdom |
| CI | GitHub Actions |

## Repository structure

```text
backend/
  Program.cs                     API configuration and startup
  Api/ApiEndpoints.cs            HTTP endpoints and validation
  Api/ErrorHandlingMiddleware.cs Consistent JSON error responses
  Data/AdmissionRepository.cs    Transactional PostgreSQL operations
  Data/Models.cs                 API/repository models
  Infrastructure/DatabaseBootstrapper.cs Versioned, lock-protected migrations
  Workers/HoldExpirationWorker.cs Background expiration sweeps
  Dockerfile
backend.tests/
  AdmissionApiTests.cs           Integration and concurrency tests
  Dockerfile
frontend/
  src/App.jsx                    Admission portal UI
  src/appLogic.js                Validation, countdown and expiry helpers
  src/App.test.jsx               React workflow tests
  src/appLogic.test.js           Pure helper tests
  Dockerfile                     Production image
  Dockerfile.test                Test-runner image
docker-compose.yml
.github/workflows/ci.yml
```

## Prerequisites

- Docker Engine with Docker Compose v2+
- Nothing else. The stack builds .NET and Node dependencies inside containers.

## Start the complete application

```bash
docker compose up --build
```

Or, to run in the background:

```bash
docker compose up --build -d
```

| Service | URL |
|---|---|
| Frontend | http://localhost:3001 |
| Backend instance 1 | http://localhost:8080 |
| Backend instance 2 | http://localhost:8081 |
| Health check | http://localhost:8080/health and http://localhost:8081/health |

Copy `.env.example` to `.env` to override defaults. The ports used by the API
are published as `8080` and `8081`; the containers listen on `8080` internally.

## API reference

Base URL: `http://localhost:8080` (or `http://localhost:8081`)

| Method | Endpoint | Notes |
|---|---|---|
| `POST` | `/classes` | Create an admission class. Body: `{ "name": "...", "totalSeats": 30 }`. Returns `201`. |
| `GET` | `/classes` | List classes with total/held/confirmed/available seat counts. |
| `GET` | `/classes/{classId}` | One class with live counts. `404` if missing. |
| `POST` | `/classes/{classId}/holds` | Apply. Requires `X-Student-Id`. Returns `201` and the hold. |
| `GET` | `/holds/{holdId}` | Public hold status. No student header required. |
| `POST` | `/holds/{holdId}/confirm` | Simulated payment. Requires `X-Student-Id`. Idempotent. |
| `DELETE` | `/holds/{holdId}` | Cancel an unexpired HELD application. Requires `X-Student-Id`. |
| `GET` | `/me/holds` | The current student's applications. Requires `X-Student-Id`. |
| `GET` | `/health` | `200` when the API can reach PostgreSQL, `503` otherwise. |

Application status values: `HELD`, `CONFIRMED`, `EXPIRED`, `CANCELLED`.

Example:

```bash
curl -X POST http://localhost:8080/classes \
  -H 'Content-Type: application/json' \
  -d '{"name":"Class 6 Morning","totalSeats":3}'

curl -X POST http://localhost:8080/classes/<classId>/holds \
  -H 'X-Student-Id: 20152691234567890'
```

Errors always return JSON in the form `{ "error": "..." }` (state-transition
conflicts also include the current `status`). Status codes: `400` invalid
input, `403` another student's application, `404` missing resource, `409`
capacity/duplicate/invalid state transition, `500` only for unexpected
failures.

## Student identity

Every student-specific request must send:

```text
X-Student-Id: 20152691234567890
```

The value must be exactly 17 ASCII digits. The frontend stores the selected
number in `localStorage`, shows a Clear button, and immediately removes the
previous student's applications when the number changes.

Important: `X-Student-Id` is an identifier supplied by the client, not
authentication. It prevents accidental cross-student operations in the UI and
in the API's ownership checks, but it cannot prove who the caller is. This is a
documented limitation of the assignment (no login system is specified).

## Reservation and expiration

- A new application becomes `HELD` for `HOLD_DURATION_SECONDS` (default `600`).
- A hold is valid while `clock_timestamp() < expires_at` and expires at or after
  the boundary (`clock_timestamp() >= expires_at`).
- A background worker in every API instance sweeps overdue holds every
  `EXPIRATION_SWEEP_SECONDS` (default `2`). It processes whole classes under the
  class advisory lock and transitions only overdue `HELD` rows to `EXPIRED`.
- Relevant operations also expire overdue holds lazily inside their transaction,
  so seat availability is correct even between sweeps.
- Class seat-count queries never count an overdue `HELD` row as held.
- An expired or cancelled application no longer occupies a seat, and the same
  student may apply again.
- Expired applications cannot be confirmed or cancelled.

The browser countdown is presentation only. The API always makes the
authoritative decision using PostgreSQL time.

## Seat allocation and concurrency

Every operation that can change occupied seats for a class (`hold creation`,
`confirmation`, `cancellation`, `expiration`) runs in a PostgreSQL transaction
and takes:

```sql
select pg_advisory_xact_lock(hashtextextended(@classId, 0));
```

Within the lock the service expires overdue holds, checks for an existing
active application, counts `HELD` + `CONFIRMED`, and only then inserts or
updates. A partial unique index adds a final structural guarantee:

```sql
create unique index ux_active_student_class
  on holds(class_id, student_id)
  where status in ('HELD', 'CONFIRMED');
```

There are no process-local locks and no application-side seat counters. Two API
instances sharing PostgreSQL therefore cannot overbook a class, and the
database is always the source of truth.

## PostgreSQL persistence

Data lives in the named Docker volume `pgdata`. It survives backend restarts
and `docker compose down` / `docker compose up` as long as the volume is not
deleted. Schema changes are applied by versioned migrations in
`DatabaseBootstrapper.cs` behind a session-level advisory lock, so two instances
can start at the same time without racing on DDL.

Verify persistence manually:

```bash
# 1. Create a class and remember its id.
curl -X POST http://localhost:8080/classes \
  -H 'Content-Type: application/json' \
  -d '{"name":"Persistence Check","totalSeats":2}'

# 2. Restart only the API containers.
docker compose restart backend1 backend2

# 3. Read the class again; it must still exist.
curl http://localhost:8080/classes/<classId>

# 4. Restart the whole stack without deleting the volume.
docker compose down
docker compose up -d

# 5. The class and its applications must still exist.
curl http://localhost:8080/classes/<classId>
```

Do **not** use `docker compose down -v` unless you intend to erase all data.

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `POSTGRES_USER` | `school` | Database user |
| `POSTGRES_PASSWORD` | `school` | Database password |
| `POSTGRES_DB` | `school_admission` | Database name |
| `HOLD_DURATION_SECONDS` | `600` | Reservation lifetime |
| `EXPIRATION_SWEEP_SECONDS` | `2` | Background expiration interval |
| `VITE_API_URL` | `http://localhost:8080` | API base URL compiled into the frontend |
| `DATABASE_URL` | derived in Compose | Npgsql connection string |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` | API listen address inside the container |

The frontend API URL is a build-time Vite variable. It is passed as a Docker
build argument in `docker-compose.yml` and can be changed in `.env` before
building.

## Tests

### Backend tests (Docker only)

The test container starts against the running Compose stack and reaches both
API instances through Docker DNS:

```bash
docker compose --profile test run --build --rm backend-tests
```

The suite covers class creation and validation, missing resources, hold
creation, capacity, duplicates, independent classes, ownership, confirmation
idempotency, expiration, cancellation rules, competing confirm/cancel requests,
durability, health, single-instance concurrency and cross-instance concurrency.

If the local `.env` changes `POSTGRES_DB`/`POSTGRES_USER`/`POSTGRES_PASSWORD`,
the same values are passed to the test container automatically.

### Frontend tests (Docker only)

```bash
docker compose --profile test run --build --rm frontend-tests
```

### Local test commands

With a local .NET 8 SDK and two API instances running (ports 8080 and 8081):

```bash
dotnet test backend.tests
```

With a local Node.js 20 installation:

```bash
cd frontend
npm ci
npm test
npm run build
```

The backend tests read `API_URL`, `API_URL_2` and `DATABASE_URL` from the
environment and default to localhost values.

## Cross-instance concurrency test

`ConcurrentHolds_NeverExceedCapacityAcrossTwoInstances` creates a five-seat
class and sends 100 simultaneous applications, splitting them between
`API_URL` and `API_URL_2`. It asserts:

- exactly 5 responses are `201`;
- exactly 95 responses are `409`;
- no response is `5xx`;
- PostgreSQL contains exactly 5 `HELD` rows;
- no student/class pair has duplicate active applications;
- available seats are 0 on both instances.

Run it through the backend test command above. The same invariant can be
checked manually with any HTTP client by alternating requests between
`localhost:8080` and `localhost:8081`.

## Continuous integration

`.github/workflows/ci.yml` runs on pushes to `main` and on pull requests:

- restore and build the .NET API and test project;
- start two API processes against a PostgreSQL service;
- run the full backend integration suite;
- install frontend dependencies with `npm ci`, run Vitest and build the bundle;
- validate the Compose file and build every Docker image.

## Troubleshooting

**`bind: address already in use` on port 8080 or 8081** — another local process
(for example Jenkins) is using the port. Stop it or publish the API on a
different host port in `docker-compose.yml`.

**Backend is not healthy** — inspect logs with
`docker compose logs backend1` and confirm the database is healthy with
`docker compose ps db`. The API waits for PostgreSQL and retries startup.

**Frontend shows a network error** — confirm `VITE_API_URL` points at a reachable
API, then rebuild the frontend image so the new value is compiled into the
bundle: `docker compose build frontend`.

**Seats look stale** — counts refresh every three seconds. An overdue hold is
excluded from counts immediately and is stored as `EXPIRED` by the background
worker within a few seconds.

**Reset all data (destructive)** — `docker compose down -v` deletes the
PostgreSQL volume and every admission record.

## Known limitations and security assumptions

- `X-Student-Id` is identification, not authentication. Anyone who knows a
  registration number can act as that student. A production system would need
  real authentication.
- The administration "create class" form has no authorization because the
  assignment specifies no authentication.
- Payment is simulated by the confirmation endpoint; no payment provider is
  involved.
- The frontend countdown can drift with the browser clock, but it is never
  authoritative; all confirmation/cancellation decisions use PostgreSQL time.
- CORS is open to all origins to keep the assignment self-contained.
- There is no rate limiting or audit trail for administrative actions.
- The system is designed for a single PostgreSQL database; it does not attempt
  multi-region replication.

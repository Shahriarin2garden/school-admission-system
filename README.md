# School Admission System

FCFS admission system with concurrency-safe PostgreSQL seat holds, .NET 8 minimal API, and React/Vite UI.

## Run
Install Docker only, then from the repository root:

```bash
docker compose up --build
```

Open http://localhost:3001. The API is at http://localhost:8080. Data is persisted in the `pgdata` Docker volume. Copy `.env.example` to `.env` to configure the database or `HOLD_DURATION_SECONDS` (default 600).

## API and tests
The API implements `POST/GET /classes`, `POST /classes/{id}/holds`, `GET /holds/{id}`, `POST /holds/{id}/confirm`, `DELETE /holds/{id}`, and `GET /me/holds`. Student operations require a 17-digit `X-Student-Id` header.

With .NET 8 installed and the API/database running, execute:

```bash
dotnet test backend.tests
```

The concurrency test sends 20 simultaneous applications against a three-seat class and verifies exactly three succeed.

The frontend checks can be run independently:

```bash
cd frontend
npm test
npm run build
```

The backend tests require a reachable API and PostgreSQL instance. The complete stack can be started with Docker Compose before running them:

```bash
docker compose up --build -d
dotnet test backend.tests
```

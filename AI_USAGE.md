# AI Usage

AI assistance was used through the Copilot SDK in VS Code to scaffold and review the .NET minimal API, PostgreSQL schema and transactions, React/Vite UI, Docker files, tests, and documentation. The tools used for this work were repository file inspection/editing, PowerShell, the .NET CLI, npm/Vitest, Git, and Docker Compose where available.

The review initially identified several gaps: the wrong-student confirmation path used `Results.Forbid()` without authentication middleware, cancellation did not distinguish an unknown hold from a non-cancellable hold, the applications view omitted class IDs and absolute expiry times, and the UI hid expired actions instead of disabling them. These were fixed with an explicit 403 JSON response, a 404 check for unknown holds, richer application details, and disabled expired actions. Network failures and invalid student IDs were also given user-facing messages.

The frontend was validated with `npm test` and `npm run build`. Backend validation uses `dotnet test backend.tests` with the API and PostgreSQL running; Docker Compose remains the intended full-stack validation path.

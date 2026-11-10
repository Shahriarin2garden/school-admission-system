# AI Usage

AI assistance was used to scaffold the .NET minimal API, PostgreSQL schema/transactions, React UI, Docker files, tests, and documentation. The implementation was reviewed against the assignment contract; in particular, PostgreSQL advisory locks and the partial unique index were retained so concurrency safety does not depend on process-local memory. Validation is performed with Docker Compose and `dotnet test backend.tests`.

# Assumptions

- Class names are free-form (maximum 200 characters); each class record is already a class-and-shift category.
- UUID-derived string IDs (`c_...`, `h_...`) are sufficient and avoid exposing sequential database keys.
- Expired holds are lazily transitioned on every relevant request. This is automatic from the user's perspective and avoids a fragile scheduler; the indexed expiry query keeps it inexpensive.
- PostgreSQL transaction-scoped advisory locks serialize operations per class across all backend processes. A partial unique index enforces one active application per student/class.
- Any user can access the administration create-class form because the assignment specifies no authentication system.
- Cancellation is allowed only for a student's own HELD application; confirmed admissions are immutable.

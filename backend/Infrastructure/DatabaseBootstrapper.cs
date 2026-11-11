using Npgsql;

namespace SchoolAdmission.Api.Infrastructure;

/// <summary>
/// Applies versioned schema migrations behind a PostgreSQL session-level
/// advisory lock. Multiple API instances can therefore start concurrently
/// without racing on DDL.
/// </summary>
public static class DatabaseBootstrapper
{
    private const string SchemaLockKey = "school_admission_schema_v1";

    private static readonly Migration[] Migrations =
    [
        new(1, "initial admission schema", """
            create table if not exists admission_classes (
                id text primary key,
                name text not null,
                total_seats integer not null,
                created_at timestamptz not null default clock_timestamp()
            );

            create table if not exists holds (
                id text primary key,
                class_id text not null references admission_classes(id),
                student_id text not null,
                status text not null,
                expires_at timestamptz not null,
                created_at timestamptz not null default clock_timestamp(),
                updated_at timestamptz not null default clock_timestamp()
            );

            do $$
            begin
                if not exists (select 1 from pg_constraint where conname = 'ck_classes_name_valid') then
                    alter table admission_classes add constraint ck_classes_name_valid
                        check (length(btrim(name)) > 0 and length(name) <= 200);
                end if;
                if not exists (select 1 from pg_constraint where conname = 'ck_classes_seats_positive') then
                    alter table admission_classes add constraint ck_classes_seats_positive
                        check (total_seats > 0);
                end if;
                if not exists (select 1 from pg_constraint where conname = 'ck_holds_status_valid') then
                    alter table holds add constraint ck_holds_status_valid
                        check (status in ('HELD', 'CONFIRMED', 'EXPIRED', 'CANCELLED'));
                end if;
                if not exists (select 1 from pg_constraint where conname = 'ck_holds_student_id_valid') then
                    alter table holds add constraint ck_holds_student_id_valid
                        check (student_id ~ '^[0-9]{17}$');
                end if;
                if not exists (select 1 from pg_constraint where conname = 'ck_holds_expiry_valid') then
                    alter table holds add constraint ck_holds_expiry_valid
                        check (expires_at is not null);
                end if;
            end $$;

            drop index if exists one_active;
            create unique index if not exists ux_active_student_class
                on holds(class_id, student_id)
                where status in ('HELD', 'CONFIRMED');
            create index if not exists ix_holds_expiry
                on holds(expires_at)
                where status = 'HELD';
            create index if not exists ix_holds_class_status on holds(class_id, status);
            create index if not exists ix_holds_student on holds(student_id);
            """),
        new(2, "add audit columns to legacy schemas", """
            do $$
            begin
                if not exists (
                    select 1 from information_schema.columns
                    where table_schema = 'public' and table_name = 'holds' and column_name = 'updated_at') then
                    alter table holds add column updated_at timestamptz not null default clock_timestamp();
                end if;
                if not exists (
                    select 1 from information_schema.columns
                    where table_schema = 'public' and table_name = 'holds' and column_name = 'created_at') then
                    alter table holds add column created_at timestamptz not null default clock_timestamp();
                end if;
                if not exists (
                    select 1 from information_schema.columns
                    where table_schema = 'public' and table_name = 'admission_classes' and column_name = 'created_at') then
                    alter table admission_classes add column created_at timestamptz not null default clock_timestamp();
                end if;
            end $$;
            """)
    ];

    public static async Task InitializeAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var db = new NpgsqlConnection(connectionString);
        await OpenWithRetryAsync(db, logger, cancellationToken);

        await using var lockCommand = new NpgsqlCommand(
            "select pg_advisory_lock(hashtextextended(@key, 0))", db);
        lockCommand.Parameters.AddWithValue("key", SchemaLockKey);
        await lockCommand.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await ExecuteAsync(
                db,
                """
                create table if not exists schema_migrations (
                    version integer primary key,
                    description text not null,
                    applied_at timestamptz not null default clock_timestamp()
                )
                """,
                null,
                cancellationToken);

            foreach (var migration in Migrations)
            {
                if (await IsAppliedAsync(db, migration.Version, cancellationToken))
                {
                    continue;
                }

                await using var tx = await db.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(db, migration.Sql, tx, cancellationToken);
                await using var record = new NpgsqlCommand(
                    "insert into schema_migrations(version, description) values(@version, @description)",
                    db,
                    tx);
                record.Parameters.AddWithValue("version", migration.Version);
                record.Parameters.AddWithValue("description", migration.Description);
                await record.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                logger.LogInformation(
                    "Applied database migration {Version}: {Description}",
                    migration.Version,
                    migration.Description);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand(
                "select pg_advisory_unlock(hashtextextended(@key, 0))", db);
            unlock.Parameters.AddWithValue("key", SchemaLockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task OpenWithRetryAsync(
        NpgsqlConnection db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        const int attempts = 30;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await db.OpenAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < attempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Database not ready (attempt {Attempt}/{Attempts}): {Message}",
                    attempt,
                    attempts,
                    ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException("The database could not be reached during startup.");
    }

    private static async Task<bool> IsAppliedAsync(
        NpgsqlConnection db,
        int version,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "select exists(select 1 from schema_migrations where version = @version)", db);
        cmd.Parameters.AddWithValue("version", version);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection db,
        string sql,
        NpgsqlTransaction? tx,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Migration(int Version, string Description, string Sql);
}

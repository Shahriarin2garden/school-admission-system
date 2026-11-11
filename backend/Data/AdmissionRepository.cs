using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace SchoolAdmission.Api.Data;

/// <summary>
/// All admission state lives in PostgreSQL. Every operation that can change the
/// number of occupied seats for a class runs inside a transaction and takes the
/// same transaction-scoped PostgreSQL advisory lock for that class.
/// </summary>
public sealed class AdmissionRepository
{
    private readonly string _connectionString;
    private readonly int _holdDurationSeconds;

    public AdmissionRepository(string connectionString, int holdDurationSeconds)
    {
        _connectionString = connectionString;
        _holdDurationSeconds = holdDurationSeconds;
    }

    public async Task<CreatedClass> CreateClassAsync(string name, int totalSeats, CancellationToken cancellationToken)
    {
        var id = "c_" + Guid.NewGuid().ToString("N");
        await using var db = await OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "insert into admission_classes(id, name, total_seats) values(@id, @name, @seats)", db);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("seats", totalSeats);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new CreatedClass(id, name, totalSeats);
    }

    public async Task<IReadOnlyList<ClassSummary>> GetClassesAsync(string? classId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        const string sql = """
            with snapshot as (select clock_timestamp() as now)
            select c.id,
                   c.name,
                   c.total_seats,
                   count(h.id) filter (where h.status = 'HELD' and h.expires_at > snapshot.now)::int as held,
                   count(h.id) filter (where h.status = 'CONFIRMED')::int as confirmed
            from admission_classes c
            cross join snapshot
            left join holds h on h.class_id = c.id
            where (@id is null or c.id = @id)
            group by c.id, c.name, c.total_seats
            order by lower(c.name), c.id
            """;

        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = (object?)classId ?? DBNull.Value });

        var results = new List<ClassSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var total = reader.GetInt32(2);
            var held = reader.GetInt32(3);
            var confirmed = reader.GetInt32(4);
            results.Add(new ClassSummary(
                reader.GetString(0),
                reader.GetString(1),
                total,
                held,
                confirmed,
                Math.Max(0, total - held - confirmed)));
        }

        return results;
    }

    public async Task<CreateHoldResult> CreateHoldAsync(string classId, string studentId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await AcquireClassLockAsync(db, tx, classId, cancellationToken);

        if (!await ClassExistsAsync(db, tx, classId, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return new CreateHoldResult(CreateHoldOutcome.ClassNotFound, null);
        }

        // Expiration happens while the class lock is held so seats are freed
        // deterministically before capacity is evaluated.
        await ExpireClassAsync(db, tx, classId, cancellationToken);

        if (await HasActiveApplicationAsync(db, tx, classId, studentId, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return new CreateHoldResult(CreateHoldOutcome.Duplicate, null);
        }

        var counts = await GetSeatCountsAsync(db, tx, classId, cancellationToken);
        if (counts.Held + counts.Confirmed >= counts.Total)
        {
            await tx.RollbackAsync(cancellationToken);
            return new CreateHoldResult(CreateHoldOutcome.Full, null);
        }

        var holdId = "h_" + Guid.NewGuid().ToString("N");
        const string insert = """
            insert into holds(id, class_id, student_id, status, expires_at)
            values (@id, @class, @student, 'HELD',
                    clock_timestamp() + make_interval(secs => @seconds::double precision))
            returning expires_at
            """;

        try
        {
            await using var cmd = new NpgsqlCommand(insert, db, tx);
            cmd.Parameters.AddWithValue("id", holdId);
            cmd.Parameters.AddWithValue("class", classId);
            cmd.Parameters.AddWithValue("student", studentId);
            cmd.Parameters.AddWithValue("seconds", _holdDurationSeconds);
            var expiresAt = (DateTime)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            await tx.CommitAsync(cancellationToken);
            return new CreateHoldResult(
                CreateHoldOutcome.Created,
                new CreatedHold(holdId, classId, studentId, "HELD", expiresAt));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // The partial unique index is the last line of defence against
            // duplicate active applications for the same student and class.
            await tx.RollbackAsync(cancellationToken);
            return new CreateHoldResult(CreateHoldOutcome.Duplicate, null);
        }
    }

    public async Task<HoldSummary?> GetHoldAsync(string holdId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        const string sql = """
            select h.id,
                   h.class_id,
                   c.name,
                   case when h.status = 'HELD' and h.expires_at <= clock_timestamp()
                        then 'EXPIRED' else h.status end,
                   h.expires_at
            from holds h
            join admission_classes c on c.id = h.class_id
            where h.id = @id
            """;

        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", holdId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new HoldSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTime>(4));
    }

    public async Task<IReadOnlyList<HoldSummary>> GetStudentHoldsAsync(string studentId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        const string sql = """
            select h.id,
                   h.class_id,
                   c.name,
                   case when h.status = 'HELD' and h.expires_at <= clock_timestamp()
                        then 'EXPIRED' else h.status end,
                   h.expires_at
            from holds h
            join admission_classes c on c.id = h.class_id
            where h.student_id = @student
            order by h.created_at desc, h.id
            """;

        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("student", studentId);

        var results = new List<HoldSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HoldSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTime>(4)));
        }

        return results;
    }

    public async Task<TransitionResult> ConfirmAsync(string holdId, string studentId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var initial = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        if (initial is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        await AcquireClassLockAsync(db, tx, initial.Value.ClassId, cancellationToken);
        var hold = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        if (hold is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        if (!string.Equals(hold.Value.StudentId, studentId, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Forbidden, null);
        }

        if (hold.Value.Status == "CONFIRMED")
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Success, "CONFIRMED");
        }

        await ExpireClassAsync(db, tx, hold.Value.ClassId, cancellationToken);
        hold = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        if (hold is null)
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        if (hold.Value.Status == "CONFIRMED")
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Success, "CONFIRMED");
        }

        if (hold.Value.Status != "HELD")
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Conflict, hold.Value.Status);
        }

        await using var cmd = new NpgsqlCommand("""
            update holds
            set status = 'CONFIRMED', updated_at = clock_timestamp()
            where id = @id and status = 'HELD' and expires_at > clock_timestamp()
            """, db, tx);
        cmd.Parameters.AddWithValue("id", holdId);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Success, "CONFIRMED");
        }

        // The row was HELD moments ago but crossed the expiration boundary
        // before the conditional update. Persist the expiration and report the
        // authoritative terminal state rather than claiming success.
        await ExpireClassAsync(db, tx, hold.Value.ClassId, cancellationToken);
        var finalState = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new TransitionResult(TransitionOutcome.Conflict, finalState?.Status);
    }

    public async Task<TransitionResult> CancelAsync(string holdId, string studentId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var initial = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        if (initial is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        await AcquireClassLockAsync(db, tx, initial.Value.ClassId, cancellationToken);
        var hold = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        if (hold is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        if (!string.Equals(hold.Value.StudentId, studentId, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Forbidden, null);
        }

        if (hold.Value.Status is "CONFIRMED" or "EXPIRED" or "CANCELLED")
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Conflict, hold.Value.Status);
        }

        // Expiration is processed before cancellation so an overdue HELD row
        // can never be rewritten as CANCELLED.
        await ExpireClassAsync(db, tx, hold.Value.ClassId, cancellationToken);
        hold = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        if (hold is null)
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.NotFound, null);
        }

        if (hold.Value.Status != "HELD")
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Conflict, hold.Value.Status);
        }

        await using var cmd = new NpgsqlCommand("""
            update holds
            set status = 'CANCELLED', updated_at = clock_timestamp()
            where id = @id and status = 'HELD' and expires_at > clock_timestamp()
            """, db, tx);
        cmd.Parameters.AddWithValue("id", holdId);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return new TransitionResult(TransitionOutcome.Success, "CANCELLED");
        }

        await ExpireClassAsync(db, tx, hold.Value.ClassId, cancellationToken);
        var finalState = await GetHoldStateAsync(db, tx, holdId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new TransitionResult(TransitionOutcome.Conflict, finalState?.Status);
    }

    public async Task<IReadOnlyList<string>> FindClassesWithExpiredHoldsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        const string sql = """
            select class_id
            from holds
            where status = 'HELD' and expires_at <= clock_timestamp()
            group by class_id
            order by min(expires_at)
            limit @limit
            """;

        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("limit", limit);

        var classIds = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            classIds.Add(reader.GetString(0));
        }

        return classIds;
    }

    public async Task<int> ExpireOverdueForClassAsync(string classId, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AcquireClassLockAsync(db, tx, classId, cancellationToken);
        var affected = await ExpireClassAsync(db, tx, classId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return affected;
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("select 1", db);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var db = new NpgsqlConnection(_connectionString);
        try
        {
            await db.OpenAsync(cancellationToken);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static async Task AcquireClassLockAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string classId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "select pg_advisory_xact_lock(hashtextextended(@id, 0))", db, tx);
        cmd.Parameters.AddWithValue("id", classId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ClassExistsAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string classId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "select exists(select 1 from admission_classes where id = @id)", db, tx);
        cmd.Parameters.AddWithValue("id", classId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> HasActiveApplicationAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string classId,
        string studentId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            select exists(
                select 1 from holds
                where class_id = @class and student_id = @student
                  and status in ('HELD', 'CONFIRMED'))
            """, db, tx);
        cmd.Parameters.AddWithValue("class", classId);
        cmd.Parameters.AddWithValue("student", studentId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(int Total, int Held, int Confirmed)> GetSeatCountsAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string classId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            select c.total_seats,
                   (select count(*) from holds h
                    where h.class_id = c.id and h.status = 'HELD')::int,
                   (select count(*) from holds h
                    where h.class_id = c.id and h.status = 'CONFIRMED')::int
            from admission_classes c
            where c.id = @id
            """, db, tx);
        cmd.Parameters.AddWithValue("id", classId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0, 0);
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<int> ExpireClassAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string classId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            update holds
            set status = 'EXPIRED', updated_at = clock_timestamp()
            where class_id = @class and status = 'HELD' and expires_at <= clock_timestamp()
            """, db, tx);
        cmd.Parameters.AddWithValue("class", classId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HoldState?> GetHoldStateAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string holdId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            "select class_id, student_id, status, expires_at from holds where id = @id", db, tx);
        cmd.Parameters.AddWithValue("id", holdId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new HoldState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTime>(3));
    }

    private readonly record struct HoldState(string ClassId, string StudentId, string Status, DateTime ExpiresAt);
}

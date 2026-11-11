using Npgsql;
using System.Data;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.Services.AddCors();
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("Database")
    ?? "Host=localhost;Port=5432;Database=school_admission;Username=school;Password=school";
var holdSeconds = int.TryParse(Environment.GetEnvironmentVariable("HOLD_DURATION_SECONDS"), out var configured) && configured > 0 ? configured : 600;
await InitializeDatabase(connectionString);
var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

app.MapPost("/classes", async (ClassRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.TotalSeats <= 0)
        return Results.BadRequest(new { error = "Name is required and totalSeats must be positive." });
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
    var id = "c_" + Guid.NewGuid().ToString("N");
    await using var cmd = new NpgsqlCommand("insert into admission_classes(id,name,total_seats) values(@id,@name,@seats)", db);
    cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("name", request.Name.Trim()); cmd.Parameters.AddWithValue("seats", request.TotalSeats);
    await cmd.ExecuteNonQueryAsync();
    return Results.Created($"/classes/{id}", new { id, name = request.Name.Trim(), totalSeats = request.TotalSeats });
});

app.MapGet("/classes", async () => Results.Ok(await GetClasses(connectionString)));
app.MapGet("/classes/{id}", async (string id) =>
{
    var item = (await GetClasses(connectionString, id)).FirstOrDefault();
    return item is null ? Results.NotFound(new { error = "Class not found." }) : Results.Ok(item);
});

app.MapPost("/classes/{id}/holds", async (string id, HttpRequest http) =>
{
    var student = StudentId(http);
    if (student is null) return Results.BadRequest(new { error = "X-Student-Id must be a 17-digit birth registration number." });
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted);
    try
    {
        await LockClass(db, tx, id);
        await Expire(db, tx);
        var classRow = await GetClassRow(db, tx, id);
        if (classRow is null) { await tx.RollbackAsync(); return Results.NotFound(new { error = "Class not found." }); }
        if (await HasActiveApplication(db, tx, id, student)) { await tx.RollbackAsync(); return Results.Conflict(new { error = "Application already exists for this class." }); }
        if (classRow.Value.held + classRow.Value.confirmed >= classRow.Value.total) { await tx.RollbackAsync(); return Results.Conflict(new { error = "No seats available." }); }
        var holdId = "h_" + Guid.NewGuid().ToString("N"); var expires = DateTime.UtcNow.AddSeconds(holdSeconds);
        await using var cmd = new NpgsqlCommand("insert into holds(id,class_id,student_id,status,expires_at) values(@id,@class,@student,'HELD',@expires)", db, tx);
        cmd.Parameters.AddWithValue("id", holdId); cmd.Parameters.AddWithValue("class", id); cmd.Parameters.AddWithValue("student", student); cmd.Parameters.AddWithValue("expires", expires);
        await cmd.ExecuteNonQueryAsync(); await tx.CommitAsync();
        return Results.Created($"/holds/{holdId}", new { holdId, classId = id, studentId = student, status = "HELD", expiresAt = expires });
    }
    catch (PostgresException ex) when (ex.SqlState == "23505") { await tx.RollbackAsync(); return Results.Conflict(new { error = "Application already exists for this class." }); }
});

app.MapGet("/holds/{id}", async (string id) =>
{
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
    await Expire(db, null);
    var h = await GetHold(db, null, id);
    return h is null ? Results.NotFound(new { error = "Hold not found." }) : Results.Ok(h);
});

app.MapPost("/holds/{id}/confirm", async (string id, HttpRequest http) =>
{
    var student = StudentId(http); if (student is null) return Results.BadRequest(new { error = "X-Student-Id must be a 17-digit birth registration number." });
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    var hold = await GetHoldRow(db, tx, id);
    if (hold is null) { await tx.RollbackAsync(); return Results.NotFound(new { error = "Hold not found." }); }
    await LockClass(db, tx, hold.Value.classId); await Expire(db, tx); hold = await GetHoldRow(db, tx, id);
    if (hold.Value.student != student) { await tx.RollbackAsync(); return Results.Json(new { error = "Not your application." }, statusCode: StatusCodes.Status403Forbidden); }
    if (hold.Value.status == "CONFIRMED") { await tx.CommitAsync(); return Results.Ok(new { holdId = id, status = "CONFIRMED" }); }
    if (hold.Value.status != "HELD" || hold.Value.expires <= DateTime.UtcNow) { await tx.CommitAsync(); return Results.Conflict(new { error = "Application cannot be confirmed." }); }
    await using var cmd = new NpgsqlCommand("update holds set status='CONFIRMED' where id=@id and status='HELD'", db, tx); cmd.Parameters.AddWithValue("id", id); await cmd.ExecuteNonQueryAsync();
    await tx.CommitAsync(); return Results.Ok(new { holdId = id, status = "CONFIRMED" });
});

app.MapDelete("/holds/{id}", async (string id, HttpRequest http) =>
{
    var student = StudentId(http); if (student is null) return Results.BadRequest(new { error = "X-Student-Id must be a 17-digit birth registration number." });
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
    await using var cmd = new NpgsqlCommand("update holds set status='CANCELLED' where id=@id and student_id=@student and status='HELD'", db);
    cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("student", student);
    var n = await cmd.ExecuteNonQueryAsync();
    if (n > 0) return Results.NoContent();
    await using var exists = new NpgsqlCommand("select exists(select 1 from holds where id=@id)", db);
    exists.Parameters.AddWithValue("id", id);
    return (bool)(await exists.ExecuteScalarAsync())!
        ? Results.Conflict(new { error = "Application cannot be cancelled." })
        : Results.NotFound(new { error = "Hold not found." });
});

app.MapGet("/me/holds", async (HttpRequest http) =>
{
    var student = StudentId(http); if (student is null) return Results.BadRequest(new { error = "X-Student-Id must be a 17-digit birth registration number." });
    await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync(); await Expire(db, null);
    await using var cmd = new NpgsqlCommand("select h.id,h.class_id,c.name,h.status,h.expires_at from holds h join admission_classes c on c.id=h.class_id where h.student_id=@student order by h.created_at desc", db);
    cmd.Parameters.AddWithValue("student", student); await using var r = await cmd.ExecuteReaderAsync(); var list = new List<object>();
    while (await r.ReadAsync()) list.Add(new { holdId = r.GetString(0), classId = r.GetString(1), className = r.GetString(2), status = r.GetString(3), expiresAt = r.GetFieldValue<DateTime>(4) });
    return Results.Ok(list);
});
app.Run();

static string? StudentId(HttpRequest r) => r.Headers.TryGetValue("X-Student-Id", out var v) && Regex.IsMatch(v.ToString(), "^[0-9]{17}$") ? v.ToString() : null;
static async Task InitializeDatabase(string cs) { await using var db = new NpgsqlConnection(cs); for (var i = 0; i < 30; i++) try { await db.OpenAsync(); break; } catch { await Task.Delay(1000); } if (db.State != ConnectionState.Open) throw new Exception("Database unavailable"); await using var c = new NpgsqlCommand("create table if not exists admission_classes(id text primary key,name text not null,total_seats integer not null check(total_seats>0)); create table if not exists holds(id text primary key,class_id text references admission_classes(id),student_id text not null,status text not null,expires_at timestamptz not null,created_at timestamptz not null default now()); create unique index if not exists one_active on holds(class_id,student_id) where status in ('HELD','CONFIRMED'); create index if not exists holds_expiry on holds(expires_at) where status='HELD';", db); await c.ExecuteNonQueryAsync(); }
static async Task Expire(NpgsqlConnection db, NpgsqlTransaction? tx) { await using var c = new NpgsqlCommand("update holds set status='EXPIRED' where status='HELD' and expires_at<=now()", db, tx); await c.ExecuteNonQueryAsync(); }
static async Task LockClass(NpgsqlConnection db, NpgsqlTransaction tx, string id) { await using var c = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext(@id))", db, tx); c.Parameters.AddWithValue("id", id); await c.ExecuteNonQueryAsync(); }
static async Task<(int total,int held,int confirmed)?> GetClassRow(NpgsqlConnection db,NpgsqlTransaction? tx,string id) { await using var c=new NpgsqlCommand("select total_seats,(select count(*) from holds where class_id=c.id and status='HELD'),(select count(*) from holds where class_id=c.id and status='CONFIRMED') from admission_classes c where id=@id",db,tx); c.Parameters.AddWithValue("id",id); await using var r=await c.ExecuteReaderAsync(); return (await r.ReadAsync())?(r.GetInt32(0),Convert.ToInt32(r.GetInt64(1)),Convert.ToInt32(r.GetInt64(2))):null; }
static async Task<bool> HasActiveApplication(NpgsqlConnection db,NpgsqlTransaction tx,string id,string student) { await using var c=new NpgsqlCommand("select exists(select 1 from holds where class_id=@id and student_id=@student and status in ('HELD','CONFIRMED'))",db,tx); c.Parameters.AddWithValue("id",id); c.Parameters.AddWithValue("student",student); return (bool)(await c.ExecuteScalarAsync())!; }
static async Task<object[]> GetClasses(string cs,string? only=null) { await using var db=new NpgsqlConnection(cs); await db.OpenAsync(); await Expire(db,null); await using var c=new NpgsqlCommand("select c.id,c.name,c.total_seats,count(h.id) filter(where h.status='HELD'),count(h.id) filter(where h.status='CONFIRMED') from admission_classes c left join holds h on h.class_id=c.id "+(only is null?"":"where c.id=@id")+" group by c.id order by c.name",db); if(only is not null)c.Parameters.AddWithValue("id",only); await using var r=await c.ExecuteReaderAsync(); var l=new List<object>(); while(await r.ReadAsync()){var t=r.GetInt32(2);var held=Convert.ToInt32(r.GetInt64(3));var confirmed=Convert.ToInt32(r.GetInt64(4));l.Add(new{id=r.GetString(0),name=r.GetString(1),totalSeats=t,heldSeats=held,confirmedSeats=confirmed,availableSeats=t-held-confirmed});} return l.ToArray(); }
static async Task<object?> GetHold(NpgsqlConnection db,NpgsqlTransaction? tx,string id) { var h=await GetHoldRow(db,tx,id); return h is null?null:new{holdId=id,classId=h.Value.classId,studentId=h.Value.student,status=h.Value.status,expiresAt=h.Value.expires}; }
static async Task<(string classId,string student,string status,DateTime expires)?> GetHoldRow(NpgsqlConnection db,NpgsqlTransaction? tx,string id) { await using var c=new NpgsqlCommand("select class_id,student_id,status,expires_at from holds where id=@id",db,tx); c.Parameters.AddWithValue("id",id); await using var r=await c.ExecuteReaderAsync(); return (await r.ReadAsync())?(r.GetString(0),r.GetString(1),r.GetString(2),r.GetFieldValue<DateTime>(3)):null; }

record ClassRequest(string Name, int TotalSeats);

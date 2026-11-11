using Npgsql;
using SchoolAdmission.Api.Api;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Infrastructure;
using SchoolAdmission.Api.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080");

var configuredConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("Database")
    ?? "Host=localhost;Port=5432;Database=school_admission;Username=school;Password=school";

// Two API processes share one PostgreSQL instance. A bounded pool per process
// keeps total connections well below the server limit under burst load.
var connectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString)
{
    MaxPoolSize = 20,
    Timeout = 15,
    CommandTimeout = 30,
    ApplicationName = "school-admission-api"
}.ConnectionString;

var holdDurationSeconds = ReadPositiveInt("HOLD_DURATION_SECONDS", 600);

builder.Services.AddSingleton(new AdmissionRepository(connectionString, holdDurationSeconds));
builder.Services.AddHostedService<HoldExpirationWorker>();
builder.Services.AddCors();

var app = builder.Build();

await DatabaseBootstrapper.InitializeAsync(
    connectionString,
    app.Logger,
    app.Lifetime.ApplicationStopping);

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.MapAdmissionEndpoints();

app.Run();

static int ReadPositiveInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
        ? value
        : fallback;

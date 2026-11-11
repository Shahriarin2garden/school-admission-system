using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class AdmissionApiTests
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:8080";

    private static readonly string BaseUrl2 =
        Environment.GetEnvironmentVariable("API_URL_2") ?? "http://localhost:8081";

    private static readonly string DatabaseUrl =
        Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? "Host=localhost;Port=5432;Database=school_admission;Username=school;Password=school";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    // ---------------------------------------------------------------- helpers

    private static string UniqueClassName() => "Test Class " + Guid.NewGuid().ToString("N");

    private static long _studentSequence;

    private static string UniqueStudentId()
    {
        var sequence = Interlocked.Increment(ref _studentSequence);
        var stamp = (ulong)DateTime.UtcNow.Ticks % 9_000_000_000_000_000UL;
        var value = (stamp + (ulong)sequence) % 10_000_000_000_000_000UL;
        return "2" + value.ToString("D16");
    }

    private static async Task<ClassResponse> CreateClassAsync(string? name = null, int seats = 5, string? baseUrl = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"{baseUrl ?? BaseUrl}/classes",
            new { name = name ?? UniqueClassName(), totalSeats = seats });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClassResponse>())!;
    }

    private static HttpRequestMessage HoldRequest(string classId, string studentId, string? baseUrl = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl ?? BaseUrl}/classes/{classId}/holds");
        request.Headers.Add("X-Student-Id", studentId);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage ConfirmRequest(string holdId, string studentId, string? baseUrl = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl ?? BaseUrl}/holds/{holdId}/confirm");
        request.Headers.Add("X-Student-Id", studentId);
        return request;
    }

    private static HttpRequestMessage CancelRequest(string holdId, string studentId, string? baseUrl = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl ?? BaseUrl}/holds/{holdId}");
        request.Headers.Add("X-Student-Id", studentId);
        return request;
    }

    private static async Task<HoldResponse> CreateHoldAsync(string classId, string studentId, string? baseUrl = null)
    {
        var response = await Client.SendAsync(HoldRequest(classId, studentId, baseUrl));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HoldResponse>())!;
    }

    private static async Task<ClassSummary> GetClassAsync(string classId)
    {
        var response = await Client.GetAsync($"{BaseUrl}/classes/{classId}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClassSummary>())!;
    }

    private static async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(DatabaseUrl);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> CountHoldsAsync(string classId, string status)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "select count(*) from holds where class_id = @class and status = @status", db);
        cmd.Parameters.AddWithValue("class", classId);
        cmd.Parameters.AddWithValue("status", status);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> GetDatabaseStatusAsync(string holdId)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("select status from holds where id = @id", db);
        cmd.Parameters.AddWithValue("id", holdId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ForceExpiryAsync(string holdId)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(
            "update holds set expires_at = clock_timestamp() - interval '2 seconds' where id = @id", db);
        cmd.Parameters.AddWithValue("id", holdId);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    private static async Task<T> WaitForAsync<T>(
        Func<Task<T>> action,
        Func<T, bool> predicate,
        int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var last = await action();
        while (!predicate(last) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            last = await action();
        }

        return last;
    }

    private static async Task<HttpResponseMessage> GetHealthAsync(string baseUrl)
    {
        return await Client.GetAsync($"{baseUrl}/health");
    }

    // ------------------------------------------------------------- API tests

    [Fact]
    public async Task ClassCreation_Returns201WithExpectedFields()
    {
        var name = UniqueClassName();
        var response = await Client.PostAsJsonAsync(
            $"{BaseUrl}/classes", new { name, totalSeats = 7 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ClassResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(body.Id));
        Assert.Equal(name, body.Name);
        Assert.Equal(7, body.TotalSeats);
        Assert.Equal($"/classes/{body.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ClassCreation_RejectsInvalidNamesAndSeats()
    {
        var invalidBodies = new object[]
        {
            new { name = "", totalSeats = 5 },
            new { name = "   ", totalSeats = 5 },
            new { name = new string('x', 201), totalSeats = 5 },
            new { name = "Valid", totalSeats = 0 },
            new { name = "Valid", totalSeats = -3 }
        };

        foreach (var body in invalidBodies)
        {
            var response = await Client.PostAsJsonAsync($"{BaseUrl}/classes", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            Assert.False(string.IsNullOrWhiteSpace(payload?.Error));
        }
    }

    [Fact]
    public async Task ClassCreation_RejectsMalformedOrMissingJson()
    {
        var malformed = await Client.PostAsync(
            $"{BaseUrl}/classes",
            new StringContent("{not valid json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("error", (await malformed.Content.ReadAsStringAsync()).ToLowerInvariant());

        var empty = await Client.PostAsync(
            $"{BaseUrl}/classes",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task StudentHeader_IsRequiredAndStrictlyValidated()
    {
        var created = await CreateClassAsync(seats: 3);
        var invalidValues = new[]
        {
            null,
            "",
            "1234567890123456",
            "123456789012345678",
            "1234567890123456a",
            "12345 78901234567",
            "1234567890123456-"
        };

        foreach (var value in invalidValues)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{BaseUrl}/classes/{created.Id}/holds");
            if (value is not null)
            {
                request.Headers.Add("X-Student-Id", value);
            }

            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task MissingResources_Return404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"{BaseUrl}/classes/c_missing")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"{BaseUrl}/holds/h_missing")).StatusCode);

        var hold = await Client.SendAsync(HoldRequest("c_missing", UniqueStudentId()));
        Assert.Equal(HttpStatusCode.NotFound, hold.StatusCode);

        var cancel = await Client.SendAsync(CancelRequest("h_missing", UniqueStudentId()));
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Fact]
    public async Task HoldCreation_ReturnsExpectedFieldsAndFutureExpiry()
    {
        var created = await CreateClassAsync(seats: 4);
        var student = UniqueStudentId();

        var response = await Client.SendAsync(HoldRequest(created.Id, student));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<HoldResponse>())!;
        Assert.StartsWith("h_", body.HoldId);
        Assert.Equal(created.Id, body.ClassId);
        Assert.Equal(student, body.StudentId);
        Assert.Equal("HELD", body.Status);
        Assert.True(body.ExpiresAt > DateTime.UtcNow);
        Assert.Equal($"/holds/{body.HoldId}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task HoldStatus_DoesNotExposeStudentRegistrationNumber()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);

        var response = await Client.GetAsync($"{BaseUrl}/holds/{hold.HoldId}");
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(student, raw);
        using var document = JsonDocument.Parse(raw);
        Assert.False(document.RootElement.TryGetProperty("studentId", out _));
        Assert.Equal("HELD", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Capacity_RejectsApplicationsBeyondTotalSeats()
    {
        var created = await CreateClassAsync(seats: 2);

        var first = await Client.SendAsync(HoldRequest(created.Id, UniqueStudentId()));
        var second = await Client.SendAsync(HoldRequest(created.Id, UniqueStudentId()));
        var third = await Client.SendAsync(HoldRequest(created.Id, UniqueStudentId()));
        var fourth = await Client.SendAsync(HoldRequest(created.Id, UniqueStudentId()));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);

        var summary = await GetClassAsync(created.Id);
        Assert.Equal(2, summary.HeldSeats);
        Assert.Equal(0, summary.ConfirmedSeats);
        Assert.Equal(0, summary.AvailableSeats);
    }

    [Fact]
    public async Task ConcurrentHolds_NeverExceedCapacityOnSingleInstance()
    {
        var created = await CreateClassAsync(seats: 5);
        var students = Enumerable.Range(0, 60).Select(_ => UniqueStudentId()).ToArray();

        var responses = await Task.WhenAll(students.Select(async student =>
        {
            using var request = HoldRequest(created.Id, student);
            return await Client.SendAsync(request);
        }));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(55, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, responses.Count(r => (int)r.StatusCode >= 500));

        Assert.Equal(5, await CountHoldsAsync(created.Id, "HELD"));
        Assert.Equal(0, await CountHoldsAsync(created.Id, "CONFIRMED"));

        var summary = await GetClassAsync(created.Id);
        Assert.Equal(0, summary.AvailableSeats);

        await AssertNoDuplicateActiveApplicationsAsync(created.Id);
    }

    [Fact]
    public async Task ConcurrentHolds_NeverExceedCapacityAcrossTwoInstances()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(BaseUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(BaseUrl2)).StatusCode);

        var created = await CreateClassAsync(seats: 5);
        var students = Enumerable.Range(0, 100).Select(_ => UniqueStudentId()).ToArray();

        var responses = await Task.WhenAll(students.Select(async (student, index) =>
        {
            var baseUrl = index % 2 == 0 ? BaseUrl : BaseUrl2;
            using var request = HoldRequest(created.Id, student, baseUrl);
            return await Client.SendAsync(request);
        }));

        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(95, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, responses.Count(r => (int)r.StatusCode >= 500));
        Assert.Equal(0, responses.Count(r => r.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict)));

        Assert.Equal(5, await CountHoldsAsync(created.Id, "HELD"));
        Assert.Equal(0, await CountHoldsAsync(created.Id, "CONFIRMED"));
        Assert.Equal(0, (await GetClassAsync(created.Id)).AvailableSeats);

        await AssertNoDuplicateActiveApplicationsAsync(created.Id);
    }

    [Fact]
    public async Task DuplicateActiveApplication_IsRejected()
    {
        var created = await CreateClassAsync(seats: 3);
        var student = UniqueStudentId();

        var first = await Client.SendAsync(HoldRequest(created.Id, student));
        var second = await Client.SendAsync(HoldRequest(created.Id, student));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountHoldsAsync(created.Id, "HELD"));
    }

    [Fact]
    public async Task StudentCanApplyToIndependentClasses()
    {
        var first = await CreateClassAsync(seats: 1);
        var second = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();

        Assert.Equal(HttpStatusCode.Created, (await Client.SendAsync(HoldRequest(first.Id, student))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Client.SendAsync(HoldRequest(second.Id, student))).StatusCode);
    }

    [Fact]
    public async Task WrongStudentCannotConfirmOrCancel()
    {
        var created = await CreateClassAsync(seats: 1);
        var owner = UniqueStudentId();
        var intruder = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, owner);

        var confirm = await Client.SendAsync(ConfirmRequest(hold.HoldId, intruder));
        var cancel = await Client.SendAsync(CancelRequest(hold.HoldId, intruder));

        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal("HELD", await GetDatabaseStatusAsync(hold.HoldId));
    }

    [Fact]
    public async Task Confirmation_TransitionsHeldToConfirmed()
    {
        var created = await CreateClassAsync(seats: 2);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);

        var response = await Client.SendAsync(ConfirmRequest(hold.HoldId, student));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<HoldStatusResponse>())!;
        Assert.Equal(hold.HoldId, body.HoldId);
        Assert.Equal("CONFIRMED", body.Status);
        Assert.Equal("CONFIRMED", await GetDatabaseStatusAsync(hold.HoldId));

        var summary = await GetClassAsync(created.Id);
        Assert.Equal(0, summary.HeldSeats);
        Assert.Equal(1, summary.ConfirmedSeats);
        Assert.Equal(1, summary.AvailableSeats);
    }

    [Fact]
    public async Task Confirmation_IsIdempotent()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);

        for (var i = 0; i < 3; i++)
        {
            var response = await Client.SendAsync(ConfirmRequest(hold.HoldId, student));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("CONFIRMED", (await response.Content.ReadFromJsonAsync<HoldStatusResponse>())!.Status);
        }

        Assert.Equal(1, await CountHoldsAsync(created.Id, "CONFIRMED"));
        var summary = await GetClassAsync(created.Id);
        Assert.Equal(1, summary.ConfirmedSeats);
        Assert.Equal(0, summary.AvailableSeats);
    }

    [Fact]
    public async Task ExpiredHold_IsAutomaticallyExpiredAndSeatReclaimed()
    {
        var created = await CreateClassAsync(seats: 1);
        var hold = await CreateHoldAsync(created.Id, UniqueStudentId());
        await ForceExpiryAsync(hold.HoldId);

        var status = await WaitForAsync(
            () => GetDatabaseStatusAsync(hold.HoldId),
            value => value == "EXPIRED");
        Assert.Equal("EXPIRED", status);

        var summary = await GetClassAsync(created.Id);
        Assert.Equal(0, summary.HeldSeats);
        Assert.Equal(1, summary.AvailableSeats);
    }

    [Fact]
    public async Task ClassCounts_ExcludeOverdueHoldsBeforeSweep()
    {
        var created = await CreateClassAsync(seats: 1);
        var hold = await CreateHoldAsync(created.Id, UniqueStudentId());
        await ForceExpiryAsync(hold.HoldId);

        // No waiting for the background worker: the count query must not treat
        // an overdue HELD row as currently held.
        var summary = await GetClassAsync(created.Id);
        Assert.Equal(0, summary.HeldSeats);
        Assert.Equal(1, summary.AvailableSeats);
    }

    [Fact]
    public async Task ExpiredHold_CannotBeConfirmed()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);
        await ForceExpiryAsync(hold.HoldId);

        var response = await Client.SendAsync(ConfirmRequest(hold.HoldId, student));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("EXPIRED", await GetDatabaseStatusAsync(hold.HoldId));
    }

    [Fact]
    public async Task OwnerCanCancelHeldApplication()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);

        var response = await Client.SendAsync(CancelRequest(hold.HoldId, student));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("CANCELLED", await GetDatabaseStatusAsync(hold.HoldId));

        var summary = await GetClassAsync(created.Id);
        Assert.Equal(0, summary.HeldSeats);
        Assert.Equal(1, summary.AvailableSeats);
    }

    [Fact]
    public async Task ExpiredHold_CannotBeCancelled()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);
        await ForceExpiryAsync(hold.HoldId);

        var response = await Client.SendAsync(CancelRequest(hold.HoldId, student));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("EXPIRED", await GetDatabaseStatusAsync(hold.HoldId));
    }

    [Fact]
    public async Task CancelledApplication_CannotBeConfirmed()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(CancelRequest(hold.HoldId, student))).StatusCode);

        var confirm = await Client.SendAsync(ConfirmRequest(hold.HoldId, student));
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal("CANCELLED", await GetDatabaseStatusAsync(hold.HoldId));
    }

    [Fact]
    public async Task ConfirmedApplication_CannotBeCancelled()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);

        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(ConfirmRequest(hold.HoldId, student))).StatusCode);

        var cancel = await Client.SendAsync(CancelRequest(hold.HoldId, student));
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("CONFIRMED", await GetDatabaseStatusAsync(hold.HoldId));
    }

    [Fact]
    public async Task StudentCanReapplyAfterCancellation()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.SendAsync(CancelRequest(hold.HoldId, student))).StatusCode);

        var reapply = await Client.SendAsync(HoldRequest(created.Id, student));
        Assert.Equal(HttpStatusCode.Created, reapply.StatusCode);
    }

    [Fact]
    public async Task StudentCanReapplyAfterExpiration()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);
        await ForceExpiryAsync(hold.HoldId);

        var reapply = await Client.SendAsync(HoldRequest(created.Id, student));
        Assert.Equal(HttpStatusCode.Created, reapply.StatusCode);
    }

    [Fact]
    public async Task ConfirmAndCancel_ConcurrentRequestsReachConsistentTerminalState()
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var created = await CreateClassAsync(seats: 1);
            var student = UniqueStudentId();
            var hold = await CreateHoldAsync(created.Id, student);

            using var confirm = ConfirmRequest(hold.HoldId, student);
            using var cancel = CancelRequest(hold.HoldId, student);
            var responses = await Task.WhenAll(Client.SendAsync(confirm), Client.SendAsync(cancel));

            var confirmResponse = responses[0];
            var cancelResponse = responses[1];

            Assert.False((int)confirmResponse.StatusCode >= 500);
            Assert.False((int)cancelResponse.StatusCode >= 500);

            var confirmWon = confirmResponse.StatusCode == HttpStatusCode.OK;
            var cancelWon = cancelResponse.StatusCode == HttpStatusCode.NoContent;
            Assert.True(confirmWon ^ cancelWon, "Exactly one competing transition must succeed.");

            var expected = confirmWon ? "CONFIRMED" : "CANCELLED";
            var rejected = confirmWon ? cancelResponse : confirmResponse;
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal(expected, await GetDatabaseStatusAsync(hold.HoldId));
        }
    }

    [Fact]
    public async Task Records_AreDurableInPostgreSql()
    {
        var created = await CreateClassAsync(seats: 1);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(ConfirmRequest(hold.HoldId, student))).StatusCode);

        // Read back through completely fresh connections, independent of the
        // API's connection pool.
        Assert.Equal(1, await CountHoldsAsync(created.Id, "CONFIRMED"));
        Assert.Equal("CONFIRMED", await GetDatabaseStatusAsync(hold.HoldId));

        var summary = await GetClassAsync(created.Id);
        Assert.Equal(1, summary.TotalSeats);
        Assert.Equal(1, summary.ConfirmedSeats);
    }

    [Fact]
    public async Task Operations_WorkThroughEitherBackendInstance()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(BaseUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetHealthAsync(BaseUrl2)).StatusCode);

        var created = await CreateClassAsync(seats: 2, baseUrl: BaseUrl);
        var student = UniqueStudentId();
        var hold = await CreateHoldAsync(created.Id, student, BaseUrl2);

        var viaFirst = await Client.GetAsync($"{BaseUrl}/holds/{hold.HoldId}");
        Assert.Equal(HttpStatusCode.OK, viaFirst.StatusCode);

        var confirm = await Client.SendAsync(ConfirmRequest(hold.HoldId, student, BaseUrl));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var viaSecond = await Client.GetAsync($"{BaseUrl2}/holds/{hold.HoldId}");
        viaSecond.EnsureSuccessStatusCode();
        var status = (await viaSecond.Content.ReadFromJsonAsync<HoldStatusResponse>())!;
        Assert.Equal("CONFIRMED", status.Status);
    }

    [Fact]
    public async Task HealthEndpoint_ReportsDatabaseReadiness()
    {
        var response = await GetHealthAsync(BaseUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("healthy", payload?.Status);
    }

    private static async Task AssertNoDuplicateActiveApplicationsAsync(string classId)
    {
        await using var db = await OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("""
            select count(*) from (
                select student_id
                from holds
                where class_id = @class and status in ('HELD', 'CONFIRMED')
                group by student_id
                having count(*) > 1
            ) duplicates
            """, db);
        cmd.Parameters.AddWithValue("class", classId);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    private sealed record ClassResponse(string Id, string Name, int TotalSeats);

    private sealed record ClassSummary(
        string Id,
        string Name,
        int TotalSeats,
        int HeldSeats,
        int ConfirmedSeats,
        int AvailableSeats);

    private sealed record HoldResponse(
        string HoldId,
        string ClassId,
        string StudentId,
        string Status,
        DateTime ExpiresAt);

    private sealed record HoldStatusResponse(string HoldId, string Status);

    private sealed record ErrorResponse(string Error);

    private sealed record HealthResponse(string Status);
}

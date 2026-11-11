using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using SchoolAdmission.Api.Data;

namespace SchoolAdmission.Api.Api;

public sealed record CreateClassRequest(string? Name, int? TotalSeats);

public static partial class ApiEndpoints
{
    private const int MaxClassNameLength = 200;

    public static void MapAdmissionEndpoints(this WebApplication app)
    {
        app.MapPost("/classes", CreateClassAsync);
        app.MapGet("/classes", GetClassesAsync);
        app.MapGet("/classes/{classId}", GetClassAsync);
        app.MapPost("/classes/{classId}/holds", CreateHoldAsync);
        app.MapGet("/holds/{holdId}", GetHoldAsync);
        app.MapPost("/holds/{holdId}/confirm", ConfirmAsync);
        app.MapDelete("/holds/{holdId}", CancelAsync);
        app.MapGet("/me/holds", GetMyHoldsAsync);
        app.MapGet("/health", HealthAsync);
    }

    private static async Task<IResult> CreateClassAsync(
        HttpRequest http,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        CreateClassRequest? request;
        try
        {
            request = await http.ReadFromJsonAsync<CreateClassRequest>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "Malformed JSON request body.");
        }
        catch (NotSupportedException)
        {
            return Error(StatusCodes.Status400BadRequest, "Request body must be JSON.");
        }

        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "Request body is required.");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error(StatusCodes.Status400BadRequest, "Class name is required.");
        }

        if (name.Length > MaxClassNameLength)
        {
            return Error(StatusCodes.Status400BadRequest, $"Class name must be at most {MaxClassNameLength} characters.");
        }

        if (request.TotalSeats is null or <= 0)
        {
            return Error(StatusCodes.Status400BadRequest, "totalSeats must be a positive integer.");
        }

        var created = await repository.CreateClassAsync(name, request.TotalSeats.Value, cancellationToken);
        return Results.Created($"/classes/{created.Id}", created);
    }

    private static async Task<IResult> GetClassesAsync(
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await repository.GetClassesAsync(null, cancellationToken));
    }

    private static async Task<IResult> GetClassAsync(
        string classId,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        var classes = await repository.GetClassesAsync(classId, cancellationToken);
        var item = classes.Count == 0 ? null : classes[0];
        return item is null
            ? Error(StatusCodes.Status404NotFound, "Class not found.")
            : Results.Ok(item);
    }

    private static async Task<IResult> CreateHoldAsync(
        string classId,
        HttpRequest http,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryReadStudentId(http, out var studentId))
        {
            return InvalidStudentId();
        }

        var result = await repository.CreateHoldAsync(classId, studentId, cancellationToken);
        return result.Outcome switch
        {
            CreateHoldOutcome.Created => Results.Created($"/holds/{result.Hold!.HoldId}", result.Hold),
            CreateHoldOutcome.ClassNotFound => Error(StatusCodes.Status404NotFound, "Class not found."),
            CreateHoldOutcome.Duplicate => Error(
                StatusCodes.Status409Conflict,
                "An active application already exists for this class."),
            CreateHoldOutcome.Full => Error(StatusCodes.Status409Conflict, "No seats available."),
            _ => Error(StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };
    }

    private static async Task<IResult> GetHoldAsync(
        string holdId,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        var hold = await repository.GetHoldAsync(holdId, cancellationToken);
        return hold is null
            ? Error(StatusCodes.Status404NotFound, "Hold not found.")
            : Results.Ok(hold);
    }

    private static async Task<IResult> ConfirmAsync(
        string holdId,
        HttpRequest http,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryReadStudentId(http, out var studentId))
        {
            return InvalidStudentId();
        }

        var result = await repository.ConfirmAsync(holdId, studentId, cancellationToken);
        return result.Outcome switch
        {
            TransitionOutcome.Success => Results.Ok(new { holdId, status = result.Status ?? "CONFIRMED" }),
            TransitionOutcome.NotFound => Error(StatusCodes.Status404NotFound, "Hold not found."),
            TransitionOutcome.Forbidden => Error(StatusCodes.Status403Forbidden, "Not your application."),
            TransitionOutcome.Conflict => Error(
                StatusCodes.Status409Conflict,
                "Application cannot be confirmed.",
                result.Status),
            _ => Error(StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };
    }

    private static async Task<IResult> CancelAsync(
        string holdId,
        HttpRequest http,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryReadStudentId(http, out var studentId))
        {
            return InvalidStudentId();
        }

        var result = await repository.CancelAsync(holdId, studentId, cancellationToken);
        return result.Outcome switch
        {
            TransitionOutcome.Success => Results.NoContent(),
            TransitionOutcome.NotFound => Error(StatusCodes.Status404NotFound, "Hold not found."),
            TransitionOutcome.Forbidden => Error(StatusCodes.Status403Forbidden, "Not your application."),
            TransitionOutcome.Conflict => Error(
                StatusCodes.Status409Conflict,
                "Application cannot be cancelled.",
                result.Status),
            _ => Error(StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };
    }

    private static async Task<IResult> GetMyHoldsAsync(
        HttpRequest http,
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TryReadStudentId(http, out var studentId))
        {
            return InvalidStudentId();
        }

        return Results.Ok(await repository.GetStudentHoldsAsync(studentId, cancellationToken));
    }

    private static async Task<IResult> HealthAsync(
        AdmissionRepository repository,
        CancellationToken cancellationToken)
    {
        return await repository.PingAsync(cancellationToken)
            ? Results.Ok(new { status = "healthy" })
            : Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static bool TryReadStudentId(HttpRequest http, out string studentId)
    {
        studentId = string.Empty;
        if (!http.Headers.TryGetValue("X-Student-Id", out StringValues values) || values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (string.IsNullOrEmpty(value) || !StudentIdPattern().IsMatch(value))
        {
            return false;
        }

        studentId = value;
        return true;
    }

    private static IResult InvalidStudentId() =>
        Error(StatusCodes.Status400BadRequest, "X-Student-Id must be exactly 17 ASCII digits.");

    private static IResult Error(int statusCode, string message, string? currentStatus = null) =>
        Results.Json(
            currentStatus is null ? new { error = message } : new { error = message, status = currentStatus },
            statusCode: statusCode);

    [GeneratedRegex("^[0-9]{17}$", RegexOptions.CultureInvariant)]
    private static partial Regex StudentIdPattern();
}

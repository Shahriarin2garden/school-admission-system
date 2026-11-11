namespace SchoolAdmission.Api.Data;

public sealed record ClassSummary(
    string Id,
    string Name,
    int TotalSeats,
    int HeldSeats,
    int ConfirmedSeats,
    int AvailableSeats);

public sealed record CreatedClass(string Id, string Name, int TotalSeats);

public sealed record CreatedHold(
    string HoldId,
    string ClassId,
    string StudentId,
    string Status,
    DateTime ExpiresAt);

public sealed record HoldSummary(
    string HoldId,
    string ClassId,
    string ClassName,
    string Status,
    DateTime ExpiresAt);

public enum CreateHoldOutcome
{
    Created,
    ClassNotFound,
    Duplicate,
    Full
}

public sealed record CreateHoldResult(CreateHoldOutcome Outcome, CreatedHold? Hold);

public enum TransitionOutcome
{
    Success,
    NotFound,
    Forbidden,
    Conflict
}

public sealed record TransitionResult(TransitionOutcome Outcome, string? Status);

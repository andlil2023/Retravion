using CSharpFunctionalExtensions;

namespace Retravion.Identity.Domain;

/// <summary>Consecutive credential failures, retry delay, and terminal lockout at ten failures.</summary>
public sealed record SignInFailures
{
    public static SignInFailures None { get; } = new(0, DateTimeOffset.MinValue);

    public int Count { get; }
    public DateTimeOffset RetryAt { get; }
    public bool IsLocked => Count == 10;

    private SignInFailures(int count, DateTimeOffset retryAt)
    {
        Count = count;
        RetryAt = retryAt;
    }

    public bool CanAttempt(DateTimeOffset now) => !IsLocked && now >= RetryAt;

    public SignInFailures RecordFailure(DateTimeOffset now)
    {
        if (IsLocked) return this;
        var count = Count + 1;
        return new(count, count is >= 3 and < 10 ? now.AddSeconds(5) : DateTimeOffset.MinValue);
    }

    public static Result<SignInFailures> Restore(int count, DateTimeOffset retryAt) =>
        Result.Success()
            .Ensure(() => count is >= 0 and <= 10, "Invalid sign-in failure count.")
            .Ensure(() => count is >= 3 and < 10
                    ? retryAt > DateTimeOffset.MinValue
                    : retryAt == DateTimeOffset.MinValue,
                "Invalid sign-in retry time.")
            .Map(() => new SignInFailures(count, retryAt));
}

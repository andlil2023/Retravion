namespace Retravion.Identity.Domain;

/// <summary>Consecutive credential failures and the earliest permitted retry.</summary>
public sealed record SignInFailures
{
    public static SignInFailures None { get; } = new(0, DateTimeOffset.MinValue);

    public int Count { get; }
    public DateTimeOffset RetryAt { get; }

    private SignInFailures(int count, DateTimeOffset retryAt)
    {
        Count = count;
        RetryAt = retryAt;
    }

    public bool CanAttempt(DateTimeOffset now) => now >= RetryAt;

    public SignInFailures RecordFailure(DateTimeOffset now)
    {
        var count = Count == int.MaxValue ? Count : Count + 1;
        return new(count, count >= 3 ? now.AddSeconds(5) : DateTimeOffset.MinValue);
    }
}

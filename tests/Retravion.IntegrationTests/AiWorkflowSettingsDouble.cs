using CSharpFunctionalExtensions;
using Retravion.Application.AiWorkflow;
using Retravion.Application.AuditTrail;

namespace Retravion.IntegrationTests;

internal sealed class InMemoryAiWorkflowSettingsStore(bool enabled = false) : IAiWorkflowSettingsStore
{
    internal AiWorkflowSettings Current = new(enabled, 1);
    internal bool Unavailable;
    internal int Saves;
    public Task<Result<AiWorkflowSettings>> Load(CancellationToken token = default) => Task.FromResult(Unavailable
        ? Result.Failure<AiWorkflowSettings>("Settings unavailable.") : Result.Success(Current));
    public Task<Result<AiWorkflowSettings>> Save(AiWorkflowSettings expected, bool value, AuditActor actor, DateTimeOffset time, CancellationToken token = default)
    {
        Saves++;
        if (Unavailable || Current != expected) return Task.FromResult(Result.Failure<AiWorkflowSettings>("Settings unavailable or stale."));
        if (Current.Enabled != value) Current = new(value, Current.Revision + 1);
        return Task.FromResult(Result.Success(Current));
    }
}

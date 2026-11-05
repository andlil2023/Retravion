using CSharpFunctionalExtensions;

namespace Retravion.Domain.Revision;

public enum RevisionEntityKind { UserNeed, Requirement, TestCase }

/// <summary>An exact endpoint; versions of the same record remain distinct.</summary>
public sealed record RevisionSelection(RevisionEntityKind Kind, Guid InternalId, Guid VersionId, int VersionNumber)
{
    public Result Validate() => Enum.IsDefined(Kind) && InternalId != Guid.Empty && VersionId != Guid.Empty && VersionNumber > 0
        ? Result.Success() : Result.Failure("An exact entity/version identity and positive version number are required.");
}

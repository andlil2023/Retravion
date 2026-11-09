using Retravion.Application.Revision;
using Retravion.Domain.Revision;

namespace Retravion.Web.Features.Revision;

internal static class RevisionPresentation
{
    internal static string KindLabel(RevisionEntityKind kind) => kind switch
    { RevisionEntityKind.UserNeed => "User Need", RevisionEntityKind.Requirement => "Requirement", _ => "Test Case" };
    internal static string Label(RevisionBaselineMember member) => $"{KindLabel(member.Endpoint.Kind)} {member.Identifier} · v{member.Endpoint.VersionNumber}";
    internal static string State(string state) => state == "InReview" ? "In Review" : state;
    internal static string EndpointPath(RevisionSelection endpoint) => $"/{(endpoint.Kind == RevisionEntityKind.UserNeed ? "user-needs" : endpoint.Kind == RevisionEntityKind.Requirement ? "requirements" : "test-cases")}/details?id={endpoint.InternalId}&version={endpoint.VersionId}";
    internal static string WorkspacePath(string product, Guid id) => $"/products/links?kind=revision&product={Uri.EscapeDataString(product)}&id={id}";
    internal static string DetailsPath(string product, Guid id) => $"/revisions/details?product={Uri.EscapeDataString(product)}&id={id}";
    internal static string Actor(string identifier) => Guid.TryParse(identifier, out _) ? "Recorded user (identity in details)" : identifier;
    internal static string Action(string action) => action switch
    {
        "revision.create" => "Draft created", "revision.copy" => "Copied from Released revision", "revision.edit" => "Details edited",
        "revision.selection.add" => "Record selected", "revision.selection.remove" => "Selection removed", "revision.selection.replace" => "Selection replaced",
        "revision.release" => "Revision released", "revision.delete" => "Draft deleted", _ => action
    };
}

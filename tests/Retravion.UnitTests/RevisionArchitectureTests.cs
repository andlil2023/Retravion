using Retravion.Domain.Revision;
using Xunit;

namespace Retravion.UnitTests;

public sealed class RevisionArchitectureTests
{
    [Fact]
    public void RevisionFeatureKeepsLayerBoundariesAndDraftAndReleasedOperationsSeparate()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Retravion.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Retravion.Web", "Features", "Revision")))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Retravion.Infrastructure", source); Assert.DoesNotContain("Npgsql", source);
            Assert.DoesNotContain("Retravion.Identity.Domain", source);
        }
        Assert.DoesNotContain(typeof(DraftRevision).GetProperties(), p => p.SetMethod?.IsPublic == true);
        Assert.DoesNotContain(typeof(DraftRevision).GetMethods(), m => m.Name is "Release" or "CopyFromReleased" or "Reopen");
        Assert.True(typeof(Retravion.Domain.Revision.Revision).IsAbstract);
        Assert.DoesNotContain(typeof(ReleasedRevision).GetMethods(), m => m.Name is "Edit" or "ChangeSelection" or "Reopen");
        Assert.DoesNotContain(typeof(ReleasedRevision).GetProperties(), p => p.SetMethod?.IsPublic == true);
    }
}

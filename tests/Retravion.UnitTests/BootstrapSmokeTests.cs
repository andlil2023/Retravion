using System.Reflection;
using Xunit;

namespace Retravion.UnitTests;

public sealed class BootstrapSmokeTests
{
    [Fact]
    public void DomainAssemblyLoadsWithoutDependingOnOtherRetravionAssemblies()
    {
        var domain = Assembly.Load("Retravion.Domain");

        Assert.Equal("Retravion.Domain", domain.GetName().Name);
        Assert.DoesNotContain(domain.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Retravion.", StringComparison.Ordinal) == true);
    }
}

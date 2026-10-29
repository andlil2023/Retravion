using System.Xml.Linq;
using Xunit;

namespace Retravion.UnitTests;

public sealed class ProductArchitectureTests
{
    [Theory]
    [InlineData("Retravion.Domain", new string[0])]
    [InlineData("Retravion.Application", new[] { "Retravion.Domain" })]
    [InlineData("Retravion.Infrastructure", new[] { "Retravion.Application" })]
    public void ProductLayersKeepExistingDependencyDirection(string name, string[] expected)
    {
        var root = Root();
        var project = XDocument.Load(Path.Combine(root, "src", name, name + ".csproj"));
        Assert.Equal(expected, project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!)).ToArray());
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.Equal(name == "Retravion.Infrastructure" ? ["Npgsql"] : new[] { "CSharpFunctionalExtensions" },
            project.Descendants("PackageReference").Select(package => (string)package.Attribute("Include")!).ToArray());
    }

    [Fact]
    public void ProductUiDoesNotDependOnInfrastructureOrIdentityDomain()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Retravion.Web", "Features", "Product"), "*", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Retravion.Infrastructure", source);
            Assert.DoesNotContain("Retravion.Identity.Domain", source);
            Assert.DoesNotContain("Npgsql", source);
        }
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Retravion.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}

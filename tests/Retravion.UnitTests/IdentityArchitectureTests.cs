using System.Xml.Linq;
using Xunit;

namespace Retravion.UnitTests;

public sealed class IdentityArchitectureTests
{
    [Theory]
    [InlineData("Retravion.Identity.Domain", new string[0])]
    [InlineData("Retravion.Identity.Application", new[] { "Retravion.Identity.Domain" })]
    [InlineData("Retravion.Identity.Infrastructure", new[] { "Retravion.Identity.Application" })]
    public void IdentityProjectsHaveOnlyApprovedDependencies(string projectName, string[] expectedReferences)
    {
        var project = LoadProject(projectName);
        var references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("Microsoft.NET.Sdk", (string?)project.Root?.Attribute("Sdk"));
        Assert.Equal(expectedReferences, references);
        var packages = project.Descendants("PackageReference").ToArray();
        var expectedPackages = projectName == "Retravion.Identity.Infrastructure"
            ? Array.Empty<string>()
            : new[] { "CSharpFunctionalExtensions" };

        Assert.Equal(expectedPackages, packages.Select(package => (string?)package.Attribute("Include")).ToArray());
        Assert.All(packages, package =>
        {
            Assert.Null(package.Attribute("Version"));
            Assert.Null(package.Attribute("VersionOverride"));
        });
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    [Fact]
    public void WebReferencesOnlyIdentityApplicationAndInfrastructure()
    {
        var references = LoadProject("Retravion.Web").Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!))
            .Where(name => name.StartsWith("Retravion.Identity.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Retravion.Identity.Application", "Retravion.Identity.Infrastructure" }, references);
    }

    [Fact]
    public void SolutionIncludesAllIdentityProjects()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryRoot(), "Retravion.slnx"));
        var paths = solution.Descendants("Project").Select(project => (string?)project.Attribute("Path")).ToArray();

        foreach (var name in new[] { "Domain", "Application", "Infrastructure" })
        {
            Assert.Contains($"src/Retravion.Identity.{name}/Retravion.Identity.{name}.csproj", paths);
        }
    }

    private static XDocument LoadProject(string name) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src", name, $"{name}.csproj"));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Retravion.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Retravion.slnx for project-reference checks.");
    }
}

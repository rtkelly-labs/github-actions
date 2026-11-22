using System.IO.Compression;
using System.Text;

namespace EstateActions.Tests;

public sealed class ReleaseCheckArtifactsTests : IDisposable
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Plan = """[{"key":"Demo","package_id":"Demo.Pkg","version":"1.2.0","tag":"Demo/v1.2.0","prerelease":false,"baseline":"","notes":"n"}]""";

    private readonly string _root = Directory.CreateTempSubdirectory("estate-actions-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private TestHost Host(string plan = Plan)
    {
        var host = new TestHost();
        host.Inputs["RELEASE_PLAN"] = plan;
        host.Inputs["RELEASE_SHA"] = Sha;
        host.Inputs["RELEASE_ARTIFACTS"] = _root;
        return host;
    }

    private void Package(string relative, string? commit, string nuspecName = "Demo.Pkg.nuspec")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(nuspecName).Open(), Encoding.UTF8))
            writer.Write(FakeNuGet.Nuspec("Demo.Pkg", "1.2.0", commit));
        using (var writer = new StreamWriter(zip.CreateEntry("lib/net10.0/Demo.Pkg.dll").Open()))
            writer.Write("not really a dll");
    }

    private static Task<int> Check(TestHost host) => Run.Command(Commands.CheckArtifacts, host, new FakeGitHub());

    [Fact]
    public async Task Exactly_the_planned_packages_built_from_the_commit_pass()
    {
        Package("Demo/Demo.Pkg.1.2.0.nupkg", Sha);
        Package("Demo/Demo.Pkg.1.2.0.snupkg", null);
        var host = Host();

        Assert.Equal(0, await Check(host));
        Assert.Empty(host.Errors);
    }

    [Fact]
    public async Task An_extra_package_anywhere_in_the_tree_fails()
    {
        Package("Demo/Demo.Pkg.1.2.0.nupkg", Sha);
        Package("Demo/Demo.Pkg.1.2.0.snupkg", null);
        Package("Other/Other.1.0.0.nupkg", Sha);
        var host = Host();

        Assert.Equal(1, await Check(host));
        Assert.Contains("expected exactly [Demo/Demo.Pkg.1.2.0.nupkg, Demo/Demo.Pkg.1.2.0.snupkg]", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_symbols_package_fails()
    {
        Package("Demo/Demo.Pkg.1.2.0.nupkg", Sha);

        Assert.Equal(1, await Check(Host()));
    }

    [Fact]
    public async Task A_missing_artifacts_directory_fails()
    {
        var host = Host();
        host.Inputs["RELEASE_ARTIFACTS"] = Path.Combine(_root, "nope");

        Assert.Equal(1, await Check(host));
    }

    [Fact]
    public async Task A_package_built_from_another_commit_fails()
    {
        Package("Demo/Demo.Pkg.1.2.0.nupkg", "2222222222222222222222222222222222222222");
        Package("Demo/Demo.Pkg.1.2.0.snupkg", null);
        var host = Host();

        Assert.Equal(1, await Check(host));
        Assert.Equal(
            $"Demo/Demo.Pkg.1.2.0.nupkg records repository commit '2222222222222222222222222222222222222222', not {Sha}; release-finalize could not match it to its draft.",
            Assert.Single(host.Errors));
    }

    [Fact]
    public async Task A_package_that_records_no_commit_fails()
    {
        Package("Demo/Demo.Pkg.1.2.0.nupkg", null);
        Package("Demo/Demo.Pkg.1.2.0.snupkg", null);

        Assert.Equal(1, await Check(Host()));
    }

    [Fact]
    public async Task A_file_that_is_not_a_package_fails_cleanly()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Demo/Demo.Pkg.1.2.0.nupkg"), "not a zip", TestContext.Current.CancellationToken);
        Package("Demo/Demo.Pkg.1.2.0.snupkg", null);
        var host = Host();

        Assert.Equal(1, await Check(host));
        Assert.Contains("isn't a readable package", Assert.Single(host.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""[{"key":"Demo"}]""")]
    public async Task An_unusable_plan_fails(string plan)
    {
        var host = Host(plan);

        Assert.Equal(1, await Check(host));
        Assert.Single(host.Errors);
    }
}

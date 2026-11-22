using System.Text.RegularExpressions;

namespace EstateActions.Tests;

/// <summary>Checks the composite action definitions and workflows that ship the binary.</summary>
public sealed partial class ActionDefinitionTests
{
    private static readonly string[] Actions = [Commands.Draft, Commands.Finalize, Commands.CheckArtifacts];

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "dist", "checksums.txt")))
                return dir.FullName;
        }
        throw new InvalidOperationException("The repository root (with dist/checksums.txt) wasn't found.");
    }

    private static string Definition(string action) => File.ReadAllText(Path.Combine(Root, action, "action.yml"));

    /// <summary>The text of every <c>run: |</c> block, by its indentation.</summary>
    private static List<string> RunBlocks(string yaml)
    {
        var blocks = new List<string>();
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = RunKey().Match(lines[i]);
            if (!match.Success)
                continue;
            var indent = match.Groups[1].Length;
            var body = new List<string>();
            for (i++; i < lines.Length && (lines[i].Length == 0 || lines[i].Length - lines[i].TrimStart().Length > indent); i++)
                body.Add(lines[i]);
            i--;
            blocks.Add(string.Join('\n', body).TrimEnd());
        }
        return blocks;
    }

    [Fact]
    public void Every_release_action_runs_the_same_bootstrap()
    {
        var bootstraps = Actions.Select(a => Assert.Single(RunBlocks(Definition(a)))).ToList();

        Assert.All(bootstraps, b => Assert.Equal(bootstraps[0], b));
        Assert.Contains("exec \"$bin\" \"$ESTATE_ACTIONS_COMMAND\"", bootstraps[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Commands.Draft)]
    [InlineData(Commands.Finalize)]
    [InlineData(Commands.CheckArtifacts)]
    public void Each_action_runs_its_own_command(string action)
    {
        Assert.Contains($"ESTATE_ACTIONS_COMMAND: {action}\n", Definition(action).Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void No_run_block_expands_an_expression_inside_the_script()
    {
        var files = Actions.Select(a => Path.Combine(Root, a, "action.yml"))
            .Concat(Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml"));
        foreach (var file in files)
        {
            foreach (var block in RunBlocks(File.ReadAllText(file)))
                Assert.False(block.Contains("${{", StringComparison.Ordinal), $"{file} puts an expression in a script; pass it through env.");
        }
    }

    [Fact]
    public void Every_external_action_is_pinned_to_a_full_commit()
    {
        var files = Directory.GetFiles(Root, "action.yml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal) || f.Contains(".github", StringComparison.Ordinal))
            .Concat(Directory.GetFiles(Path.Combine(Root, ".github", "workflows"), "*.yml"));
        foreach (var file in files)
        {
            foreach (Match uses in Uses().Matches(File.ReadAllText(file)))
            {
                var target = uses.Groups[1].Value;
                if (target.StartsWith("./", StringComparison.Ordinal))
                    continue;
                Assert.Matches(PinnedUses(), target);
            }
        }
    }

    [Fact]
    public void The_committed_checksums_name_the_binaries_and_where_they_come_from()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "dist", "checksums.txt"));

        Assert.Contains("# repository: rtkelly-labs/github-actions", lines);
        Assert.Contains(lines, l => l.StartsWith("# release:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("# source:", StringComparison.Ordinal));
        foreach (var entry in lines.Where(l => l.Length > 0 && l[0] != '#'))
            Assert.Matches(ChecksumLine(), entry);
    }

    [GeneratedRegex(@"^(\s*)(?:- )?run: [|>]")]
    private static partial Regex RunKey();

    [GeneratedRegex(@"^\s*(?:- )?uses:\s*([^\s#]+)", RegexOptions.Multiline)]
    private static partial Regex Uses();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}\z")]
    private static partial Regex PinnedUses();

    [GeneratedRegex(@"^[0-9a-f]{64} \*?estate-actions-(linux-x64|linux-arm64|osx-arm64|win-x64\.exe)\z")]
    private static partial Regex ChecksumLine();
}

using System.Security.Cryptography;
using System.Text;

namespace EstateActions;

/// <summary>What a command needs from the runner: inputs, annotations, the step summary and step outputs.</summary>
internal interface IActionHost
{
    /// <summary>An input, passed to the binary by environment variable (never by argument).</summary>
    string? Get(string name);

    void Error(string message);

    void Info(string message);

    void Summary(string markdown);

    void Output(string name, string value);
}

/// <summary>A failure the command reports as one <c>::error::</c> annotation, then exits 1.</summary>
internal sealed class ActionFailure(string message) : Exception(message);

internal static class ActionHostExtensions
{
    public static string Require(this IActionHost host, string name) =>
        host.Get(name) is { Length: > 0 } value ? value : throw new ActionFailure($"{name} isn't set.");

    public static bool RequireBool(this IActionHost host, string name, bool? fallback = null)
    {
        var value = host.Get(name);
        if (string.IsNullOrEmpty(value) && fallback is { } f)
            return f;
        return value switch
        {
            _ when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
            _ when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            _ => throw new ActionFailure($"{name} must be 'true' or 'false', not '{value}'."),
        };
    }

    /// <summary>The summary line is also printed, as the bash steps did with <c>tee</c>.</summary>
    public static void Report(this IActionHost host, string line)
    {
        host.Info(line);
        host.Summary(line + "\n");
    }
}

/// <summary>The GitHub Actions runner: environment variables and the files it names.</summary>
internal sealed class RunnerHost : IActionHost
{
    public string? Get(string name) => Environment.GetEnvironmentVariable(name);

    public void Error(string message) => Console.Out.WriteLine($"::error::{EscapeData(message)}");

    public void Info(string message) => Console.Out.WriteLine(message);

    public void Summary(string markdown) => AppendTo("GITHUB_STEP_SUMMARY", markdown);

    public void Output(string name, string value)
    {
        if (!value.Contains('\n', StringComparison.Ordinal) && !value.Contains('\r', StringComparison.Ordinal))
        {
            AppendTo("GITHUB_OUTPUT", $"{name}={value}\n");
            return;
        }
        var delimiter = "EOF_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        AppendTo("GITHUB_OUTPUT", $"{name}<<{delimiter}\n{value}\n{delimiter}\n");
    }

    /// <summary>Workflow-command data escaping, so untrusted text (a tag, an API message) can't end the annotation.</summary>
    public static string EscapeData(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal);

    private static void AppendTo(string variable, string text)
    {
        // Outside a runner (a local run) these files aren't set; the text was already printed where it matters.
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path)
            File.AppendAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

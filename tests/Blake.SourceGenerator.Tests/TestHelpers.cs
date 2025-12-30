using System.Text.RegularExpressions;

namespace Blake.SourceGenerator.Tests;

public static class TestHelpers
{
    public static string NormalizeOutput(string output)
    {
        // Remove timestamps
        output = Regex.Replace(
            output,
            @"^// Generated at:.*$",
            "// Generated at: TIMESTAMP",
            RegexOptions.Multiline);

        // Remove #blake-line comments used for LSP position mapping
        // Handle two cases:
        // 1. Comment with newline before: \n// #blake-line N\n → \n
        // 2. Comment at start of line: ^// #blake-line N\n → (empty)
        output = Regex.Replace(
            output,
            @"(?:\r?\n)?// #blake-line \d+\r?\n",
            (m) => m.Value.StartsWith("\n") || m.Value.StartsWith("\r") ? "\n" : "",
            RegexOptions.Multiline);

        return output;
    }
}

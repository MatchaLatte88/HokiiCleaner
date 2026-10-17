using Cleanii.Core.Diagnostics;

namespace Cleanii.Tests;

public class StartupParsingTests
{
    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x04, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x01, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    public void Approval_uses_the_lowest_bit_of_the_first_byte(byte[] data, bool expected)
    {
        Assert.Equal(expected, StartupApproval.IsEnabled(data));
    }

    [Fact]
    public void Missing_approval_value_means_enabled()
    {
        Assert.True(StartupApproval.IsEnabled(null));
        Assert.True(StartupApproval.IsEnabled(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" -silent", "C:\\Program Files\\App\\app.exe")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\"", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe /background", "C:\\Tools\\tool.exe")]
    [InlineData("C:\\Tools\\tool.exe", "C:\\Tools\\tool.exe")]
    [InlineData("  \"C:\\x\\y.exe\"  ", "C:\\x\\y.exe")]
    [InlineData("\"C:\\unclosed\\a.exe /x", "C:\\unclosed\\a.exe /x")]
    public void ResolveExecutable_handles_quotes_and_arguments(string command, string expected)
    {
        Assert.Equal(expected, StartupCommandParser.ResolveExecutable(command, _ => false));
    }

    [Fact]
    public void ResolveExecutable_finds_unquoted_paths_with_spaces_when_the_file_exists()
    {
        const string full = @"C:\Program Files\App\app.exe";

        string? result = StartupCommandParser.ResolveExecutable(full + " --hidden", path => path == full);

        Assert.Equal(full, result);
    }

    [Fact]
    public void ResolveExecutable_expands_environment_variables()
    {
        string? result = StartupCommandParser.ResolveExecutable(@"%SystemRoot%\system32\cmd.exe /c", _ => false);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system32", "cmd.exe"), result, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveExecutable_returns_null_for_empty_command(string command)
    {
        Assert.Null(StartupCommandParser.ResolveExecutable(command));
    }

    [Fact]
    public void Exists_checks_absolute_paths_directly()
    {
        Assert.True(StartupCommandParser.Exists(@"C:\a\b.exe", p => p == @"C:\a\b.exe"));
        Assert.False(StartupCommandParser.Exists(@"C:\a\b.exe", _ => false));
    }

    [Fact]
    public void Exists_searches_the_path_for_bare_names_and_returns_unknown_when_not_found()
    {
        var directories = new[] { @"C:\Windows\System32" };

        Assert.True(StartupCommandParser.Exists("rundll32", p => p == @"C:\Windows\System32\rundll32.exe", directories));
        Assert.Null(StartupCommandParser.Exists("nothere", _ => false, directories));
        Assert.Null(StartupCommandParser.Exists(null));
    }
}

using HokiiCleaner.Core.Programs;

namespace HokiiCleaner.Tests;

public class UninstallCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT", @"C:\Program Files\Foo\unins000.exe", "/SILENT")]
    [InlineData("\"C:\\Program Files\\Foo\\uninstall.exe\"", @"C:\Program Files\Foo\uninstall.exe", "")]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789ABC}", "MsiExec.exe", "/X{12345678-1234-1234-1234-123456789ABC}")]
    [InlineData("C:\\Program Files\\Bar App\\uninst.exe /S /D=C:\\x", @"C:\Program Files\Bar App\uninst.exe", "/S /D=C:\\x")]
    [InlineData("  \"C:\\X\\u.EXE\"  -remove  ", @"C:\X\u.EXE", "-remove")]
    public void Parses_commands_that_start_an_exe(string command, string file, string arguments)
    {
        Assert.True(UninstallCommand.TryParse(command, out string parsedFile, out string parsedArguments));
        Assert.Equal(file, parsedFile);
        Assert.Equal(arguments, parsedArguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rundll32.dll setupapi.dll,InstallHinfSection DefaultUninstall 132 C:\\x.inf")]
    [InlineData("cmd /c del C:\\x")]
    [InlineData("\"unbalanced")]
    [InlineData("\"\"")]
    [InlineData("C:\\Program Files\\Foo\\remove.bat")]
    [InlineData("..\\relativ\\u.exe /S")]
    public void Rejects_everything_else_instead_of_guessing(string? command)
    {
        Assert.False(UninstallCommand.TryParse(command, out _, out _));
    }

    [Fact]
    public void Rejects_a_command_with_an_embedded_null_character()
    {
        Assert.False(UninstallCommand.TryParse("C:\\a\\u.exe\0 /S", out _, out _));
    }
}

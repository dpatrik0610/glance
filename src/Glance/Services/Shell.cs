using System.Diagnostics;

namespace Glance.Services;

public static class Shell
{
    public record Result(int Code, string Out, string Err);

    /// Installed, as a shell would find it.
    public static bool OnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => System.IO.File.Exists(System.IO.Path.Combine(dir, exe)));

    /// PowerShell 7 when installed, else the Windows PowerShell 5.1 every Windows has. Scripts run by it must suit both.
    public static string PowerShell => OnPath("pwsh.exe") ? "pwsh.exe" : "powershell.exe";

    /// Runs a console tool hidden and captures its output. Throws Win32Exception if the exe isn't installed.
    public static async Task<Result> Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return new(p.ExitCode, await stdout, await stderr);
    }
}

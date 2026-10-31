using System.Diagnostics;

namespace Glance.Services;

public static class Shell
{
    public record Result(int Code, string Out, string Err);

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

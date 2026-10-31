using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance.Services;

public record Container(string ID, string Names, string Image, string State, string Status, string Ports)
{
    public bool Running => State == "running";
    /// "0.0.0.0:5432->5432/tcp, [::]:5432->5432/tcp" → "5432→5432"
    public string Published => string.Join("  ", Docker.PortMap().Matches(Ports).Select(m => $"{m.Groups[1]}→{m.Groups[2]}").Distinct());
}

public static partial class Docker
{
    public enum State { Ok, NotRunning, NotInstalled }

    public static readonly string Desktop = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");

    public static async Task<(State, List<Container>)> Containers()
    {
        try
        {
            var r = await Shell.Run("docker", "ps", "-a", "--format", "{{json .}}");
            if (r.Code != 0) return (State.NotRunning, []); // daemon unreachable: Docker Desktop not started
            var list = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonSerializer.Deserialize<Container>(l)!)
                .OrderBy(c => !c.Running).ThenBy(c => c.Names).ToList();
            return (State.Ok, list);
        }
        catch (Win32Exception) { return (State.NotInstalled, []); }
    }

    /// start / stop / restart. Returns the error text, or null.
    public static async Task<string?> Do(string verb, string id)
    {
        var r = await Shell.Run("docker", verb, id);
        if (r.Code == 0) { Db.Info($"docker {verb} {id}"); return null; }
        return r.Err.Trim();
    }

    [GeneratedRegex(@":(\d+)->(\d+)")]
    internal static partial Regex PortMap();
}

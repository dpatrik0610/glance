using System.IO;

namespace Glance.Services;

/// Blame: which kind of heavy process to name as the likely cause.
public enum Blame { None, Cpu, Memory }

public record Alert(string Label, float Value, float Limit, string Unit, bool Above, bool Critical, Blame Blame);

/// Threshold alerts with debounce (N samples in a row), hysteresis (must drop clearly below to re-arm)
/// and a per-rule cooldown, so a hovering sensor can't spam.
public sealed class Alerts
{
    record Rule(string Key, string Label, string Unit, Func<Snapshot, float?> Value, Func<Settings, float> Limit, Blame Blame);

    static readonly Rule[] Rules =
    [
        new("cpuTemp", "CPU · Temperature", "°C", s => s.CpuTemp, c => c.CpuTempLimit, Blame.Cpu),
        new("gpuTemp", "GPU · Temperature", "°C", s => s.GpuTemp, c => c.GpuTempLimit, Blame.None),
        new("ram", "Memory · Usage", "%", s => s.RamUsed / s.RamTotal * 100, c => c.RamLimit, Blame.Memory),
    ];

    const int Streak = 3;           // consecutive samples over the limit
    const float Hysteresis = 5;     // units below the limit to re-arm
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10), DiskEvery = TimeSpan.FromMinutes(10);

    readonly Dictionary<string, int> streak = [];
    readonly HashSet<string> active = [];
    readonly Dictionary<string, DateTime> lastFired = [];
    DateTime nextDisk;

    public event Action<Alert>? Fired;

    public void Check(Snapshot s, Settings cfg, DateTime now)
    {
        if (!cfg.AlertsEnabled) return;
        foreach (var r in Rules) Eval(r.Key, r.Label, r.Unit, r.Value(s), r.Limit(cfg), true, Streak, now, r.Blame);

        if (now < nextDisk) return;
        nextDisk = now + DiskEvery;
        foreach (var d in DriveInfo.GetDrives())
            if (d.DriveType == DriveType.Fixed && d.IsReady)
                Eval("disk" + d.Name, $"Drive {d.Name.TrimEnd('\\')} · Free space", "%",
                    (float)(d.TotalFreeSpace * 100.0 / d.TotalSize), cfg.DiskFreeLimit, false, 1, now, Blame.None);
    }

    void Eval(string key, string label, string unit, float? value, float limit, bool above, int needed, DateTime now, Blame blame)
    {
        if (value is not { } v) return;
        if (above ? v >= limit : v <= limit)
        {
            streak[key] = streak.GetValueOrDefault(key) + 1;
            if (streak[key] < needed || !active.Add(key)) return;
            if (lastFired.TryGetValue(key, out var t) && now - t < Cooldown) return;
            lastFired[key] = now;
            Fired?.Invoke(new Alert(label, v, limit, unit, above, above ? v >= limit + 10 : v <= limit / 2, blame));
        }
        else
        {
            streak[key] = 0;
            if (above ? v < limit - Hysteresis : v > limit + Hysteresis) active.Remove(key);
        }
    }
}

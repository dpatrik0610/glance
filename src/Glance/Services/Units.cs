namespace Glance.Services;

/// Sizes and speeds as every page shows them.
public static class Units
{
    /// 1.5 GB, 340 KB. Binary units, as Explorer counts them.
    public static string Bytes(double b) => b switch
    {
        >= 1L << 40 => $"{b / (1L << 40):0.0} TB",
        >= 1L << 30 => $"{b / (1L << 30):0.0} GB",
        >= 1 << 20 => $"{b / (1 << 20):0.0} MB",
        >= 1 << 10 => $"{b / 1024:0} KB",
        _ => $"{b:0} B",
    };

    /// A process's memory: whole megabytes, or gigabytes with a decimal from 1 GB on.
    public static string Megabytes(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

    /// Network and disk throughput, in decimal units like Task Manager.
    public static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1e6 => $"{bytesPerSec / 1e6:0.0} MB/s",
        >= 1e3 => $"{bytesPerSec / 1e3:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };
}

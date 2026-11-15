using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glance.Services;

/// Per-app volume through Core Audio sessions on the default output. An app can have several sessions (a browser's
/// audio service); they move together. Media sessions carry no volume, so this is the way.
/// ponytail: sets the session's channel volumes (IChannelAudioVolume): this Windows build refuses ISimpleAudioVolume
/// on other apps' sessions, so the Volume mixer's own slider doesn't move, but what you hear does. Mute = level 0.
/// Runs on a pool (MTA) thread, so the session objects are used directly rather than through a UI-thread proxy.
public static class Audio
{
    static readonly ConcurrentDictionary<string, float> beforeMute = new(StringComparer.OrdinalIgnoreCase);

    public static (float Level, bool Muted)? Get(string exe) => Mta<(float, bool)?>(() =>
    {
        foreach (var v in Volumes(exe))
            try
            {
                var level = Level(v);
                return (level, level <= 0);
            }
            catch (COMException) { } // session ended in between
        return null;
    });

    public static void Set(string exe, float level) => Mta(() =>
    {
        foreach (var v in Volumes(exe)) SetLevel(v, Math.Clamp(level, 0, 1));
        return 0;
    });

    /// Muting remembers the level, so unmuting puts it back.
    public static void Mute(string exe, bool muted)
    {
        if (muted)
        {
            if (Get(exe) is { Level: > 0 } now) beforeMute[exe] = now.Level;
            Set(exe, 0);
        }
        else Set(exe, beforeMute.TryRemove(exe, out var was) ? was : 1);
    }

    static float Level(IChannelAudioVolume v)
    {
        v.GetChannelCount(out var n);
        var levels = new float[n];
        v.GetAllVolumes(n, levels);
        return n == 0 ? 0 : levels.Max();
    }

    static void SetLevel(IChannelAudioVolume v, float level)
    {
        try
        {
            v.GetChannelCount(out var n);
            var ctx = Guid.Empty;
            v.SetAllVolumes(n, Enumerable.Repeat(level, (int)n).ToArray(), ref ctx);
        }
        catch (COMException) { }
    }

    // A few milliseconds of COM calls; waiting for them keeps the callers simple.
    static T Mta<T>(Func<T> f) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA ? f() : Task.Run(f).GetAwaiter().GetResult();

    static List<IChannelAudioVolume> Volumes(string exe)
    {
        var found = new List<IChannelAudioVolume>();
        try
        {
            var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            devices.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o);
            ((IAudioSessionManager2)o).GetSessionEnumerator(out var sessions);
            sessions.GetCount(out var n);
            for (var i = 0; i < n; i++)
                try
                {
                    sessions.GetSession(i, out var s);
                    s.GetProcessId(out var pid);
                    if (pid == 0) continue; // system sounds
                    using var p = Process.GetProcessById((int)pid);
                    if (p.ProcessName.Equals(exe, StringComparison.OrdinalIgnoreCase)) found.Add((IChannelAudioVolume)s);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidCastException or COMException) { } // exited, or odd session
        }
        catch (COMException) { } // no output device, audio service restarting
        return found;
    }

    // ---- Core Audio COM interfaces (only up to the last method used; vtable order matters). Failures throw COMException. ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr groupingId, int flags, out IntPtr control); // IAudioSessionManager
        void GetSimpleAudioVolume(IntPtr groupingId, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName(out IntPtr name);
        void SetDisplayName(IntPtr name, IntPtr ctx);
        void GetIconPath(out IntPtr path);
        void SetIconPath(IntPtr path, IntPtr ctx);
        void GetGroupingParam(out Guid grouping);
        void SetGroupingParam(IntPtr grouping, IntPtr ctx);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        void GetSessionIdentifier(out IntPtr id);
        void GetSessionInstanceIdentifier(out IntPtr id);
        void GetProcessId(out uint pid);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IChannelAudioVolume
    {
        void GetChannelCount(out uint count);
        void SetChannelVolume(uint index, float level, ref Guid ctx);
        void GetChannelVolume(uint index, out float level);
        void SetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray)] float[] levels, ref Guid ctx);
        void GetAllVolumes(uint count, [MarshalAs(UnmanagedType.LPArray), Out] float[] levels);
    }
}

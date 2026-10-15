using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AISuitePanel;

/// <summary>
/// Ties every tool process the panel starts to the panel itself.
///
/// <para>
/// WHY. The panel shells out to python for every run (create / login / claim /
/// extra). Those children were never tied to it, and the tools deliberately
/// keep running until they finish - python does not die with its parent. Close
/// or kill the exe and the script stayed alive: a real case was
/// <c>claim_advanced.py --open</c> still running with a DEAD parent
/// (pid 22852, parent 17740 gone) and its Chrome still on screen, with no STOP
/// button anywhere to end it.
/// </para>
///
/// <para>
/// Two layers, because they cover different ways the panel can go away:
/// </para>
/// <list type="bullet">
///   <item><description>
///   a Windows <b>job object</b> with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
///   The moment the last handle to the job closes - normal exit, crash, or Task
///   Manager "End task" - the OS terminates every process in the job, and it
///   covers children of children, which is what takes the tool's browser down
///   with it. This is the layer that also survives a force-kill.
///   </description></item>
///   <item><description>
///   a plain registry of what we started, killed on <c>Application.Exit</c> as
///   a belt-and-braces path, and used by the STOP buttons to end one script's
///   whole tree on demand.
///   </description></item>
/// </list>
///
/// <para>
/// Never fatal: if the job cannot be created or the process cannot be assigned
/// (some hosts already run the panel inside a restrictive job), the registry
/// still does the job on a normal exit.
/// </para>
/// </summary>
internal static class ProcessGuard
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<Process>> Tracked = new();

    private static IntPtr _job = IntPtr.Zero;
    private static bool _jobTried;

    /// <summary>Hand a freshly started process to the guard.</summary>
    public static void Adopt(Process process)
    {
        if (process is null) return;

        lock (Gate)
        {
            Tracked.Add(new WeakReference<Process>(process));
            PruneLocked();
        }

        AssignToJob(process);
    }

    /// <summary>Stop tracking a process that has finished.</summary>
    public static void Forget(Process process)
    {
        if (process is null) return;
        lock (Gate)
        {
            for (var i = Tracked.Count - 1; i >= 0; i--)
            {
                if (Tracked[i].TryGetTarget(out var p) && ReferenceEquals(p, process))
                    Tracked.RemoveAt(i);
            }
        }
    }

    /// <summary>How many tracked processes are still alive.</summary>
    public static int LiveCount
    {
        get
        {
            lock (Gate)
            {
                PruneLocked();
                return Tracked.Count;
            }
        }
    }

    /// <summary>
    /// Kill every tracked process and its children. Called on application exit
    /// so closing the panel can never leave a python run (or the browser it
    /// opened) behind.
    /// </summary>
    public static void KillAll()
    {
        Process[] alive;
        lock (Gate)
        {
            alive = SnapshotLocked();
            Tracked.Clear();
        }
        foreach (var p in alive) KillTree(p);
    }

    /// <summary>Kill one process and everything it started.</summary>
    public static void KillTree(Process process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or access denied - nothing else we can do.
        }
        finally
        {
            Forget(process);
        }
    }

    // ---------------------------------------------------------------- internals

    private static void PruneLocked()
    {
        for (var i = Tracked.Count - 1; i >= 0; i--)
        {
            if (!Tracked[i].TryGetTarget(out var p) || SafeHasExited(p))
                Tracked.RemoveAt(i);
        }
    }

    private static Process[] SnapshotLocked()
    {
        var list = new List<Process>(Tracked.Count);
        foreach (var w in Tracked)
        {
            if (w.TryGetTarget(out var p) && !SafeHasExited(p))
                list.Add(p);
        }
        return list.ToArray();
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private static void AssignToJob(Process process)
    {
        EnsureJob();
        if (_job == IntPtr.Zero) return;

        try
        {
            // The panel holds the only handle to the job, so the job (and with it
            // every assigned process) dies with this process. Handle is kept for
            // the lifetime of the app on purpose - do NOT close it.
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch
        {
            // Restrictive host job, or the process already exited.
        }
    }

    private static void EnsureJob()
    {
        if (_jobTried) return;
        _jobTried = true;

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                             buffer, (uint)size))
                {
                    // Without the flag the job is pointless; fall back silently.
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            _job = job;
        }
        catch
        {
            _job = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ Win32

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

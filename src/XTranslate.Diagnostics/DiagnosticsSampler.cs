using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XTranslate.Diagnostics;

/// <summary>
/// Captures low-overhead, point-in-time diagnostics for the current process.
/// The sampler does not force GC, trim the working set, or touch pages.
/// </summary>
public sealed class DiagnosticsSampler : IDisposable
{
    private const int ErrorBadLength = 24;
    private const int ErrorInsufficientBuffer = 122;
    private const int InitialWorkingSetBufferBytes = 64 * 1024;
    private const int MaximumWorkingSetBufferBytes = 128 * 1024 * 1024;
    private readonly bool _includeModules;
    private readonly object _gate = new();
    private IntPtr _workingSetBuffer;
    private int _workingSetBufferBytes;
    private bool _disposed;

    /// <summary>
    /// If module enumeration was interrupted, this contains the reason and the
    /// returned list is only the stable prefix. A null value means enumeration
    /// completed or has not been attempted.
    /// </summary>
    public string? LastModuleCaptureFailureReason { get; private set; }

    public DiagnosticsSampler(bool includeModules = false)
    {
        _includeModules = includeModules;
    }

    public DiagnosticsSnapshot Capture()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var process = Process.GetCurrentProcess();
            process.Refresh();

            var workingSetBytes = process.WorkingSet64;
            var privateBytes = process.PrivateMemorySize64;
            var privateWorkingSet = QueryPrivateWorkingSet(process.Handle);

            int? gdi = null;
            int? user = null;
            string? guiFailure = null;
            (gdi, guiFailure) = ReadGuiResource(process.Handle, 0, "GDI", guiFailure);
            (user, guiFailure) = ReadGuiResource(process.Handle, 1, "USER", guiFailure);

            var gc = GC.GetGCMemoryInfo();
            return new DiagnosticsSnapshot(
                CapturedUtc: DateTimeOffset.UtcNow,
                WorkingSetBytes: workingSetBytes,
                PrivateBytes: privateBytes,
                PrivateWorkingSetBytes: privateWorkingSet.Bytes,
                PrivateWorkingSetFailureReason: privateWorkingSet.FailureReason,
                ManagedAllocatedEstimateBytes: GC.GetTotalMemory(false),
                GcCommittedBytes: gc.TotalCommittedBytes,
                HeapSizeBytes: gc.HeapSizeBytes,
                FragmentedBytes: gc.FragmentedBytes,
                GcIndex: gc.Index,
                Generation: gc.Generation,
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2),
                Threads: process.Threads.Count,
                Handles: process.HandleCount,
                GdiObjects: gdi,
                UserObjects: user,
                GuiResourceFailureReason: guiFailure,
                Modules: _includeModules ? CaptureModules(process) : null,
                PrivateWorkingSetMethod: "QueryWorkingSet / PSAPI_WORKING_SET_BLOCK.Shared (bit 8)",
                PrivateWorkingSetBufferBytes: privateWorkingSet.BufferBytes,
                GcSnapshotNote: "GC.GetGCMemoryInfo is a last-GC-event snapshot; Index 0 can mean no completed GC snapshot; no collection is forced.",
                ModuleCaptureFailureReason: _includeModules ? LastModuleCaptureFailureReason : null);
        }
    }

    /// <summary>
    /// Captures loaded-module metadata. The size is the virtual PE image size
    /// reported by the process module, not resident memory or disk size.
    /// </summary>
    public IReadOnlyList<ModuleSnapshot> CaptureModules()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var process = Process.GetCurrentProcess();
            return CaptureModules(process);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_workingSetBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_workingSetBuffer);
                _workingSetBuffer = IntPtr.Zero;
                _workingSetBufferBytes = 0;
            }
        }
    }

    private IReadOnlyList<ModuleSnapshot> CaptureModules(Process process)
    {
        LastModuleCaptureFailureReason = null;
        var modules = new List<ModuleSnapshot>();
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                modules.Add(new ModuleSnapshot(
                    module.ModuleName,
                    TryGetModulePath(module),
                    Math.Max(0, module.ModuleMemorySize)));
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            LastModuleCaptureFailureReason = $"Module enumeration failed: {error.GetType().Name}.";
        }

        return modules;
    }

    private static string? TryGetModulePath(ProcessModule module)
    {
        try { return module.FileName; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { return null; }
    }

    private static (int? Value, string? FailureReason) ReadGuiResource(IntPtr processHandle, uint kind, string name, string? previousFailure)
    {
        SetLastError(0);
        uint value;
        try
        {
            value = GetGuiResources(processHandle, kind);
        }
        catch (Exception error) when (error is Win32Exception or OverflowException)
        {
            return (null, $"{previousFailure}{name} GetGuiResources failed: {error.GetType().Name}. ");
        }

        var errorCode = Marshal.GetLastWin32Error();
        if (value == 0 && errorCode != 0)
            return (null, $"{previousFailure}{name} GetGuiResources failed (Win32 error {errorCode}). ");

        try { return (checked((int)value), previousFailure); }
        catch (OverflowException) { return (null, $"{previousFailure}{name} GetGuiResources returned an out-of-range value. "); }
    }

    private PrivateWorkingSetResult QueryPrivateWorkingSet(IntPtr processHandle)
    {
        var size = Math.Max(InitialWorkingSetBufferBytes, _workingSetBufferBytes);
        while (size <= MaximumWorkingSetBufferBytes)
        {
            try
            {
                EnsureWorkingSetBuffer(size);
                if (QueryWorkingSet(processHandle, _workingSetBuffer, size))
                {
                    var count = ReadEntryCount(_workingSetBuffer);
                    if (!TryGetEntryBytes(count, size, out _))
                        return new(null, "QueryWorkingSet returned an invalid entry count.", size);

                    var privatePages = CountPrivatePages(_workingSetBuffer, count);
                    return new(checked((long)privatePages * Environment.SystemPageSize), null, size);
                }

                var error = Marshal.GetLastWin32Error();
                var requestedCount = ReadEntryCount(_workingSetBuffer);
                if (error is not (ErrorBadLength or ErrorInsufficientBuffer))
                    return new(null, $"QueryWorkingSet failed (Win32 error {error}).", null);

                var requestedBytes = requestedCount > 0 && requestedCount <= (ulong)(int.MaxValue / IntPtr.Size)
                    ? checked(IntPtr.Size + (int)requestedCount * IntPtr.Size)
                    : 0;
                var next = Math.Max(size * 2, requestedBytes);
                if (next <= size) return new(null, "QueryWorkingSet buffer size overflowed.", null);
                size = Math.Min(MaximumWorkingSetBufferBytes + 1, next);
            }
            catch (Exception error) when (error is ExternalException or OverflowException or OutOfMemoryException)
            {
                return new(null, $"QueryWorkingSet failed: {error.GetType().Name}.", null);
            }
        }

        return new(null, "QueryWorkingSet requires an unexpectedly large buffer.", null);
    }

    private void EnsureWorkingSetBuffer(int size)
    {
        if (size <= _workingSetBufferBytes) return;
        var replacement = Marshal.AllocHGlobal(size);
        if (_workingSetBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_workingSetBuffer);
        _workingSetBuffer = replacement;
        _workingSetBufferBytes = size;
    }

    private static int CountPrivatePages(IntPtr buffer, ulong count)
    {
        if (count > int.MaxValue) throw new OverflowException("Working-set entry count is too large.");
        var entries = checked((int)count);
        var privatePages = 0;
        for (var index = 0; index < entries; index++)
        {
            var offset = checked(IntPtr.Size + index * IntPtr.Size);
            var flags = IntPtr.Size == 8
                ? unchecked((ulong)Marshal.ReadInt64(buffer, offset))
                : unchecked((uint)Marshal.ReadInt32(buffer, offset));
            if (!WorkingSetPageDecoder.IsShared(flags, IntPtr.Size * 8)) privatePages++;
        }

        return privatePages;
    }

    private static ulong ReadEntryCount(IntPtr buffer) => IntPtr.Size == 8
        ? unchecked((ulong)Marshal.ReadInt64(buffer))
        : unchecked((uint)Marshal.ReadInt32(buffer));

    private static bool TryGetEntryBytes(ulong count, int bufferSize, out int entryBytes)
    {
        entryBytes = 0;
        if (count > (ulong)(int.MaxValue / IntPtr.Size)) return false;
        entryBytes = checked(IntPtr.Size + (int)count * IntPtr.Size);
        return entryBytes <= bufferSize;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DiagnosticsSampler));
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(IntPtr hProcess, IntPtr pv, int cb);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void SetLastError(uint dwErrCode);

    private readonly record struct PrivateWorkingSetResult(long? Bytes, string? FailureReason, int? BufferBytes);
}

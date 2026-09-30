namespace XTranslate.Diagnostics;

/// <summary>A point-in-time process resource snapshot.</summary>
public sealed record DiagnosticsSnapshot(
    DateTimeOffset CapturedUtc,
    long WorkingSetBytes,
    long PrivateBytes,
    long? PrivateWorkingSetBytes,
    string? PrivateWorkingSetFailureReason,
    long ManagedAllocatedEstimateBytes,
    long GcCommittedBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long GcIndex,
    int Generation,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int Threads,
    int Handles,
    int? GdiObjects,
    int? UserObjects,
    string? GuiResourceFailureReason,
    IReadOnlyList<ModuleSnapshot>? Modules,
    string PrivateWorkingSetMethod,
    int? PrivateWorkingSetBufferBytes,
    string GcSnapshotNote,
    string? ModuleCaptureFailureReason);

/// <summary>
/// Metadata for a loaded module. SizeOfImageBytes is the module's virtual
/// image size, not its resident size and not its on-disk file length.
/// </summary>
public sealed record ModuleSnapshot(string Name, string? Path, long SizeOfImageBytes);

namespace StWorkbench.Core;

/// <summary>ESI engineering metadata. Support describes parsing coverage, never hardware readiness.</summary>
public sealed record EtherCatDeviceDescription(
    string? SourceSha256,
    IReadOnlyList<EtherCatPdoGroup> PdoGroups,
    IReadOnlyList<EtherCatSyncManager> SyncManagers,
    IReadOnlyList<EtherCatDcMode> DcModes,
    IReadOnlyList<EtherCatStartupCommand> StartupCommands,
    IReadOnlyList<string> UnsupportedFeatures)
{
    public bool IsDescriptionSupported => UnsupportedFeatures.Count == 0;
}
public sealed record EtherCatPdoGroup(ushort Index, string Name, bool IsInput, int? SyncManager, bool Fixed, bool Mandatory, IReadOnlyList<ushort> Excludes, bool DefaultSelected, IReadOnlyList<PdoEntry> Entries);
public sealed record EtherCatSyncManager(int Index, string Name, uint? StartAddress, int? DefaultSize, byte? ControlByte, bool? Enable, bool? Watchdog);
public sealed record EtherCatDcMode(string Name, ushort AssignActivate, EtherCatDcTiming? Sync0, EtherCatDcTiming? Sync1);
public sealed record EtherCatDcTiming(long Value, int? Factor, long? Shift);
/// <summary>DataHex is raw wire-order bytes, with no numeric endian conversion.</summary>
public sealed record EtherCatStartupCommand(string Transition, ushort Index, byte SubIndex, string DataHex, bool CompleteAccess);

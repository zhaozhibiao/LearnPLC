using System.Text.Json;
using StWorkbench.Core;

namespace StWorkbench.RuntimeMigration;

internal static class ContractChecks
{
    public static void Verify(LoadedProject migrated, EtherCatRuntimeCompilation compiled)
    {
        using var machine = JsonDocument.Parse(compiled.MachineJson);
        using var topology = JsonDocument.Parse(compiled.TopologyJson);
        void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
        var root = machine.RootElement;
        var axis = root.GetProperty("axes")[0];
        Check(root.GetProperty("requiredRuntimeCapabilities")[0].GetString() == "workbench_motion_migration_v1", "Missing motion capability requirement.");
        Check(axis.GetProperty("maximumJerk").GetDouble() == 20 && axis.GetProperty("softLimitPositive").GetDouble() == 2 && axis.GetProperty("maximumVelocity").GetDouble() == 1, "Production travel/velocity/jerk contract differs.");
        Check(axis.GetProperty("driveDemandProtection").GetBoolean() && axis.GetProperty("driveErrorOnly").GetBoolean() && !axis.GetProperty("demandDeliveryEnforced").GetBoolean(), "Protection policy differs.");
        var pdo = axis.GetProperty("pdoEntries");
        Check(pdo.GetProperty("positionDemand").GetString()!.EndsWith("0x6062:0") && pdo.GetProperty("followingError").GetString()!.EndsWith("0x60F4:0") && pdo.GetProperty("velocityOffset").GetString()!.EndsWith("0x60B1:0"), "PDO binding contract differs.");
        Check(root.GetProperty("cycleTiming").GetProperty("requiredGoodCycles").GetUInt32() == 1000, "Missing stable timing qualification.");
        Check(compiled.TopologyJson.Contains("670000", StringComparison.Ordinal), "Missing verified DC shift.");
        Check(migrated.Project.Hardware.Axes[0].Runtime!.DriveFollowingErrorWindowRaw == 10486, "Missing explicit drive startup window.");
    }
}

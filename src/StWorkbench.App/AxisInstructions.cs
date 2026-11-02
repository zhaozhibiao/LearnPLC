using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private IEnumerable<StInstruction> AxisInstructions() => AxisInstructionCatalog.Create(project.Hardware.Axes);
    private IEnumerable<StInstruction> AvailableInstructions() => PathInstructions().Concat(AxisInstructions()).Concat(BuiltinLibraryCatalog.CommunicationInstructions(project.Communication)).Concat(BuiltinLibraryCatalog.ControlInstructions(WorkbenchSettings.WorkspaceRoot, project.Tasks.FirstOrDefault(t => t.Enabled)?.IntervalMilliseconds / 1000.0 ?? 0.01)).Concat(StInstructionCatalog.All);
}

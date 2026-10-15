using StWorkbench.Core;

namespace StWorkbench.App;

public sealed partial class MainWindow
{
    private IEnumerable<StInstruction> AxisInstructions()
    {
        foreach (var axis in project.Hardware.Axes.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            StInstruction Block(string name, string label, string args, params string[] values) => new("Axis_" + axis.Name + "_" + name, name + " · " + axis.Name + " " + label, "工程轴", "绑定工程轴 " + axis.Name + " → " + axis.ControllerAxis + "。通过自有 EcMotion 服务执行；输入默认为不启动。实际反馈来自轴控工作进程。", "{p}(Axis := AXIS_" + axis.Name + (args.Length == 0 ? "" : ", " + args) + ");", ["{p} : " + name + ";", .. values], Stateful: true);
            yield return Block("MC_Power", "使能 / 关闭", "Enable := {p}Enable", "{p}Enable : BOOL := FALSE;");
            yield return Block("MC_Reset", "故障复位", "Execute := {p}Execute", "{p}Execute : BOOL := FALSE;");
            yield return Block("MC_Home", "回零", "Execute := {p}Execute", "{p}Execute : BOOL := FALSE;");
            yield return Block("MC_MoveAbsolute", "绝对定位", "Execute := {p}Execute, Position := {p}Position, Velocity := {p}Velocity", "{p}Execute : BOOL := FALSE;", "{p}Position : LREAL := 0.0;", "{p}Velocity : LREAL := 1.0;");
            yield return Block("MC_MoveVelocity", "速度运行 / 点动", "Execute := {p}Execute, Velocity := {p}Velocity", "{p}Execute : BOOL := FALSE;", "{p}Velocity : LREAL := 1.0;");
            yield return Block("MC_Stop", "受控停止", "Execute := {p}Execute", "{p}Execute : BOOL := FALSE;");
            yield return Block("MC_ReadAxisStatus", "实际状态", "Enable := TRUE");
            yield return Block("MC_ReadActualPosition", "实际位置", "Enable := TRUE");
            yield return Block("MC_ReadActualVelocity", "实际速度", "Enable := TRUE");
        }
    }
    private IEnumerable<StInstruction> AvailableInstructions() => PathInstructions().Concat(AxisInstructions()).Concat(StInstructionCatalog.All);
}

namespace StWorkbench.Core;

// Shared by desktop dragging, MCP insertion and generated examples.
public static class AxisInstructionCatalog
{
    public static IEnumerable<StInstruction> Create(IReadOnlyList<AxisConfiguration> axes, bool includeCompatibilityAliases = false)
    {
        foreach (var axis in axes.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            StInstruction Block(string name, string label, string args, string outputs, string behavior, params string[] values) => new("Axis_" + axis.Name + "_" + name, name + " · " + axis.Name + " " + label, "工程轴", "绑定工程轴 " + axis.Name + " → " + axis.ControllerAxis + "。通过自有 EcMotion 服务执行；输入默认为不启动。输入：Axis 及 " + args + "。直接读取实例输出：" + outputs + "。" + behavior, "{p}(Axis := AXIS_" + axis.Name + (args.Length == 0 ? "" : ", " + args) + ");", ["{p} : " + name + ";", .. values], Stateful: true);
            const string diagnosticOutputs = "{p}.Busy, {p}.Error, {p}.ErrorID";
            const string commandOutputs = "{p}.Done, " + diagnosticOutputs + ", {p}.Active, {p}.CommandAborted, {p}.RequestId";
            const string readOutputs = "{p}.Valid, " + diagnosticOutputs;
            const string executeBehavior = "Execute 上升沿提交一次并锁存输入；降低 Execute 不取消运行。终态保持到 Execute 释放，并至少可观察一个扫描周期。普通竞争命令会拒绝，不支持缓冲或混合。";
            const string readBehavior = "Enable 默认为 TRUE，直接读取实际反馈，不启动运动；仅 Valid 为 TRUE 时使用数据。新鲜的 ErrorStop 状态可读取，命令拒绝不使新鲜数据失效。";
            const string statusOutputs = ", {p}.Disabled, {p}.Standstill, {p}.Homing, {p}.DiscreteMotion, {p}.ContinuousMotion, {p}.SynchronizedMotion, {p}.Stopping, {p}.ErrorStop, {p}.Homed";
            yield return Block("MC_Power", "使能 / 关闭", "Enable := {p}Enable", "{p}.Status, " + diagnosticOutputs + ", {p}.CommandAborted, {p}.RequestId", "Enable 为电平控制；Status 是实际使能状态。失败或重连后不自动使能。", "{p}Enable : BOOL := FALSE;");
            yield return Block("MC_Reset", "故障复位", "Execute := {p}Execute", commandOutputs, executeBehavior, "{p}Execute : BOOL := FALSE;");
            yield return Block("MC_Home", "回零", "Execute := {p}Execute", commandOutputs, executeBehavior, "{p}Execute : BOOL := FALSE;");
            yield return Block("MC_MoveAbsolute", "绝对定位", "Execute := {p}Execute, Position := {p}Position, Velocity := {p}Velocity, Acceleration := {p}Acceleration, Deceleration := {p}Deceleration, Jerk := {p}Jerk", commandOutputs, executeBehavior, "{p}Execute : BOOL := FALSE;", "{p}Position : LREAL := 0.0;", "{p}Velocity : LREAL := 1.0;", "{p}Acceleration : LREAL := 0.0;", "{p}Deceleration : LREAL := 0.0;", "{p}Jerk : LREAL := 0.0;");
            yield return Block("MC_MoveVelocity", "速度运行 / 点动", "Execute := {p}Execute, Velocity := {p}Velocity, Acceleration := {p}Acceleration, Deceleration := {p}Deceleration, Jerk := {p}Jerk", "{p}.InVelocity, " + diagnosticOutputs + ", {p}.Active, {p}.CommandAborted, {p}.RequestId", "Execute 上升沿锁存速度请求；InVelocity 表示有效关联反馈已达到接受的速度，不代表定位完成。降低 Execute 不停止运行；使用 MC_Stop 停止。", "{p}Execute : BOOL := FALSE;", "{p}Velocity : LREAL := 1.0;", "{p}Acceleration : LREAL := 0.0;", "{p}Deceleration : LREAL := 0.0;", "{p}Jerk : LREAL := 0.0;");
            yield return Block("MC_Stop", "受控停止", "Execute := {p}Execute, Deceleration := {p}Deceleration, Jerk := {p}Jerk", commandOutputs, "Execute 上升沿提交停止；Done 表示实际停止完成。Execute 保持 TRUE 时锁定轴且 Busy 保持 TRUE；降低 Execute 释放锁定。", "{p}Execute : BOOL := FALSE;", "{p}Deceleration : LREAL := 0.0;", "{p}Jerk : LREAL := 0.0;");
            yield return Block("MC_ReadStatus", "实际状态", "Enable := TRUE", readOutputs + statusOutputs, readBehavior + "SynchronizedMotion 固定为 FALSE，当前不支持同步运动；Homed 是扩展反馈。");
            yield return Block("MC_ReadActualPosition", "实际位置", "Enable := TRUE", readOutputs + ", {p}.Position", readBehavior);
            yield return Block("MC_ReadActualVelocity", "实际速度", "Enable := TRUE", readOutputs + ", {p}.Velocity", readBehavior);
            if (includeCompatibilityAliases)
                yield return Block("MC_ReadAxisStatus", "实际状态（兼容名称）", "Enable := TRUE", readOutputs + statusOutputs, readBehavior + "新程序使用 MC_ReadStatus；Homed 是兼容扩展，SynchronizedMotion 固定为 FALSE。");
        }
    }
}

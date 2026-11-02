# 使用右侧指令库生成的工程轴示例

推荐使用新的[标准工程轴接口简洁示例](../standard-axis-interfaces/StandardInterfaces.stproj)。本工程保留为旧流程对照，包含较多应用层防护与诊断代码；新示例直接按功能块 Done/Error/CommandAborted 编排动作。

打开 **LibraryAxis.stproj**。此版本取代用于对照的手写实例示例：轴功能块声明、输入变量和调用语句均通过 MCP 的 `st_insert_instruction` 实际插入，并与桌面拖拽共用 `AxisInstructionCatalog`、`StSnippetInsertion`。

工程轴为 `X → Axis1`，单位 rev。功能块仍是工程站现有的 MC_* 库；没有另外实现运动功能块，也没有直接操作内部 AB_ 变量或 EtherCAT。

当前试转参数：默认 `Speed=0.5 rev/s`（30 rpm），允许指令速度最高 `1 rev/s`（60 rpm），加速度和减速度各 `1 rev/s²`，实速保护 `1.5 rev/s`（90 rpm）。这是测试工程的设置，不代表电机额定或最高转速；当前减速比为 1:1。

位置软限位仍为 `-2..+2 rev`，相对于回零后建立的工程坐标。每次正转 1.5 圈、反转 1 圈，净前进 0.5 圈；连续两轮后再启动第三轮时，正向目标会超过 +2 圈，示例会在运动前拒绝。连续旋转的正式应用应按机械需求重新配置行程或选择适当的旋转方式，不能以电机额定转速推导位置范围。

修改这些参数后，先下载硬件组态，再下载与之匹配的程序。仅修改 ST 中的 Speed 不会自动提高 Linux 核心已加载的工程限速；上传旧硬件组态也会将设备中的旧参数回读到当前工程。

## 与右侧指令库的对应关系

| 右侧指令 | 示例中生成的实例 |
|---|---|
| MC_Power · X | SnippetAxis_X_MC_Power1 |
| MC_Home · X | SnippetAxis_X_MC_Home1 |
| MC_Reset · X | SnippetAxis_X_MC_Reset1 |
| MC_Stop · X | SnippetAxis_X_MC_Stop1 |
| MC_MoveAbsolute · X，第 1 次插入 | SnippetAxis_X_MC_MoveAbsolute1 |
| MC_MoveAbsolute · X，第 2 次插入 | SnippetAxis_X_MC_MoveAbsolute2 |
| MC_ReadAxisStatus · X | SnippetAxis_X_MC_ReadAxisStatus1 |
| MC_ReadActualPosition · X | SnippetAxis_X_MC_ReadActualPosition1 |
| MC_ReadActualVelocity · X | SnippetAxis_X_MC_ReadActualVelocity1 |
| TON，3 次插入 | SnippetTON1 / SnippetTON2 / SnippetTON3 |
| R_TRIG | SnippetR_TRIG1 |

轴调用原样保留。例如 `SnippetAxis_X_MC_Power1(Axis := AXIS_X, Enable := SnippetAxis_X_MC_Power1Enable);`。
流程逻辑在调用之前给输入变量赋值。TON 的 PT 按需要设置为 2 秒、300 秒、2 秒，这是定时器参数配置。
CASE、IF 等负责组织顺序，不替代指令库的运动实现。

## 操作

1. 在工程中检查并应用匹配的轴组态；本次生成没有下载、应用组态或启动真实设备。
2. 运行并连接监视后，将 `PowerEnable` 写 TRUE，等待使能完成。
3. 将 `HomeExecute` 写 TRUE，等待生成的 MC_Home 实例 Done=TRUE 后写回 FALSE。
   当前工程继承现场的当前位置临时原点回零设置；机械、限位、反馈换算及保护参数在硬件组态中。
4. 等待 `Ready=TRUE`，将 `CycleExecute` 从 FALSE 写 TRUE：正转 1.5 圈，到位后等待 2 秒，再反转 1 圈，最终位于起点 +0.5 圈。
5. `StopExecute=TRUE` 请求受控停止。需要撤使能时 `PowerEnable=FALSE`。
   再次循环前将 CycleExecute 写 FALSE，Acknowledge 写 TRUE 再写 FALSE，等待 Ready 后再次启动。

功能块每周期调用，保持 CycleExecute=TRUE 不会重复启动。冷启动仅请求撤使能，绝不使能、回零或定位。
`Ready` 表示允许开始新循环，运动中变 FALSE 是正常的；不能拿它当运行中故障条件。运动中应观察 `CycleActive`、`Failed` 和各轴功能块的反馈。

故障诊断保留首次原因：`DiagnosticCode` 为首先触发停止的程序错误，`AxisErrorID` 为对应轴接口错误。停止失败或无法确认停稳时，另记录 `StopDiagnosticCode=7115` 和 `StopErrorID`，并置 `OutcomeUnknown=TRUE`，不再覆盖首次错误码。确认复位才会清除这些记录。
例如通道反馈丢失后停止也失败，将保留 `DiagnosticCode=7112`、`AxisErrorID=2004`，同时记录 `StopDiagnosticCode=7115`。旧示例只显示最终 7115，无法据此确定最初原因。
静止速度反馈波动问题未在此示例中修复；不能把本次内存测试当作真实试转已通过。

## 可核对证据

- `Main.st`：最终源码，与工程实际保存的不可变源码一致。
- `library-insertion-evidence.json`：13 次模板插入的 ID、生成实例、声明和调用；每次结果都与桌面插入引擎逐字比较。
- `mcp-final-evidence.json`：实际 MCP 工具调用记录。
- `build-final-evidence.json`：真实 ST 编译打包记录。
- `verification.json`：纯内存时序回归；测试没有设备连接。

复验生成：在仓库根目录执行 `dotnet run --project examples/library-axis-turn/mcp-client/Client.csproj -c Release -- D:/OPenplc`。先构建 MCP 到 `bin/library-axis-verify/`，保留原始 `examples/standard-axis-turn/Main.st` 作为流程对照输入。

本例程、生成工具与测试采用 MIT 许可证，依赖组件授权以项目授权说明为准。

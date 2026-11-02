# 工程轴 X：正转 1.5 圈、等待 2 秒、反转 1 圈

本目录保留早期手写调用版作为流程对照。需要与右侧指令库插入结果一致的版本，请打开 [LibraryAxis.stproj](../library-axis-turn/LibraryAxis.stproj)。

打开 `StandardAxis.stproj`。程序源码为 `Main.st`，保存工程内的不可变源码与此文件一致。

## 工程轴绑定

| 项目 | 配置 |
|---|---|
| 工程逻辑轴 | X |
| ST 引用 | AXIS_X（由工程站生成，不要手写轴序号） |
| 控制器轴 | Axis1 |
| 工程单位 | rev，一圈 = 1.0 |
| 软件行程 | -2～+2 rev |
| 例程默认速度 | 0.5 rev/s，即 30 rpm |
| 指令速度上限 | 1 rev/s，即 60 rpm |
| 加速度 / 减速度 | 各 1 rev/s² |
| 实速保护上限 | 1.5 rev/s，即 90 rpm |
| PLC 周期 | 10 ms |
| 总线周期 | 1 ms |

程序只调用工程轴功能块，不读写内部桥变量，不直接操作 EtherCAT/PDO。
机械传动、反馈分辨率、驱动器参数和回零方法均保存在硬件组态。此工程保留现场已验证的 IS620N/ISMH1 配置，逻辑轴名称统一为 X。
更换硬件时必须核对并应用对应的工程轴组态，不能只改 ST 名称。

## 按顺序操作

1. 检查硬件组态中 `X → Axis1` 的从站、单位、机械参数、限位及回零设置。
   首次使用此工程时执行“下载硬件”，使设备组态与工程匹配，然后编译、下载程序并连接在线监视。
2. 将 `PowerEnable` 写 TRUE，等待 `AxisPower.Status=TRUE`、`AxisPower.Busy=FALSE`。
3. 将 `HomeExecute` 写 TRUE，等待 `AxisHome.Done=TRUE`，再写 FALSE。
   本工程回零方式为现场已确认的“当前位置建立临时原点”，没有寻找原点开关的动作；其他设备必须在组态中设置自己的回零方法。
4. 等待 `Ready=TRUE`，将 `CycleExecute` 从 FALSE 写 TRUE。
   程序从实际起点正向走 1.5 圈，到位后等待 2 秒，再反向走 1 圈。
5. `Done=TRUE` 表示正常完成，最终位置是起点 +0.5 圈；`PowerEnable=TRUE` 时保持使能。
   需要撤使能时将 `PowerEnable` 写 FALSE。

所有功能块在每次 PLC 扫描中调用；CASE 只决定 Execute 和流程跳转。
循环开始时锁存目标和速度，执行期间改参数不改变已经提交的动作。
保持 `CycleExecute=TRUE` 不会重复运行。

## 停止与再次执行

- 将 `StopExecute` 写 TRUE，请求受控停止；取消完成后 `Cancelled=TRUE`、`Done=FALSE`。
  确认停止后将 StopExecute 写回 FALSE；普通受控停止不是急停。
- 再次执行前：CycleExecute 写 FALSE，Acknowledge 写 TRUE 清除流程结果，再写 FALSE；等待 Ready 后重新给 CycleExecute 上升沿。
- `ResetExecute` 对应 MC_Reset，仅用于轴故障复位；`Acknowledge` 只清除本例程流程状态。
- `AxisStatus.Valid`、`AxisPosition.Valid`、`AxisVelocity.Valid` 必须有效。
  Failed 或 OutcomeUnknown 时，先核对 DiagnosticCode 和各功能块 ErrorID，不会自动重试运动。
- 冷启动所有操作输入均为 FALSE。MC_Power 的首次 FALSE 调用只请求撤使能，不会使能、回零或定位。

## 流程与接口范围

Step：0 待命 → 10 正向定位 → 20 等待 → 30 反向定位 → 100 完成。
80 为停止处理中，900 为失败。循环总超时 300 秒，停止反馈超时 2 秒。

使用当前工程站提供的 MC_Power / MC_Home / MC_MoveAbsolute / MC_Stop / MC_Reset 等接口。
本工程采用工程轴和 PLCopen 风格功能块调用方式，并不表示当前运动库已覆盖或认证全部 PLCopen 规范。

## 已完成验证

通过实际 MCP 创建、组态、保存、检查、编译打包。
在 Linux 上编译实际生成的 C++，进行纯内存测试：冷启动、完整往返、非零起点、未回零拒绝、越限拒绝、反馈失效拒绝、等待阶段取消，共 7 项。
没有下载、激活或执行真实轴运动。证据见 build-final-evidence.json、verification.json。

复验：`python3 verify_sequence.py <MCP编译输出目录>`。测试不会访问轴服务或 EtherCAT。

本例程原创 ST 和测试代码采用 MIT 许可证，依赖组件的授权仍以项目授权说明为准。

# 基础轴控接口

本阶段把 ST 程序接到自有 EcMotion v4 协议。原工程树、ST 编辑器、硬件配置、编译下载和在线监视继续使用同一套流程。基础轴接口与连续路径接口、实时轴控周期配置分别检查就绪状态。

## 工程站操作

1. 在硬件配置中添加工程轴，例如逻辑名 `X`、控制器轴名 `Axis1`，填写单位、位置范围和适用速度上限。
2. 在右侧资源库展开“工程轴”，把需要的功能块拖入程序或功能块。工程站自动生成实例、输入变量和 `Axis := AXIS_X` 绑定；一次撤销可同时移除声明与调用。
3. 使能和运动触发变量默认为 FALSE。只读状态、位置和速度条目默认开启读取。功能块必须每个 PLC 扫描都调用；Execute 的上升沿提交一次请求，保持 TRUE 不会重复执行。
4. 沿用检查、生成候选和“下载 / 应用程序”流程：上传与 Linux 编译通过后，确认重启提示即自动应用。后端缺失、轴清单不匹配、能力文件过期、模拟与物理模式混淆都会阻止应用，不会停止当前程序。
5. 激活与运行仍是两个操作。只有活动版本一致时开启 ST 在线监视，直接观察实例的 Busy、Done、Error、ErrorID、Position 等反馈。

可打开 [示例工程](../examples/axis-control/AxisControl.stproj)。该工程配置 `X → Axis1`，包含九种功能块，所有运动触发变量初始为 FALSE。


推荐打开 [标准接口简洁示例](../examples/standard-axis-interfaces/StandardInterfaces.stproj)：正转 1.5 圈、等待 2 秒、反转 1 圈。程序只编写动作顺序，直接使用实例的 Done、Error、ErrorID、CommandAborted，不再复制到位容差、停止确认计时器或 71xx 诊断逻辑。该工程已通过实际 MCP 编译打包，未下载到设备。[旧版指令库示例](../examples/library-axis-turn/LibraryAxis.stproj)保留供比较。桌面和 MCP 共用同一份工程轴模板。

资源库顶部提供“配置工程轴”入口。未配置轴时显示配置提示，不再提供旧的“待接入”注释示例；路径调用也只从实际导入的工程路径生成。更新程序后，请保存工程并关闭旧窗口，使用 `启动ST工程站.cmd` 重新打开。先前插入源码的注释会保留，删除旧示例后重新拖入对应工程轴条目即可。

多个工程轴会各生成九条指令，名称标出对应轴，可滚动或搜索选择。资源库底部“说明 / 代码预览”以弹窗显示，不占用指令列表高度。顶部“已配置 N 个工程轴”指的是逻辑轴数量，不是 ESI 目录或工程从站数量；增加轴时逻辑名和控制器轴引用须分别唯一。添加失败会在组态页显示“未应用”及具体原因，原轴清单保留。

| 功能块 | 本阶段行为 |
|---|---|
| MC_Power | Enable 电平控制；实时 Status、Busy、Error、ErrorID；首次 FALSE 也提交关闭 |
| MC_Reset | 等待实际故障清除；持续故障有界超时 |
| MC_Home | 回零；物理模式必须证明命令已在原生周期应用 |
| MC_MoveAbsolute | 绝对定位；Done 必须对应本次 MotionId 和实际终态 |
| MC_MoveVelocity | 有符号速度运行；Busy 持续，InVelocity 比较提交时锁存速度 |
| MC_Stop | 受控停止；保持 Execute 时锁定该轴，释放后允许下一命令 |
| MC_ReadStatus | Valid、Busy、Error、ErrorID 和轴状态标志；Homed 为本工程扩展 |
| MC_ReadActualPosition | 实际位置及 Valid、Busy、Error、ErrorID |
| MC_ReadActualVelocity | 实际速度及 Valid、Busy、Error、ErrorID |

`MC_ReadAxisStatus` 保留为兼容名称，与 `MC_ReadStatus` 输出一致；新资源库默认使用后者，MCP 仍接受旧模板 ID。编辑器补全直接读取功能块的公开接口，内部 AB_ 仲裁变量不作为用户接口提供。

执行类功能块的 Busy 表示已接受并正在处理请求，Active 在后端确认执行后成立。Done、CommandAborted、Error 保持到 Execute 释放；执行途中提前释放 Execute 不取消运动，终态至少可观察一次调用。忙时重新触发普通运动不会改写已接受目标，输入在接受时锁存。所有实例必须每扫描调用。`MC_Stop` 在实际停止后 Done 成立；Execute 仍为 TRUE 时 Busy、Active 和轴锁保持，状态读取仍为 Stopping，释放后回到 Standstill。

读取类功能块的 Valid 表示反馈可用，轴本身 ErrorStop 或一次命令被拒绝不会让新鲜位置消失；通信、扫描看门狗、租约或协议失效则撤销 Valid。禁用读取会清除 Valid、Busy 和状态标志，位置数值可能保留，只有 Valid 时才能使用。MC_Power 的 Status 实时反映使能；故障与断联会报告 Error。关闭尚未完成时收到的新 Enable 上升沿只等待关闭成功确认，失败取消该请求，恢复通信或清除故障不会自动重新使能。

Stop/关闭可以抢占该轴，前一实例报告 CommandAborted。其他命令不能覆盖正在执行的命令。工作进程不自动重连、不重放未知结果。扫描停止、反馈过期或通信失败会锁存错误，并尝试停止自己拥有的轴和释放租约；控制服务断联时，停止请求不保证送达，依赖原控制服务的租约超时保护。

## Linux 接入

独立 Python 工作进程使用标准库，通过私有 Unix socket 连接既有 EcMotion 服务；不会启动、发现或安装运动控制器。控制器、工作进程和 Runtime 的权限/身份须匹配。私有轴 socket 的目录权限为 0700，socket 和能力文件为 0600。

设备 `a@192.168.1.31` 已安装工作进程及协调器，实际就绪状态必须每次读取，不能由安装状态推断。IS620N 独立测试记录见 `examples/is620n-test`；通用 ESI 配置流程见 [通用 EtherCAT 组态](generic-ethercat.md)。

使用候选包内的准确 `axis-manifest.json`，先明确选择后端：

```sh
python3 axes/axis_worker.py --manifest /private/candidate/axis-manifest.json \
  --socket /private/axis/axis.sock --controller-socket /private/controller/ecmotion.sock \
  --backend EcMotion
```

启动阶段只有 Hello 和只读快照，不获取控制租约。租约在实际 PLC 扫描提交有效命令后获取，只有递增的成功扫描续租。失败后应停止 PLC 并重新检查、重新建立工作进程，不能期待自动恢复运动。

部署协调器配置增加 `axis_backend: "EcMotion"` 和 `axis_socket_path: "/private/axis/axis.sock"`。Runtime 运行环境须配置同值的 `STWORKBENCH_AXIS_BACKEND`、`STWORKBENCH_AXIS_SOCKET`。协调器校验活进程、私有目录、能力文件时效和候选清单摘要；这些设置须通过设备部署过程配置，编辑 ST 工程不会隐式改变服务环境。

仿真明确使用 `EcMotionSimulator`，不会通过物理后端检查。没有匹配的已运行工作进程时，Linux 编译仍可成功，但界面显示基础轴控未就绪并禁止激活。实体设备接入仍须核对控制器协议、action receipts、轴名称、驱动回零与限位设置。通用组态候选还必须匹配实际原生核心加载的硬件指纹，才能将工程机械参数视为已经应用；旧候选不会因新增字段自动获得就绪状态。

## 当前范围

PLC 用户程序保持在原 PLC 周期内提交运动意图；实时轴循环继续由自有运动核心执行。扫描桥只做固定容量的数据复制与队列操作，通信在后台线程和工作进程执行。工作进程读取反馈的默认观察周期为 10 ms，不代表同步的 1 ms 硬件反馈。

本阶段没有把轴控周期配置应用到运动核心，也未实现物理连续加工轨迹或任意多轴同步。配置了轴控周期的候选仍会被对应检查阻止激活；基础轴接口就绪不能绕过它。轴实例目前只支持固定工程轴、标量实例和一个启用的 1–1000 ms PLC 周期，函数局部轴实例和动态 Axis 引用会被拒绝。

自研轴控代码使用 MIT；独立编译工具继续履行 GPL 义务，见 [授权记录](licenses.md)。这组 MC_ 功能块不宣称完整 PLCopen 认证。

验证入口：`scripts/verify.ps1` 包含工程站与 Python 回归测试；`tools/axis-bridge/verify-linux.sh` 检查真实生成的 ST 和原生传输；`linux/axis/integration_simulator.py`、`integration_candidate.py` 使用显式提供的原项目仿真器与独立候选，均不会激活生产 PLC 或连接实体轴。
# 机械参数的归属

减速比、驱动器反馈分辨率、直线导程、轴类型、旋转范围及回零配置集中在硬件组态中的轴属性。ST 指令保持工程轴引用和单次运动输入。动态参数v2已增加Acceleration、Deceleration、Jerk；它们只定义本次动作，不替代硬件组态的机械参数和硬上限。旋转最短方向等未支持功能仍被拒绝。

未启用通用运行组态时，显式机械属性仅用于工程保存、校验、换算预览及候选冻结，候选激活会拒绝尚未支持的应用方式。启用通用运行组态后，支持的机械、回零和保护字段写入原生核心配置，实际加载指纹通过验证后才允许激活。未支持的旋转模式等字段仍明确阻止应用。操作说明见 [通用组态](generic-ethercat.md)。


## 动态参数v2

`MC_MoveAbsolute`及`MC_MoveVelocity`支持Acceleration、Deceleration、Jerk；普通`MC_Stop`支持Deceleration、Jerk。0继承轴组态的明确默认值，负值、非有限值和超限请求拒绝；Busy期间修改输入不改变已接受动作。正的已配置Jerk不能用命令Jerk=0取消。

含动态输入的程序选择独立v2桥接；省略所有新增输入的旧程序保持v1。新旧接口不共用socket、不靠包长猜版本。ST编辑补全、右侧指令库和MCP元数据已同步；不需要工程中自写参数传输代码。

[实施和实机验收记录](axis-dynamics-v2-verification.md)，[v2接口与打包细节](axis-v2-engineering.md)。本次实机只验证低速小行程，不代表全部PLCopen符合性或高速性能认证。

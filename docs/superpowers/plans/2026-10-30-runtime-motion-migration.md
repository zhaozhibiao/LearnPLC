# 正式 Runtime 运动核心迁移计划

> 执行时按任务逐项记录结果，先离线验证再部署；不绕过工程包身份、硬件确认或现有权限边界。

**Goal:** 将已完成空载300、1000和3000 rpm平滑往返试验的运动能力迁移到正式工程站与Linux Runtime，工程编译、下载、在线、重启恢复均使用同一配置来源。

**Architecture:** Windows工程模型生成完整、带身份的硬件及程序包；Linux正式运动核心负责插补和机器保护，驱动器负责内部跟随保护，主站可选60F4辅助跟随监控。调试用固定延迟交付比较不作为默认故障来源。正式PLC、运动核心、轴worker、协调服务沿既有接口连接。

**Tech Stack:** C#/.NET工程模型与UI、C++ CiA402/IgH运动核心、Python部署协调与硬件broker、systemd。
## 实施前基线与迁移缺项（历史对照）

- 诊断核心二进制：`e7c390c70baf8f51280a04c12d3a8c5e46a5f7b07f912f41f231d3cf3ed4c476`；源码改动已整理到`linux/ethercat-i210/csp-timing/overlay`及哈希清单。
- 空载平滑往返至3000rpm通过，证据为`artifacts/complete-retest-20261030/high-speed-report.md`；这不是恒速耐久、带载或独立测速验收。
- 最近正式安装审计仍固定旧核心`ceae22f11a89…`。正式机器配置使用60FF、速度上限1rev/s、软限位±2rev、旧跟随参数；诊断配置使用60B1、6062、60F4、平滑规划及分层保护。不能只替换可执行文件。
- 工程模型目前没有完整输出`maximumJerk`、辅助保护模式、交付诊断策略、速度反馈/前馈单位和时序配置。
- 诊断保护仍限定固定台架单位、比例和门限，PDO解析也依赖专用键后缀；尚不是通用工程轴实现。
- 当前平滑算法仅静止起始的单轴MoveAbsolute使用五次轨迹；运动中请求可能回退旧规划，Stop/Jog/外部路径不具有相同加加速度语义。必须明确支持边界，不能宣称全部轴指令已平滑。
- 正式安装器、恢复服务、硬件版本目录、worker能力文件及工程候选身份尚未一起升级。
- PLC任务调度代码与独立构建已有验证，但正式运行版本未部署。它是独立迁移项，不能把更换运动核心说成PLC扫描调度也已升级。
## Task 1：现场盘点与可执行回滚

**Files:** 新建`linux/ethercat-i210/runtime-migration/preflight.py`、`install.py`、`rollback.py`；复用`apply-runtime-hardware.py`、`restore-runtime.py`。

- [x] 读取实际进程路径/二进制SHA、systemd单元、控制socket、正式及诊断配置、安装记录、active记录和协调服务状态；不输出认证密钥。
- [x] 确认轴已撤使能、位置稳定、PLC运行/停止状态；迁移保持既有PLC状态，切换核心不自动使能或启动运动。
- [x] 将原二进制、安装配置、硬件目录、active记录、服务配置及相关协调/worker版本备份到root控制的版本目录，生成逐文件哈希清单。
- [x] 回滚脚本仅恢复已备份版本，检查目标路径/权限/哈希并恢复原控制通道；默认以撤使能状态恢复。回滚不能自动恢复运动。
- [x] 单元测试验证备份缺失、哈希不符、符号链接、部分失败均终止；在隔离目录演练回滚。

**完成条件:** 有实际备份、安装前审计记录及可验证的回滚入口。
## Task 2：将诊断算法整理为正式可配置能力

**Files:** `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/{include/axis_feedback.hpp,include/motion_setpoint.hpp,src/axis_feedback.cpp,src/motion_setpoint.cpp}`及对应自测；`source-manifest.json`。

- [x] 先补失败测试：零/极短行程、正反向、接近限位、速度/加减速度/jerk边界、NaN/Inf/溢出、采样离散端点、同步与重置。
- [x] 补Stop/Disable/故障中断、反向请求、非零初速与运动重入测试。未实现的平滑组合明确拒绝或以文档定义的停止语义处理，消除静默回退。
- [x] 把固定台架条件改为经过范围验证的显式工程参数；计数转换和可达行程必须受int32边界约束。
- [x] 将原始位置差、驱动60F4、交付诊断及时序故障分开表示。默认不以固定历史逐计数匹配触发停机；不得因此关闭模式、反馈有效性、超速、限位、总线或时序检查。
- [x] 60F4辅助保护直接使用已声明的单位与有效状态；未映射、单位未知或固件能力不符时拒绝所选监控模式，不能按零误差处理。
- [x] 使PDO对象按工程绑定解析，支持正式键名；不依赖`Axis1Drive.Position_actual_value…`等诊断名称。
- [x] 驱动保护窗口、主站辅助窗口/时间与诊断策略分别配置；驱动器不支持6066时明确显示，不能假定存在20ms内部延时。
- [x] 用新测试验证20周期超差、失配仅诊断、实际反馈冻结、时序失效、限位/超速与故障复位；完整CMake/CTest通过并刷新哈希清单。

**完成条件:** 不是仅复制台架二进制，而是具备明确接口和边界的可复现发布构建。
## Task 3：工程模型、硬件组态与生成器贯通

**Files:** `src/StWorkbench.Core/EtherCatRuntimeConfiguration.cs`、`src/StWorkbench.App/AxisPropertiesWorkspace.cs`、`src/StWorkbench.App/HardwareWorkspace.cs`、工程序列化/MCP配置入口；`tests/StWorkbench.Core.Tests/EtherCatRuntimeTests.cs`及App/MCP相关测试。

- [x] 增加工程字段：平滑模式和jerk、驱动跟随窗口、主站辅助监控模式/窗口/时间、速度比例、前馈、交付诊断策略及运动时序参数。给旧工程明确兼容规则，不能静默启用新行为。
- [x] 上位机轴属性显示可理解的单位和作用范围；上传/下载、保存/重开及MCP往返保留字段。
- [x] 为已确认IS620N配置生成匹配实机的60B1、6062、60F4 PDO；通用CiA402保持能力检查，不向不支持设备强塞专用映射。
- [x] 通过工程配置生成6065启动SDO，并读回确认；默认不写EEPROM保存对象。保留旧值供回滚。
- [x] DC偏移670us仅作为当前已验证设备配置，不硬编码为所有设备的默认值；输出周期和时序裕量配置。
- [x] 使用工程轴原有速度/限位，禁止把诊断±1000圈、55rev/s等测试上限直接作为生产默认。
- [x] 测试：新旧工程读写、单位换算、缺PDO拒绝、SDO生成、硬件上传重建工程与下载前校验。

**完成条件:** 从工程文件重新生成的配置完整表达已验收策略，不依赖SSH手改JSON。
## Task 4：候选包与正式轴接口一致

**Files:** `src/StWorkbench.Toolchain`包生成相关实现、`linux/deployment/{axis_gate.py,ethercat_hardware.py,configuration_download.py,coordinator.py}`、`linux/axis/{axis_worker.py,test_axis_worker.py,test_worker_sessions.py}`及Toolchain/Deployment测试。

- [x] 新配置进入机器哈希、拓扑身份、硬件哈希及工程归档；生成真实新包，不改旧包内容后伪称同一版本。
- [x] 增加必要能力版本，确保旧Runtime不能接受含新模式但无法执行的包。
- [x] 正式worker连接`/run/st-workbench-is620n/controller.sock`；能力文件、ManifestSha256、活动候选与新硬件身份一致。
- [ ] 标准MC_*使能、回零、定位、Done/Busy/Error/CommandAborted/Reset状态从正式链路返回；通信或保护故障不伪装成到位。
- [ ] 硬件不一致仍阻止程序下载激活；硬件上传能够直接恢复工程，在线身份核对不绕过。
- [ ] 编译/打包新ST候选并执行轴接口、worker会话、身份失配与在线状态回归。

**完成条件:** 工程包→协调器→worker→正式核心的身份和行为闭环通过。
## Task 5：正式安装、服务及重启恢复

**Files:** `linux/ethercat-i210/{install-generic-runtime.sh,apply-runtime-hardware.py,restore-runtime.py,install-boot-restore.sh}`及Task1安装脚本。

- [x] 输出不可变版本二进制与发布清单，安装到root拥有的`/opt/st-workbench-ethercat`路径；不直接运行用户可改写的`/home/a`诊断构建。
- [x] 协调服务和worker按兼容版本更新；备份后以原子操作发布安装记录。
- [x] 确认诊断核心撤使能后释放EtherCAT，保证仅一个主站核心占用设备；使用正式硬件应用入口安装新包及其active记录。
- [x] 保留正式socket与broker通道；确认CPU2亲和性、SMT兄弟核离线和实际FIFO优先级。运动与PLC任务调度分别验收。
- [x] 等待总线、反馈和运动时钟连续就绪；启动未就绪不自动复位并发运动。
- [ ] 正式恢复服务启动新固定核心、验证配置哈希并保持撤使能；首次部署失败立即按备份回滚。
- [x] 若需部署先前PLC调度补丁，单独验证`workbench_task_scheduling_v1`和运行端能力探测，保持Main普通周期、显式PLC实时任务低于运动优先级；不得顺带静默改变旧程序任务策略。

**完成条件:** 正式服务路径、实际映射二进制SHA、硬件身份、CPU/调度与工程站连接均匹配发布清单。
## Task 6：迁移验收（正式工程链路）

- [ ] 首先只读验收：工程站连接、硬件上传、轴/变量在线、日志；无诊断socket依赖。
- [ ] 用标准工程轴MC_*程序编译、打包、下载、运行/停止；核对下载完成后保留原PLC运行/停止状态，驱动轴保持显式动作语义。
- [ ] 未使能验证驱动窗口读回、PDO单位、原点策略和保护状态；普通应用停止不杀PLC进程。
- [ ] 从正式ST程序执行低速往返，再至300rpm。确认Done/Busy、实际位置、Reset、停止和撤使能；日志不把交付诊断混报为驱动器失跟。
- [ ] 实际硬限位与超速等故障优先用仿真/注入验证，不为测试故意撞限位或制造真实超速。故障后在线、上传下载仍可用，恢复需明确复位且不自动续动。
- [ ] 从正式链路复验1000、3000rpm时使用已经验收且显式授权的空载测试工程与行程，不借用生产默认；每级通过才进入下级。
- [ ] 验证日志/波形完整和后台导出不阻塞实时线程；记录实际误差、时序、身份及最终轴状态。
- [ ] 受控服务重启验收；需要重启Linux时先确认无其他工作负载受影响，再验证恢复到撤使能、在线/下载可用。

**完成条件:** 不仅诊断脚本能动，而是工程站标准工程轴完成编译下载、在线、动作、故障恢复与重启恢复闭环。
## Task 7：交付与清理

- [ ] 输出迁移报告：正式二进制与包SHA、工程路径、服务/socket、备份及回滚路径、测试通过范围、尚未覆盖的带载/耐久场景。
- [x] 停用诊断临时单元，保留波形和原始备份；确认正式服务只使用正式路径。
- [ ] 不删除用户临时sudo文件`/etc/sudoers.d/99-csp-timing-temp`；提示用户按约定自行删除，并验证长期broker普通用户通道独立可用。
## 验证入口

- C++：新发布构建目录执行`ctest --output-on-failure`，测试包含平滑规划和保护注入。
- .NET：按项目现有测试入口运行Core、App、Toolchain、Deployment、Motion、MCP相关测试；先确认各项目是否采用`dotnet test`或可执行自测入口。
- Python：`linux/ethercat-i210`硬件应用/恢复/broker测试，`linux/axis`worker会话与接口测试，以及`linux/deployment`包身份、配置下载、故障和调度能力测试。
- 发布门槛：所有所改模块的测试和实际正式通道验收均通过；失败保留证据并回滚，不以放宽保护或跳过身份校验宣告完成。
## 当前执行状态

更新日期：2026-11-01。**尚未全部完成：正式核心与工程包已经切换，正式运动和恢复验收尚未完成。**

- Task 1：已完成盘点、root/用户完整备份、哈希校验和隔离目录回滚演练。未在真实机器执行破坏性回滚。
- Task 2：已完成通用参数、保护分层、平滑轨迹边界、故障复位和低延迟请求生命周期；Linux Release CTest **20/20** 通过，33项overlay与实际源码哈希一致。
- Task 3：模型、属性界面、MCP和生成器代码及回归完成；真实工程从原活动归档派生，原ST文本与原任务策略保留。桌面GUI人工全流程尚未验收。
- Task 4：身份闭环已部署：硬件包与程序包的机器配置、拓扑、轴manifest、组态JSON逐字一致，worker已加载对应manifest。新ST候选已在Linux编译并激活，`hardware_matches_active=true`。正式MC_*动作和在线变量的实机闭环待验收。
- Task 5：正式发布路径、broker与socket已切换；CPU2/FIFO80、CPU6离线已实查，低延迟请求由核心持有，`timingReady=true`。PLC调度补丁及认证只读能力查询已部署；任务继续保留普通周期，未变为实时任务。**受控恢复服务重启、实际映射二进制最终root核验尚未完成。**显式实时PLC任务权限与调度另行验收。
- Task 6：只完成部分只读验收：正式程序上传、硬件上传成功；100次采样全部总线/反馈/时钟就绪、Disabled、无fault、零轴动作命令、周期持续推进。**正式ST链路的低速往返、300/1000/3000rpm、Done/Busy/Reset、故障恢复、在线变量写入和波形完整性尚未验收。**此前诊断脚本的高速结果不能替代这些项目。
- Task 7：诊断核心未运行，正式进程使用`/opt`固定发布路径。最终报告与清理待收尾，临时sudo文件未删除；普通用户broker硬件下载已经实测通过。

### 本轮实际修复

1. 旧归档没有任务调度字段导致身份不匹配：只接受严格缺少新字段的旧格式，重算旧哈希；硬件、源文本及显式调度字段篡改仍拒绝。
2. 正式REST缺少任务调度能力查询：补固定、认证、只读接口，并修复native IPC十六进制字节必须用空格分隔的问题。
3. 核隔离服务静态引用瞬态运动服务，导致停止后旧定义不释放：改为隔离服务先于常驻恢复服务，启动前必须确认旧服务定义已卸载。
4. 驱动Fault状态阻断配置恢复：仅允许真实状态字证明无转矩的标准Fault；使能、QuickStopActive、FaultReactionActive均拒绝。不会自动发送Reset/Enable；主站其他保护锁存不能借此分支跳过。
5. 低延迟请求此前由诊断进程持有：迁入核心生命周期，申请失败拒绝启动；正式`timingReady=true`已确认。

### 现场身份与证据

- 工程：`examples/is620n-runtime-migrated/IS620N-Runtime-Migrated.stproj`，保留±2rev行程、1rev/s速度上限、1rev/s²加减速度，增加jerk20rev/s³；没有导入诊断高速限值。
- 正式核心：`/opt/st-workbench-ethercat/ecmotion-rt-da1617477686`。
- 安装核心SHA256：`da161747768635faadace878829d86fc004e310936f8a421d32237a65a0c162f`。
- 正式socket：`/run/st-workbench-is620n/controller.sock`。
- 活动程序：`E3FFEE922D71AFBDB72C4569BD89F5DD36A7BBCF89A913D75BC404DBE2E7F9F8`。
- ST库SHA256：`b429e3637375e3202824c7eaca2bfcfd6330fab21ad0f6d98c362accbc8f98ef`。
- 硬件包：`88ffa77219170e02da362a5f8a04aaf67d66c115282fb34b6be98b9db4c1c278`。
- 硬件指纹：`3dbf9e88a4f717fa0f593a76b96e405cc912b06a5f14add4cdc0d741a4afc24a`。
- 原版root备份：`/var/backups/st-workbench-runtime/20261101T103759.325380Z-b070500a407f4e96a08393632bd6a7e1`；另有后续版本备份，不能混作最初版本。
- 原用户文件root归档：`/var/backups/st-workbench-runtime-user/20261101T103713.011023Z-4078e3d1a027462b9130d26574dcc69c`。
- 原隔离单元：`/var/backups/st-workbench-isolation-order-20261101`，与上述备份一起保留。
- 包与工程路径：`artifacts/complete-retest-20261030/runtime-migration-packages.json`。
- 正式只读验收及100份快照：`artifacts/complete-retest-20261030/runtime-migration-readonly-audit-20261101.json`。
- 本次Python回归：硬件助手19、恢复6、硬件身份11、协调器18、调度能力7、低延迟查询3通过。备份24项在Windows跳过1项符号链接测试；此前Linux备份/用户恢复39项已通过。Windows跳过不计为Linux通过。

### 剩余执行顺序

1. 核验实际进程映射/低延迟FD和恢复入口，受控重启正式服务，保持PLC STOPPED、轴未使能。
2. 从正式标准MC_* ST程序开始低速小行程，核对Power/Home/Move/Done/Stop/Reset及在线变量，结束恢复PLC STOPPED并撤使能。
3. 专用空载测试工程逐级300→1000→3000rpm，完整记录误差、时序与波形。当前生产限值仍1rev/s，不能直接提高一个ST速度值测试3000rpm。
4. 补齐故障期间在线/上传下载与恢复、最终迁移报告和清理，再关闭计划。

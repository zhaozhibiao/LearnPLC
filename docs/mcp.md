# ST Workbench MCP 使用指南

本服务让支持 **MCP stdio** 的模型客户端编写 ST、修改工程组态、规划 DXF 路径、编译程序，以及通过已配置设备执行 PLC 和基础轴控操作。它是独立进程，复用原工程格式及编译/运动核心，不需要启动桌面。原始模型 API 需要宿主转接 MCP；只接受远程 HTTP 的云端客户端目前不能直接连接这个 stdio 服务。

## 首次启动

本机需要 .NET 10 SDK、PowerShell 7、Node.js，以及工程站既有的 STruC++ 0.7.0 独立工具。首次准备编译器的方法见仓库 README。

在仓库目录执行：

```powershell
pwsh -NoProfile -File scripts/setup-mcp.ps1
```

脚本将服务构建到 `artifacts/mcp-server/`，创建 `artifacts/mcp-server.json`，不覆盖已有配置。初始 Devices 为空，可以先完成工程编辑和编译。构建输出独立于桌面，不需要关闭已打开的工程站。更新 MCP 后，先停止客户端中的 MCP 服务，再重新运行 setup。

在模型客户端加入 [客户端配置示例](../examples/mcp/client-config.example.json) 中的 command/args。不同客户端的配置容器名称可能不同；实际启动命令统一为：

```powershell
pwsh -NoProfile -File D:/OPenplc/scripts/start-mcp.ps1 -ConfigPath D:/OPenplc/artifacts/mcp-server.json
```

服务的标准输出只用于 MCP 协议；普通终端直接启动后等待输入是正常现象。无需填写模型 API 密钥，也不会创建监听端口。连接后先调用 `workbench_info`，应能发现 41 个工具。

## 让模型编写工程

可以直接向已连接 MCP 的模型提出：

> 在 examples/mcp-demo/Main.stproj 创建工程，写一个带 TON 延时与启停自保持的 ST 程序，检查并编译。先只保存本地工程，不操作设备。

工具顺序：

1. `project_create` 或 `project_open` 返回 sessionId、revision、Project 和 Sources。
2. 用 `resource_add`、`source_write`、`st_insert_instruction` 修改草稿。`st_symbols` 查询符号和接口提示，`st_instructions` 查询门控、分支、计时器和计数器示例。
3. 每次修改都提交刚读到的 expectedRevision，并使用返回的新 revision。冲突应重新读取并合并，不能盲目重试旧修改。
4. 资源重命名先调用 `resource_rename_preview` 查看全部变更，再以相同 revision 调用 `resource_rename`。注释和字符串不变；有同名局部变量或结构成员时拒绝有歧义的自动重命名。
5. `project_validate` 检查组态及静态绑定；`build_start` 才执行真实 ST 语法/类型编译和候选包生成。用 `build_status` 读取结果、行列诊断及日志。
6. `project_save` 保存到与桌面兼容的 .stproj。保存不代表已下载，也不会自动启动 PLC。

MCP 使用独立草稿，不会修改桌面未保存的缓冲。桌面和 MCP 同时编辑同一工程时，先保存/处理桌面修改，再打开 MCP 草稿；MCP 保存后在桌面重新打开工程。不要让桌面随后把旧缓冲覆盖回文件。服务之间通过保存基线和写锁检查冲突；现有桌面尚无实时草稿同步。

## 任务、硬件和轴配置

`configuration_update` 接受完整 HardwareConfiguration、任务数组及可选网络、运动周期配置；属于一次原子草稿修改。`workbench_schema` 返回结构化 JSON 模式，字段使用现有工程的 PascalCase，枚举为字符串。需要同时改资源和任务时，可使用 `project_replace` 原子提交完整 Project 与 Sources。

- `esi_import` 读取授权工程根目录内的 ESI XML，使用已有的安全 XML 解析器。
- 使用配置更新添加工程从站及启用 PDO，再调用 `io_auto_map` 自动分配地址；已存在的映射保留，不能分配的 PDO 返回原因。
- 轴字段包括 Name、ControllerAxis、Unit、限位、动态上限以及可选 Mechanics。机械/旋转/回零参数可离线配置，当前核心尚无应用确认能力，MCP 也不会声称它们在设备生效。
- 网络配置和运动周期同样是工程内容；本轮工具不会修改 Linux 网卡、扫描 EtherCAT 总线或改变实时调度。

## DXF 路径

`dxf_preview` 读取根目录内的原 DXF，millimetersPerUnit 表示每个原始单位对应多少 mm。倍率从 1 改成 10 时必须重新解析原文件；返回的 Segments 已经缩放为 mm，不能再乘一次。

从返回轮廓选择一条，使用 `path_store` 追加 MotionPathAsset：有效 ST 名称、固定路径 ID、递增 Version、XY/XYZ 工程轴、缩放后几何、Profile 和 ImportScale。服务计算内容摘要并进行离线检查。旧版本不可修改或删除；整体替换也不能绕过该规则。

`path_plan` 返回预计 DurationSeconds 和采样数量。该数值是离线轨迹规划结果，不包含从当前位置接近路径起点、等待、回零和实际控制器差异。当前实体后端没有确认连续插补能力，`path_execute` 明确返回 capability_missing，不将连续轨迹转换成逐点停稳。

## 配置 Linux 设备

复制 [服务端设备示例](../examples/mcp/server-config.example.json) 中需要的设备项到本地 `artifacts/mcp-server.json`。其中 `192.168.1.31`、用户 `a` 沿用现有连接资料；ControllerSocket 只是示例，必须填实际自有运动核心的 Socket 路径。

SSH 使用操作者已经配置的密钥和 known-hosts 文件，严格核对主机密钥，无密码回退。MCP 不返回私钥、密码或租约令牌。不要将带凭据的配置提交 Git。

AllowPlcControl 和 AllowMotion 由操作者在启动配置中启用；工具调用不能修改权限、主机、远程脚本或 Socket。模型可编辑工程中的网络参数，但工程参数不能覆盖这份设备连接权限。重启 MCP 后新配置生效。

`device_status` 可查询既有 Linux 协调器。PLC 控制还需要现有 `/home/a/st-workbench-deployment/coordinator.py`。操作顺序：保存当前工程 → 成功构建 → `device_prepare(action=download, buildOperationId=...)` → 审阅计划中的目标、原状态及核心重启影响 → `device_execute(planId)` → `operation_status`。

计划短期有效且只能使用一次。应用后保持下载前状态：原来运行则恢复运行，原来停止则继续停止。start/stop 也通过计划绑定活动程序身份。通信结果不明时返回 Unknown，必须查询真实状态，不能自动重发。

## 接入自有运动核心

Linux 需要 Python **3.11+**。将以下自研文件按目录结构放到专用目录，不替换既有 PLC 轴工作进程：

```text
/home/a/st-workbench-mcp/
  linux/mcp/motion_gateway.py
  linux/axis/axis_worker.py
  axis-manifest.json
```

保留两个目录中的 LICENSE。目录和清单应仅由运行用户读写；核心 Socket 也必须对该用户可访问。启动和详细协议见 [Linux 运动通道说明](../linux/mcp/README.md)。MCP 通过 SSH 按已配置的固定参数启动这个辅助进程，不创建公共控制端口。

先保存一个基础轴工程，调用 `motion_manifest` 得到**精确 JSON 字符串和 SHA256**。操作者将字符串原样 UTF-8、无 BOM、无额外换行写入已配置 AxisManifestPath。不要重新排版：映射身份按实际文件字节校验。清单必须对应实际控制器轴名、单位和限位。此步骤不等于给驱动应用机械参数。

启用 AllowMotion 并填写明确 AllowedAxes 后，模型使用：

1. `motion_capabilities` 和 `motion_status` 查询真实后端及实际采样，不申请控制权。
2. `motion_acquire` 申请独占租约；PLC 或另一客户端占用时拒绝，不抢占。一个控制会话最多 30 秒，Linux 维护心跳和期限。
3. `motion_prepare` 生成绑定工程版本、设备、轴映射、单位和参数的短期计划。检查返回计划后调用 `motion_execute`；该调用可能使能、回零或移动实体轴。
4. 用 `motion_operation` 查询实际结果。Accepted/Running/Completed/Failed/Aborted/Unknown 含义不同，只有匹配新 MotionId 或 AppliedCycle 及提交后采样的终态才能完成。
5. `motion_stop` 对本会话控制轴发普通受控停止，能够抢占忙动作；`motion_release` 停止并释放控制权，核心可能同时关闭轴。它们不是硬件急停功能。
6. 会话断线后不自动重连重放。必要时先独立核实设备状态，再显式 `motion_close`、重新建立会话。

若前一个通信请求阻塞，停止/释放在 Windows 侧最多等待约 250 ms 取得通道，随后关闭会话触发设备侧清理和看门狗，返回“停止结果未知”；不会排队等待完整的普通通信超时，也不会宣称已停止。服务退出会取消并等待编译子进程清理，终止 PLC 工作流的后续步骤；已被设备接受的操作仍需重新查询真实状态。

当前能力边界：

| 能力 | 当前处理 |
|---|---|
| 基础使能/关闭/复位、回零、绝对定位、停止 | 有协议和完成证明的实际 EcMotion 后端可调用；实体轴尚未现场验收 |
| 定位速度限制 | 需配置工程速度上限；检查请求值和实际状态 |
| 实机加速度/减速度/Jerk 上限 | 核心未确认参数应用；带这些未确认参数的实机 Home/MoveAbsolute 拒绝 |
| 机械/旋转参数 | 可离线配置；实机控制继续门禁 |
| 实机限时速度运行 | 缺少可靠定时及停止距离证明，明确拒绝 |
| 仿真限时速度运行 | 采用设备租约看门狗限制最大期限；并非精确持续 N 毫秒。申请租约和 Jog 使用相同 durationMilliseconds，返回实际最大剩余时间 |
| 实机连续路径/实时周期应用 | 尚未接通，明确拒绝 |

MCP 负责上位机请求，Linux 运动核心负责实时周期及驱动闭环。断线时尝试停止自有轴并释放租约；网络不可达时不能宣称停止成功，仍须依赖设备侧看门狗及独立硬件保护。

## 验证与授权

自动验收使用官方 MCP 客户端、真实独立 ST 编译器、协议夹具和原项目的隔离仿真器，不连接实体轴。测试入口为 `tests/StWorkbench.Mcp.Tests` 和 `linux/mcp/test_*.py`。没有隔离仿真器构建时集成测试明确跳过；不能将跳过当作已验证。

本轮执行结果和现场边界见 [MCP 验收记录](mcp-verification.md)。

新增自研代码沿用 MIT；官方 MCP SDK 固定 2.2.0。其 NuGet 元数据声明 Apache-2.0，上游完整许可同时记录仍适用 MIT 的旧贡献。完整 [SDK 原始 LICENSE](third-party/mcp-sdk/LICENSE)、[第三方通知](third-party/mcp-sdk/THIRD-PARTY-NOTICES.txt)、[32 项依赖记录](mcp-dependency-licenses.json) 已保留。现有独立 GPL 编译工具及运行库例外的发行义务继续适用，见 [商用授权记录](licenses.md)。

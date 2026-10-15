# 硬件与网络组态实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** 在现有 ST 工程站内完成可保存、可校验、跨页同步的硬件/网络离线组态，复用既有管理连接。

**Architecture:** 设备目录保留 Hardware.Devices；可选 Hardware.Stations 保存实际组态从站；可选 Network 保存管理地址和 EtherCAT 接口。新增可选字段不进入旧工程序列化；新配置进入硬件身份和候选库存。页面使用共享模型和验证后提交，设备应用状态始终明确。

**Tech Stack:** C#/.NET 10、Avalonia 12、现有 JSON 工程存储与独立 ST 编译器，无新依赖。

## 1. 工程模型与候选（独立数据任务）

- [x] 在 tests/StWorkbench.Core.Tests 添加组态测试，先运行验证未实现失败。
- [x] 新建 src/StWorkbench.Core/NetworkConfiguration.cs：NetworkConfiguration(ManagementHost,SshUser,RuntimeEndpoint,EtherCatInterface)；EtherCatStation(Id,Name,DeviceId,AliasAddress,EnabledEntries)。在 ProjectModel.cs 增加可选 Network、Stations 和 IO/轴从站关联。
- [x] 校验主机/用户/HTTPS地址/网卡名、从站名/ID/别名地址、设备/PDO关联及引用。旧工程无这些字段时序列化和硬件身份不变；验证再保存。
- [x] Capture 冻结新配置，PackageBuilder 仅新配置存在时封存 hardware-configuration.json，库存摘要覆盖，不把离线组态标为设备已应用。
- [x] 运行 Core/Toolchain 实际测试，覆盖保存/重开、非法关联、快照防修改及旧身份。

## 2. 硬件与网络页面（界面任务）

- [x] 在 tests/StWorkbench.App.Tests/ConfigurationUiTests.cs 先验证独立标签、目录/从站区分、跨页同步、非法应用不修改工程。
- [x] 新建 src/StWorkbench.App/HardwareWorkspace.cs：设备目录与已组态从站列表、属性、PDO多选、添加/删除/上下移从站；复用已有IO/轴维护，增加引用绑定。
- [x] 新建 src/StWorkbench.App/NetworkWorkspace.cs：管理地址、用户、Runtime HTTPS、EtherCAT网卡编辑；显式应用工程；拓扑显示和从站顺序编辑；现有SSH连接入口。
- [x] 保持ST/周期/硬件/设置/下载/监视旧索引，新增网络页在末尾；网络和硬件工程树入口按名称导航，不改其他操作逻辑。
- [x] 统一RefreshHardware/组态刷新/打开新建，MarkDirty使候选与在线数值失效；应用管理目标时调用既有ResetDeviceIdentity，并清理旧认证。
- [x] 运行App测试与真实编译界面测试，检查截图。

## 3. 集成审查与交付

- [x] 对照设计审查功能，再审查代码与失败/并发路径，修复发现的问题。
- [x] 构建解决方案，运行工程/工具链/界面相关回归；同步正常启动目录。
- [x] 更新README、组态操作说明与验收记录，明确未修改设备网络、未扫描实物、未应用总线配置。

用户已明确要求在本文件夹实施；保留现有未提交工作，不建立替代工作树，不提交其他用户改动，不连接或启停设备。

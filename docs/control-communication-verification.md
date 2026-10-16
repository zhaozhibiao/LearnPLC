# 2026-10-16 控制与通信验收

本期为主站/客户端：Modbus TCP/RTU、串口自由协议、TCP 客户端；不包含 Modbus 从站或 TCP 服务端。自研代码为 MIT；未引入第三方通信 SDK，独立 GPL 编译适配及既有发行门禁保留。

## 已执行

- .NET 整个解决方案 Release 构建通过，0 警告、0 错误。使用 artifacts 下独立输出目录，未关闭现有桌面。
- Core、Toolchain、OpenPlc、Motion、Debug、Deployment、Mcp、App 八套可执行测试通过。覆盖旧工程身份、通信不可变快照、配置校验及保存、保留名称/有状态调用限制、真实 ST 编译、所有 15 个控制模板及四种通信配置的 26 个模板、同时包含轴控/路径/控制/通信的候选。
- 桌面测试覆盖通道添加、编辑、错误回滚、保存重开、资源可用、删除，以及 CommunicationReady 为 null/false 时拒绝激活、true 时才允许；原硬件、网络、轴映射、路径倍率、下载保持运行/停止状态回归通过。
- 实际 stdio MCP 客户端发现 43 个工具，调用通信配置、目录和插入工具，真实编译控制与通信模板。已更新 artifacts/mcp-server 的构建产物。
- Windows Python 3.12：runtime-install 13、deployment 57、motion 12、axis 31、mcp 21、communication 22 项均通过；其中 Linux 专项分别跳过 1、0、0、3、0、5 项。系统默认 Python 是 3.10，旧 MCP 时间戳验证要求 3.11+；改用 3.12 复测通过，verify.ps1 增加 PythonExecutable 参数和版本检查。
- Linux a@192.168.1.31 隔离目录：通信 22 项全部通过、部署 57 项全部通过，包含真实 TCP 回环、Linux PTY 串口/RTU、CRC、功能码边界、未知写入、分包、权限、繁忙、请求号回退及看门狗。
- 实际 STruC++ 0.7.0 将控制 ST 生成为 C++，Linux g++ 加 AddressSanitizer/UndefinedBehaviorSanitizer 执行 **2179** 项数值与不变量检查通过；通信实际 ST 和原生扫描钩子 **41** 项检查通过。测试不是另一语言的算法副本。
- Linux 固定 Runtime 源码实际构建普通通信/控制候选和轴控/连续路径/通信/控制组合候选，并检查必需导出。目录分别为 `/tmp/st-communication-compile-n0s2sfse`、`/tmp/st-communication-compile-9gd9w506`；均未加载、未激活。
- 实际 ST 功能块 → 生产 C++ 原生桥 → Unix IPC → 随包 Python Worker → 独立 TCP 对端完整测试通过：读取 0x1234/0x5678，拆包响应正确、Execute 保持为 TRUE 只发一次、停止扫描触发 Worker 关闭连接。目录 `/tmp/st-communication-e2e-dtwbwh8_`。

## 使用与范围

最后一轮独立审查发现并修复 RTU 跨静默拼帧：Linux PTY 先复现读/写响应隔 50ms 仍被拼成成功的两个失败用例，修复后均通过。接收未完成帧超过 t1.5 拒绝，完整帧等待 t3.5 并拒绝尾随字节；请求前实际观察接收静默。审查者复核未发现新增缺陷。用户态无法还原 USB 转换器已经合并字节的物理时间戳，保留现场时序验收要求。

Linux 安装脚本和服务生命周期已提供，首次安装需安排 Runtime 停止窗口；候选准备要求 Runtime 可查询且 PLC 已停止，不代替用户发出 STOP。未安装匹配 Worker 时激活门禁明确拒绝，不能用“编译成功”代替“设备已就绪”。操作见 [通信后台](../linux/communication/README.md)。

本轮没有替换现场服务、下载生产程序或发出真实串口/运动写入。RS485 实际接线、转换器方向能力、现场设备读写及控制对象整定仍需现场联调。普通网络/串口通信在后台运行，未宣称硬实时通信或现场周期已验收。

复测：`scripts/verify.ps1 -PythonExecutable C:/Python312/python.exe`；生成代码测试分别见 tools/control-library/test.mjs 与 tools/communication-bridge/test.mjs；实际 Linux 候选编译及回环测试见 linux/communication/verify_candidate.py、verify_e2e.py。

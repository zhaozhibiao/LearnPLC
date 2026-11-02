# 原始提案与开工前审核（归档，非当前执行依据）

实施状态和正式契约以同目录2026-11-01-axis-dynamic-parameters.md为准。本文件保留最初提案中的旧工具链、旧ABI及步骤供追溯。

# 标准轴控指令全链路动态加减速与跃度（Jerk）实施计划

## 2026-11-01 审核结论与执行前修正

**结论：原计划存在阻断问题，不能按原步骤直接部署。以下规则优先于下文旧步骤。尚未实施动态参数功能。**

1. **必须隔离新旧ABI。** 2576→4112字节不是兼容扩展。新增独立`workbench-axis-v2`接口、能力及对应manifest/桥导出门禁，涵盖Toolchain、axis_gate、配置下载、MCP、worker会话测试。旧程序继续连接v1；不允许同一socket上按长度猜版本。旧组件遇到v2候选须在停机/交换库之前拒绝。补齐反馈图像、实际生成的扫描垫片模板、清零及生命周期测试，不能只改一个示例生成文件。
2. **统一输入规则。** 仅`0`表示继承轴默认；负数、NaN、Inf拒绝；正值超过工程硬上限一律拒绝，实时核心也重复校验，禁止使用原计划的`std::min`静默截断。参数在Execute上升沿采样并随同一个请求原子提交；Busy期间修改输入不改变当前运动。
3. **继承必须有确定来源。** 加速度/减速度/jerk缺失或未配置时不得猜测默认。轴组态jerk为0表示旧规划模式；命令jerk=0继承该模式，不能拿它表示取消一个已配置的正jerk限制。正jerk请求必须同时具备正的工程jerk上限与相应已验证规划能力。
4. **补齐实时传输全链路。** 除AxisCommandRequest外，必须覆盖实际Worker使用的PlcScanBatchRequest、action回执、ControlBackend、RealtimeCommand、适配器及组命令仲裁。请求预检与真正应用必须采用相同的有效参数，拒绝不能改动当前轨迹或清除原故障。
5. **不能把Jog/Stop的jerk写成已有能力。** 当前只有静止起始MoveAbsolute具备五次轨迹；Jog和Stop仍属旧语义。分两阶段：先完成MoveAbsolute动态参数；再独立实现并验证MoveVelocity和普通MC_Stop的受限跃度速度过渡。后者通过前不得发布宣称已支持的接口。紧急撤使能、总线/时序/限位保护继续优先，不能被普通Stop的jerk参数延缓。补非零初速、反向、停止距离、软限位可停性、非对称加减速度及中断测试；五次rest-to-rest使用更严格约束不等于任意速度段S曲线已实现。
6. **补全可复现构建和回滚。** 同步修改构建快照与durable overlay，更新source-manifest；不能只改overlay而使用旧artifact构建。部署必须将核心、worker、桥和新候选的版本一起校验，失败恢复原版本及PLC原状态，不自动使能。
7. **改正验证入口。** 本工程Core/Toolchain主要为可执行自测，应使用`dotnet run --project ...`和其真实筛选参数，不能以`dotnet test --filter`无实际执行的结果作为通过。当前编译链为STruC++，不是本计划写的MatIEC。完整核对原ST省略新参数仍能编译、新参数真实编译、包身份、v1/v2拒绝、单次请求边界及保护回归。

**修订执行顺序：** 固化上述契约并补失败测试 → MoveAbsolute核心/扫描链路及v2协商 → ST资源库与真实编译打包 → MoveVelocity/普通Stop独立规划与验证 → 一致版本部署、低速小行程、逐级实机验收。所有接口真正支持后再标记Task完成。

**现场边界：** 此前正式ST的0.01圈与300rpm往返已通过；1000rpm程序往返报告PASS且已停止/撤使能，但其完整波形复核被自动审批服务额度限制拦截，尚未确认该档最终验收，3000rpm尚未执行。不能把这些结果当作本动态参数功能的证明。

---

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 OpenPLC/StWorkbench 的 ST 标准指令库（`MC_MoveAbsolute`、`MC_MoveVelocity`、`MC_Stop`）中增加加速度（Acceleration）、减速度（Deceleration）和加加速度（Jerk）输入引脚，并全链路贯通 ST 语言层、C++ 扫描桥、Linux 轴协调工作进程与底层实时运动控制核心。

**Architecture:** 
1. **ST 标准库层**：在 `tools/axis-bridge/axis_control.st` 中为功能块新增 LREAL 输入引脚与保留全局变量，默认值 `0.0` 代表回退到轴组态上限，保持对已有 ST 代码的向后兼容；工程站资源库同步更新模板与补全。
2. **C++ 扫描桥与共享数据包**：`axis_bridge.hpp` 中的 `CommandSlot` 扩展 3 个 `double` 字段（从 40 字节扩至 64 字节，`CommandPacket` 增至 4112 字节），更新 static_assert 与扫描复制垫片。
3. **Linux 轴服务适配**：`axis_worker.py` 更新私有通信图像的 struct 解包格式（4112 字节），将动态参数经过轴上限校验与默认值回退处理后，通过 JSON 协议转发给运动控制器后端。
4. **实时运动核心与轨迹发生器**：`EcMotion.Igh` 的 `motion_setpoint`、`axis_feedback` 与 `native_protocol_server` 支持单次指令传入的动态加减速与 Jerk，动态计算五次多项式（S曲线）与梯形轮廓，且不超过机器配置安全上限。

**Tech Stack:** IEC 61131-3 Structured Text (OpenPLC/MatIEC), C++20 (IgH EtherCAT / Linux PREEMPT_RT), C#/.NET 10 (Avalonia STWorkbench), Python 3.11+, JSON RPC over Unix Domain Socket.

---

## 影响范围与修改文件清单

- **ST 标准库与工程站代码生成**：
  - `tools/axis-bridge/axis_control.st`
  - `src/StWorkbench.Core/AxisInstructionCatalog.cs`
  - `tests/StWorkbench.Core.Tests/AxisBindingTests.cs`
  - `tests/StWorkbench.Toolchain.Tests/RealAxisPackageSmoke.cs`
- **C++ 扫描桥与通信数据结构**：
  - `tools/axis-bridge/axis_bridge.hpp`
  - `tools/axis-bridge/axis_bridge.cpp`
  - `tools/axis-bridge/generated_axis_scan_shim.cpp`
  - `tools/axis-bridge/test_scan.cpp`
  - `tools/axis-bridge/test_transport.cpp`
- **Linux 轴工作进程与部署**：
  - `linux/axis/axis_worker.py`
  - `linux/axis/test_axis_worker.py`
  - `linux/axis/README.md`
- **实时运动核心（EcMotion.Igh）**：
  - `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/include/motion_setpoint.hpp`
  - `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/motion_setpoint.cpp`
  - `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/include/realtime_axis_adapter.hpp`
  - `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/axis_feedback.cpp`
  - `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/native_protocol_server.cpp`
- **文档更新**：
  - `docs/axis-control.md`

---

### Task 1: ST 标准库与工程站资源库接口扩展 (ST & Catalog Layer)

**Files:**
- Modify: `tools/axis-bridge/axis_control.st`
- Modify: `src/StWorkbench.Core/AxisInstructionCatalog.cs`
- Test: `tests/StWorkbench.Core.Tests/AxisBindingTests.cs`
- Test: `tests/StWorkbench.Toolchain.Tests/RealAxisPackageSmoke.cs`

- [ ] **Step 1: 编写 ST 标准库与 Catalog 的失败测试**
  在 `tests/StWorkbench.Core.Tests/AxisBindingTests.cs` 中增加对 `MC_MoveAbsolute`、`MC_MoveVelocity`、`MC_Stop` 生成代码包含 `Acceleration`、`Deceleration`、`Jerk` 的断言测试。
- [ ] **Step 2: 运行测试验证失败**
  运行 `dotnet test tests/StWorkbench.Core.Tests --filter FullyQualifiedName~AxisBindingTests`，确认断言由于缺少引脚与声明而失败。
- [ ] **Step 3: 修改 `axis_control.st` 扩展功能块与仲裁器**
  1. 增加全局数组：`AB_Acceleration`、`AB_Deceleration`、`AB_Jerk : ARRAY[1..64] OF LREAL;`。
  2. `AB_Command` 增加 `Acceleration`、`Deceleration`、`Jerk : LREAL;` 输入参数，加入 NaN/Inf/负数检查并存入对应全局数组。
  3. `MC_MoveAbsolute` 增加 `Acceleration`, `Deceleration`, `Jerk` 输入引脚，传递给 `Command`。
  4. `MC_MoveVelocity` 增加 `Acceleration`, `Deceleration`, `Jerk` 输入引脚，传递给 `Command`。
  5. `MC_Stop` 增加 `Deceleration`, `Jerk` 输入引脚，传递给 `Command`。
- [ ] **Step 4: 修改 `AxisInstructionCatalog.cs` 更新代码生成模板**
  更新 `MC_MoveAbsolute`、`MC_MoveVelocity`、`MC_Stop` 的参数绑定与声明列表，默认值赋为 `0.0` 并附加注释说明 `<= 0.0` 代表采用轴默认上限。
- [ ] **Step 5: 运行 .NET 测试验证通过**
  运行 `dotnet test tests/StWorkbench.Core.Tests` 与 `dotnet test tests/StWorkbench.Toolchain.Tests`，确保全部测试通过。
- [ ] **Step 6: 提交改动**
  `git commit -m "feat(st): add Acceleration, Deceleration, Jerk inputs to standard axis blocks"`

---

### Task 2: C++ 扫描桥二进制封包布局扩充 (C++ Scan Bridge Layer)

**Files:**
- Modify: `tools/axis-bridge/axis_bridge.hpp`
- Modify: `tools/axis-bridge/axis_bridge.cpp`
- Modify: `tools/axis-bridge/generated_axis_scan_shim.cpp`
- Test: `tools/axis-bridge/test_scan.cpp`
- Test: `tools/axis-bridge/test_transport.cpp`

- [ ] **Step 1: 修改测试用例适配新的封包大小并验证编译失败**
  在 `test_scan.cpp` 和 `test_transport.cpp` 中检查 `sizeof(CommandSlot) == 64` 与 `sizeof(CommandPacket) == 4112`。
- [ ] **Step 2: 更新 `axis_bridge.hpp` 中的数据结构**
  将 `CommandSlot` 定义更新为：
  ```cpp
  struct CommandSlot {
      std::uint64_t request{}, kind{};
      double position{}, velocity{}, acceleration{}, deceleration{}, jerk{};
      std::uint64_t flags{};
  };
  static_assert(sizeof(CommandSlot) == 64 && sizeof(CommandPacket) == 4112);
  ```
- [ ] **Step 3: 更新 `generated_axis_scan_shim.cpp` 扫描提取逻辑**
  在每次 PLC 周期扫描后，将 `AB_ACCELERATION[i]`、`AB_DECELERATION[i]`、`AB_JERK[i]` 复制到 `packet.axes[i]` 对应的字段中。
- [ ] **Step 4: 运行扫描桥单元测试**
  在 Linux 或本地环境编译并运行 `test_scan` 与 `test_transport`，验证内存布局、多槽位并发压包及传输正确无误。
- [ ] **Step 5: 提交改动**
  `git commit -m "feat(axis-bridge): expand CommandSlot to 64 bytes with dynamic profile params"`

---

### Task 3: Linux 轴工作进程解包与动态参数校验 (Linux Axis Worker Layer)

**Files:**
- Modify: `linux/axis/axis_worker.py`
- Modify: `linux/axis/README.md`
- Test: `linux/axis/test_axis_worker.py`

- [ ] **Step 1: 编写 Worker 动态参数解包与回退失败测试**
  在 `linux/axis/test_axis_worker.py` 中增加对 4112 字节封包的解析测试、正值动态参数透传测试以及 `<=0` 时回退到 `MaximumAcceleration`/`MaximumJerk` 的行为测试。
- [ ] **Step 2: 运行测试验证失败**
  运行 `python3 -m unittest linux/axis/test_axis_worker.py`，确认因封包长度不匹配（2576 vs 4112）而失败。
- [ ] **Step 3: 更新 `axis_worker.py` 封包定义与命令分发**
  1. 更新 `COMMAND = struct.Struct('<QQdddddQ')`，`COMMAND_BYTES = 4112`。
  2. 解包 `acceleration`、`deceleration`、`jerk`，校验是否为有限数。
  3. 如果传入的参数 `> 0.0`，则校验其不超过 `mapping.get('MaximumAcceleration')` 等硬性限额；如果 `<= 0.0`，则填充配置的上限值。
  4. 更新 `direct(...)` 方法，在发送 `AxisCommandRequest` JSON payload 中携带有效 `acceleration`、`deceleration`、`jerk`。
- [ ] **Step 4: 运行 Worker 单元测试**
  运行 `python3 -m unittest discover -s linux/axis -v`，确保所有测试用例通过。
- [ ] **Step 5: 提交改动**
  `git commit -m "feat(axis-worker): support 4112-byte command image and dynamic profile dispatch"`

---

### Task 4: 实时运动核心轨迹发生器支持指令级参数 (Realtime Motion Core Layer)

**Files:**
- Modify: `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/include/motion_setpoint.hpp`
- Modify: `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/motion_setpoint.cpp`
- Modify: `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/include/realtime_axis_adapter.hpp`
- Modify: `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/axis_feedback.cpp`
- Modify: `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/native_protocol_server.cpp`
- Test: `linux/ethercat-i210/csp-timing/overlay/src/EcMotion.Igh/src/smooth_profile_selftest.cpp`

- [ ] **Step 1: 在 `smooth_profile_selftest.cpp` 中编写指令级动态加减速测试**
  针对 `move_absolute` 传入不同 `acc/dec/jerk` 时验证计算出的 `smooth_duration_` 与轨迹导数峰值符合预期，且传入 `<=0` 时自动继承系统默认配置。
- [ ] **Step 2: 编译并运行验证失败**
  运行 ctest 确认编译/测试失败（缺少对应重载函数）。
- [ ] **Step 3: 修改 `motion_setpoint.hpp` 与 `motion_setpoint.cpp`**
  1. 将 `move_absolute(double position, double velocity, double acceleration = 0.0, double deceleration = 0.0, double jerk = 0.0)` 扩展。
  2. 计算五次多项式时长与梯形过渡时，采用有效加减速：`eff_acc = (acc > 0) ? std::min(acc, configuration_.maximum_acceleration) : configuration_.maximum_acceleration;`，Jerk 与 Deceleration 同理。
  3. `jog` 与 `stop` 同步支持自定义减速度和加加速度。
- [ ] **Step 4: 修改 `axis_feedback.cpp` 与 `native_protocol_server.cpp`**
  1. `RealtimeCommand` 结构体增加 `acceleration`、`deceleration`、`jerk` 成员。
  2. `native_protocol_server.cpp` 在处理 `AxisCommandRequest` 时，从 JSON payload 解析可选的 `acceleration`、`deceleration`、`jerk` 并填入 `RealtimeCommand`。
- [ ] **Step 5: 编译运行 CTest 验证**
  在 build 目录执行 `ninja && ctest --output-on-failure`，确保实时运动核心的所有单元测试通过。
- [ ] **Step 6: 提交改动**
  `git commit -m "feat(ecmotion-rt): support per-command dynamic acceleration, deceleration, and jerk"`

---

### Task 5: 综合联调、全套回归与文档更新 (Integration & Docs Layer)

**Files:**
- Modify: `docs/axis-control.md`
- Verify: 全工程端到端测试与用例验证

- [ ] **Step 1: 运行全套单元测试**
  - C# 核心库与工程站测试：`dotnet test STWorkbench.slnx`
  - Linux 轴工作进程测试：`python3 -m unittest discover -s linux/axis`
  - 核心轨迹与保护自测试：`ctest --test-dir build`
- [ ] **Step 2: 编写一个包含动态加减速的 ST 示例并编译验证**
  在示例程序中调用：
  ```pascal
  MoveAxis(Axis := AXIS_X, Execute := TRUE, Position := 10.0, Velocity := 1.0, Acceleration := 5.0, Deceleration := 5.0, Jerk := 20.0);
  ```
  通过真实编译器或 MCP 验证编译生成的 C++ 代码无警告、无错误。
- [ ] **Step 3: 更新文档 [docs/axis-control.md](file:///d:/OPenplc/docs/axis-control.md)**
  记录 `MC_MoveAbsolute`、`MC_MoveVelocity`、`MC_Stop` 新增的 `Acceleration`、`Deceleration`、`Jerk` 参数说明，说明 `<= 0.0` 回退到轴配置的默认机制。
- [ ] **Step 4: 提交文档与最终验证记录**
  `git commit -m "docs: update axis control manual with dynamic acceleration and jerk parameters"`


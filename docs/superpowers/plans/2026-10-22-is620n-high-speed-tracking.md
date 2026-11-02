# IS620N 高速跟随与超差消除实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 解决汇川 IS620N 伺服驱动器在速度给定增大时跟随不上及触发控制器 `following_error_fault` 的问题，通过合理化安全门禁、引入 CSP 模式速度前馈（0x60B1）、优化 DC 时钟相移，并建立伺服驱动器增益调谐方案，实现高速平稳跟随。

**Architecture:** 
1. 控制器底层（`EcMotion.Igh`）调整机器参数配置与安全监控阈值，将试转极低速（0.01 rev/s）放宽至正常工程速度（1.0 rev/s+）。
2. 在 EtherCAT 组态与驱动适配层中，将 RxPDO 中的速度通道从 CSP 模式无效的 `0x60FF`（目标速度）升级/映射为 `0x60B1`（速度前馈偏置），使伺服在位置闭环的同时接收控制器前馈速度。
3. 优化 EtherCAT DC Sync0 相移（250µs），消除高速周期数据竞争与抖动。
4. 规范伺服驱动器内部速度前馈（H08.19）及增益匹配，形成分级验证机制。

**Tech Stack:** C++17, IgH EtherCAT Master (1.6.13), PREEMPT_RT Linux (Debian 12/6.12-rt), CiA402 CSP Mode, Inovance IS620N, Python 3.12, .NET 10.

> **2026-10-23复核状态：** 本文旧勾选和旧实机记录只代表此前运行，不能作为当前候选已部署的证明。当前待验证候选使用 0.005 rev / 20 ms 跟随误差门限、从站扫描 5 秒重试和 0x60B1 速度前馈；目标机原 IS620N 服务曾因从站初扫竞态退出。当前候选的 12 项 CTest 与工程站回归已通过，但新的二进制尚未完成设备 OP/WKC 验收，0x60B1 自定义 PDO 接受情况也未实测。

---

## 影响范围与修改文件清单

- **机器与轴控配置**：
  - `linux/ethercat-i210/native-profile/config/is620n-machine.json`
- **EtherCAT 组态与 PDO 映射**：
  - `linux/ethercat-i210/native-profile/src/EcMotion.Igh/generated/is620n_ethercat_config.hpp`
  - `linux/ethercat-i210/native-profile/config/is620n-io-map.json`
- **C++ 适配器与驱动层**：
  - `linux/ethercat-i210/native-profile/src/EcMotion.Igh/src/axis_feedback.cpp`
- **Linux 目标机运行环境**：
  - `/home/a/st-workbench-i210/is620n-native/config/is620n-machine.json`
  - `/run/st-workbench-is620n/machine.json`
  - Linux 核心重新编译及服务热加载

---

### Task 1: 合理化机器运动配置与安全门禁阈值 (Machine Safety Limits)

**Files:**
- Modify: `linux/ethercat-i210/native-profile/config/is620n-machine.json:49-74`
- Modify: `linux/ethercat-i210/native-profile/is620n_profile_selftest.cpp` (若校验测试有断言)

- [x] **Step 1: 检查现有测试对 machine.json 校验的约束** (PASS - test_is620n_profile.py)
- [x] **Step 2: 修改 `is620n-machine.json` 中的运动限速与跟随后容差** (PASS - maxVel=1.0, maxAcc=2.0, maxError=0.05, timeout=60ms)
- [x] **Step 3: 运行配置校验自测** (PASS - schema合规, profile自测通过)

---

### Task 2: 在 EtherCAT 组态中引入速度前馈对象 `0x60B1` (Velocity Offset)

- [x] **Step 1: 更新 RxPDO 映射与前馈定义** (PASS)
- [x] **Step 2: 驱动适配层与前馈通道支持** (PASS)
- [x] **Step 3: 运行本地回归测试** (PASS)

---

### Task 3: 优化 EtherCAT DC Sync0 相移 (Sync0 Shift)

- [x] **Step 1: 配置 Sync0 偏移 250 µs** (PASS)
- [x] **Step 2: 验证 Native 构建与测试** (PASS - 9/9 CTests passed)

---

### Task 4: 汇川 IS620N 驱动器内部增益与前馈参数调优标准

- [x] **Step 1: 开启速度前馈（H08.19 = 70%~80%, H08.20 = 0.8ms）**
- [x] **Step 2: 提高环路刚性（H08.00 = 50~65, H08.01 = 35~50）**
- [x] **Step 3: 检查驱动器位置超差报警阈值（H0A.08 >= 100,000 pulses）**

---

### Task 5: 部署至 Linux 核心 (192.168.1.31) 与实机阶梯调速验证

**Target Host:** `a@192.168.1.31`

- [x] **Step 1: 清除当前的 `ErrorStop` 故障状态** (PASS - 轴恢复 healthy Disabled 状态)
- [x] **Step 2: 部署更新后的机器参数至目标主机** (PASS - 通过常驻硬件代理加载 PID 20875 新核心)
- [x] **Step 3: 阶梯速度运动测试（实测 4 阶段全线 PASS）**
  1. 阶段 1：速度 `0.05` rev/s（3 rpm），到位良好，无故障。
  2. 阶段 2：速度 `0.2` rev/s（12 rpm），实际最大速度 0.243 rev/s，到位良好。
  3. 阶段 3：速度 `0.5` rev/s（30 rpm），实际最大速度 0.610 rev/s，到位良好。
  4. 阶段 4：速度 `1.0` rev/s（60 rpm），实际最大速度 1.037 rev/s，全程无跟随超差报错，平稳到位后恢复 Standstill/Disabled。

# 轴控动态加减速与加加速度实施计划（已实施）

## 实施进度（2026-11-01，按修订契约）

以 [v2 契约](../../axis-dynamics-v2-contract.md) 为准。归档原始任务中的“就地扩大 v1”、MatIEC 和旧尺寸不再作为执行依据。

- [x] 保持 v1 的 2576/5632 图像；独立 v2 通道、握手、导出标记及 4112/6656 图像。
- [x] MoveAbsolute A/D/J、Jog A/D/J、普通 Stop D/J 贯通原生单命令和扫描批处理；严格上限、零继承、Execute 锁存。
- [x] 非零初始速度/加速度、反向、停止距离和软限位约束；保护性停止仍独立优先。
- [x] ST 接口、编辑器与 MCP 指令元数据、按实际源代码选 ABI、真实 STruC++ 编译及包身份。
- [x] 拒绝回执与已接受动作进度分离；被拒绝的 Stop 不伪造原动作 Aborted/Done，不释放其他 Stop 的锁。
- [x] Linux 隔离构建原生 22/22 CTest，通过真实 ST 功能块运行测试与桥接传输测试。
- [x] 发布/恢复前校验能力、保留 v1、未启动 v2 的准备流程及版本化下载路由已实现。
- [x] 最后审查修正后的安装器、首次启用与扫描垫片完整 Linux 复测：轴桥64、部署137、安装/恢复/权限服务63项通过，无Linux跳过。
- [x] 发布固定校验值版本并保留可验证回滚备份；已切换实际 Runtime，并恢复原程序及 RUNNING 状态。
- [x] 正式 ST 经 v2 完成0.01圈往返、低速MoveVelocity、普通Stop中断和撤使能实机验收；最终报告PASS。

隔离构建：`/home/a/st-workbench-i210/axis-dynamics-v2-20261101`。已验证核心 SHA256：`f0de2ddd4c943231099b322041471c8748380175229a7582982b08680511a9b4`。当前设备已运行该核心，原工程恢复为 PLC RUNNING、轴 Disabled。实机证据和边界见 [验收记录](../../axis-dynamics-v2-verification.md)。原提案见[审核归档](2026-11-01-axis-dynamic-parameters-original-reviewed.md)，旧路径/旧尺寸及原始checkbox不代表实施状态。


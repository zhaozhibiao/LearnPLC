# 7103 轴反馈无效故障修复（2026-10-20）

现场程序为 examples/is620n-turn-test，活动程序 MD5 为 cfec57221a6be6a64c649b063059d3b8。
7103 表示测试启动时 MC_ReadAxisStatus / MC_ReadActualPosition / MC_ReadActualVelocity 至少一项 Valid=FALSE。

## 现场证据

- 真实 ST 采样：State=900、Active=FALSE，三个读取块 ErrorID=2004。
- 实时核心：总线就绪，轴 Disabled、无故障，没有已提交运动命令。
- 不含任何命令的轴桥读帧测试复现多次 100–186ms 延迟，随后超过 200ms 保护上限。
- 原服务每 10ms 原子重写能力文件并 fsync，磁盘同步阻塞其唯一的协议处理线程。

## 修复

- 10ms 后端只读采样保持原样。
- 状态变化立即发布；不变的能力文件每秒更新一次，仍满足部署端 5 秒新鲜度检查。
- 文件保留私有权限、所有权验证、原子替换、精确清单摘要和 PID 绑定；易失性心跳不再调用 fsync。
- 200ms 整帧超时、故障锁存及不自动重连/重试保护保持原样。
- 将两个试转输入写 FALSE 并读回，检查轴 Disabled 和活动程序身份后重建用户 PLC/轴桥服务。
- 未发出使能、回零或定位命令；EtherCAT 实时核心未重启。

## 验证

- 新增能力心跳测试，先确认旧实现缺少该机制，再实现修复。
- Windows 33 项测试通过（4 项 Unix 专用跳过）；Linux 33 项全部通过。
- 安装后真实采样：三个读取块 Valid=TRUE、ErrorID=0，State=0、DiagnosticCode=0；RunOnce/ParametersConfirmed=FALSE。
- 原始证据位于 artifacts/axis-feedback-diagnostic/，包括修复前后采样和持续观察记录。

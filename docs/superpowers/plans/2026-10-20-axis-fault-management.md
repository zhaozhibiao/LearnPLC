# 轴故障与工程管理分离

目标：轴 ErrorStop 不影响工程传输、编译、版本验证、在线诊断和变量监视。保护故障仍保留在轴上，阻止非预期再启动。

- Native runtimeReady 表示总线/IO/轴表及实时采样可用；新增 motionReady 表示所有轴当前具备基础运动就绪条件。各轴命令仍由实时核心逐轴校验。
- Hello/Snapshot 同时输出两种状态及原有逐轴 fault/state，不通过清故障伪装核心就绪。
- Python 轴桥保持故障反馈和诊断连接，对故障轴新的使能/运动请求在申请控制前拒绝，允许显式 Stop/Disable/Reset。
- 组态哈希、样本时效、后端归属校验保留。程序管理与轴故障解耦；物理总线重配仍要求停止和安全状态。
- 回归：故障时 native RuntimeReady=true/MotionReady=false，失去总线时两者 false；故障轴不发使能/定位，故障恢复无需重建只读会话。
- Linux 原生核心及 Python 测试通过后更新可复现补丁和来源哈希。现场更换核心须确保 PLC 停止、驱动器撤使能；不发起试转。

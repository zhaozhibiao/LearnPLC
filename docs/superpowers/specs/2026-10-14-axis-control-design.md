# ST 基础轴控接口

用户要求继续实现轴控接口，沿用已批准的同一工程/周期界面、自有 EcMotion 服务和非阻塞 ST 请求分层。在当前目录实施，不启动实体轴、不抢占现有控制权，不修改 D:/ethercat主站。

范围：MC_Power、MC_Reset、MC_Home、MC_MoveAbsolute、MC_MoveVelocity、MC_Stop、MC_ReadAxisStatus、MC_ReadActualPosition、MC_ReadActualVelocity。现有连续路径虚拟链保持；不把 ExactStop 接口标为连续加工。不声明完整 PLCopen 认证。源接口位置/速度由既有核心管理加减速，本阶段不增加未支持的动态参数。

工程轴按 Name Ordinal 排序，1..64，对应 AXIS_REF (UINT) 常量 AXIS_<Name>；包内 axis-manifest.json 保存固定编号、逻辑/控制器轴名、单位/限位/动态上限、PLC 周期。部署和私有工作进程必须绑定同一清单摘要。未知或动态未绑定轴引用在检查阶段拒绝；修改轴映射改变候选身份。

ST 每个轴只有一个命令槽及反馈槽。Execute 上升沿提交一次；普通命令不抢占忙轴，Stop 和 Power.Disable 可抢占，旧实例返回 CommandAborted。Stop Execute 保持时锁住普通新命令。MC_Power 是 Enable 状态请求，Status 取实际状态；速度运行 Busy/Active 持续，InVelocity 根据实际速度，直到停止/抢占。Done 来源于匹配请求及实际状态，定位必须匹配设备 MotionId 和终态。

通信格式（所有平台只支持 little endian 本机 IPC）：64 轴固定图像。
- CommandPacket: uint64 scan,flags（bit0 PLC scan success）,然后64个 Slot {uint64 request,kind; double position,velocity; uint64 flags(bit0 StopHeld)}。总2576字节。kind 1 Enable/2 Disable/3 Reset/4 Home/5 MoveAbsolute/6 Jog/7 Stop。
- FeedbackPacket: 64个 Slot {uint64 request,busy,done,error,aborted,enabled,homed,state,motion_id; double position,velocity;}。总5632字节。state 0 unknown,1 Disabled,2 Standstill,3 Homing,4 DiscreteMotion,5 ContinuousMotion,6 Stopping,7 ErrorStop；Jog 的 InVelocity 在 ST 根据实际速度计算。
- 错误2001 axis/mapping invalid,2002 scan watchdog,2003 ownership refused,2004 transport/protocol/feedback stale,2005 backend reject/fault,2006 slot busy/stop held,2007 invalid numeric/limit,2008 unsupported proof/capability,2009 request exhaustion。

扫描钩子只做固定复制和有界原子队列，不做 IPC/分配/文件。后台线程与私有 axis worker 交换二进制帧。Worker 通过既有 v4、4字节小端长度前缀 JSON Unix socket 连接 EcMotion，校验版本/请求ID/序列/帧长度；只有实际扫描计数推进才续租，断线/超时/PLC停止受控停止自有命令并释放控制权，不重连重放。Home 实机使用 native PlcScanBatch/AppliedCycle 证明，仿真允许同步直接请求。无法证明实际终态不得生成 Done。

Worker 必须显式选择 EcMotion 或 EcMotionSimulator，默认没有物理连接/自动启动；只读 Hello/Snapshot 后发布私有能力文件，扫描开始才按需要申请租约。物理能力与仿真区分，仿真不能让物理配置就绪。Axis worker 不改变 EtherCAT 实时调度；周期配置仍以既有阶段门禁为准，不能根据基础接口连接把周期设置显示已生效。

验证顺序：纯 ST/原生 mailbox/协议状态测试、真实独立编译器、隔离 Linux 编译与原项目仿真协议端到端。实机驱动参数和状态尚未验证时不自动下载/激活/运行。

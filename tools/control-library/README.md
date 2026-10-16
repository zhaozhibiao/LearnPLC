# Workbench ST 控制算法库 1.0.0

`control.st` 是权威实现，15 个功能块均为本项目原创 MIT 源码。函数 `WB_FINITE` 与 `WB_CLAMP` 是库内部辅助函数。编译测试使用独立进程中的 STruC++ 0.7.0；第三方编译器及其运行库仍各自遵循原许可证。

所有计算使用 LREAL；时间为秒。`Ts` 是调用者提供的本次采样间隔，默认 0.01，必须有限且大于零。名义任务周期不代表实际实时抖动。所有块都提供 `Enable`、`Reset`、`Ts`、`Out`、`Error`、`ErrorID`，默认 Enable=TRUE。除 ONOFF/TIME_PROPORTION 的 Out 为 BOOL 外，其余 Out 为 LREAL。除另有说明，其余未指定初值的数值为 0、BOOL 为 FALSE。

禁用数值块时保持输出及状态并清除诊断，不消耗时间；重新启用从最后成功采样继续。禁用期间需要重新初始化时，重新启用同时 Reset=TRUE。输入错误时不提交算法状态，保留最后有效输出。首次错误输出默认为 0；PID、增量 PID、串级 PID 在有限有效输出范围内对 0 限幅；积分器对有限 Initial 限幅，若 Initial 无效则对 0 限幅。范围自身无效时首次输出仍为有限的 0。Reset 也需要参数有效，不能掩盖错误。

ErrorID：0 正常；1 NaN/Infinity、超出保守数值范围或计算溢出；2 采样/增益/范围/时间参数错误；3 窗口错误。有限数值检查采用绝对值不超过 1E300 的保守范围。ONOFF 和 TIME_PROPORTION 出错使用 FaultOutput（默认 FALSE），禁用强制 FALSE。异常诊断只在本次调用有效，恢复有效参数后可继续。

## 接口与计算语义

表格省略公共参数，列出全部专用输入；实际类型、初值以 `control.st` 声明为准。

| 块 | 专用输入及默认值 | 行为 |
| --- | --- | --- |
| WB_PID | Setpoint, Measurement, Kp=1, Ki=0, Kd=0, DerivativeTime=0.01, MinOut=0, MaxOut=100, Manual, ManualValue, FeedForward, Reverse | 并联 PID，微分作用于测量值，一阶后向欧拉滤波；首次/复位仅 P+前馈。积分按输出边界截断，饱和时仅允许向恢复方向积分。手动跟踪，切自动首扫描保持原输出。 |
| WB_INCREMENTAL_PID | 与 WB_PID 相同 | 输出增量为 Kp×误差差值 + Ki×误差×Ts − 方向×Kd×滤波测量微分差值 + 前馈差值；输出限幅直接约束累积状态。首次/复位 P+前馈；手动/自动切换无扰。 |
| WB_CASCADE_PID | Setpoint, PrimaryMeasurement, SecondaryMeasurement, PrimaryKp=1/Ki=0/Kd=0/DerivativeTime=0.01, SecondaryKp=1/Ki=0/Kd=0/DerivativeTime=0.01, PrimaryMin=0, PrimaryMax=100, MinOut=0, MaxOut=100, InnerManual, ManualValue, PrimaryEvery=1, ReversePrimary, ReverseSecondary | 外环输出为内环设定值，另输出 SecondarySetpoint。PrimaryEvery=1..1000 个调用周期执行一次外环；外环 Ts 使用实际累计时间。首次和复位立即执行。内环手动时每扫描将外环跟踪到限幅后的 SecondaryMeasurement，停止外环积分；离开手动强制执行外环并无扰恢复。两个内部实例复制后提交，任何错误不污染已提交状态。 |
| WB_ONOFF | Setpoint, Measurement, Hysteresis=1, MinOnTime=0, MinOffTime=0, Reverse, FaultOutput=FALSE | 正作用误差大于正阈值开启、小于负阈值关闭，阈值相等保持，最短时间按 Ts 累计。首次启用允许立即开启；Reset 关闭并从零累计最短关断时间。错误重置驻留计时，恢复必须重新满足最短通断时间。禁用关闭并重置首次状态。 |
| WB_LOW_PASS | In, Tau=1 | 后向欧拉一阶低通，alpha=Ts/(Tau+Ts)。Tau 可为 0。首次/复位输出输入，避免初始化阶跃。 |
| WB_MOVING_AVERAGE | In, Window UINT=8 | 1..64 环形窗口。预热按真实样本数平均，另输出 Count。窗口变更或 Reset 清空后插入当前样本。逐项除样本数再求和，避免总和先溢出。 |
| WB_MEDIAN | In, Window UINT=3 | 1..9 奇数窗口，有界排序。窗口变更/复位清空再插入。预热为偶数个样本时选较小的中值，另输出 Count。 |
| WB_KALMAN_1D | In, Initial=0, InitialCovariance=1, ProcessNoise=0.01, MeasurementNoise=1 | 标量随机游走估计，过程噪声为每秒协方差增长率，Pminus=P+Q×Ts，K=Pminus/(Pminus+R)。Q/Pinitial>=0，R>0。首次/复位由 Initial/Pinitial 开始并立即更新当前样本；另输出 Covariance。 |
| WB_SLEW_RATE | In, RiseRate=1, FallRate=1, Initial=0, Track, TrackValue | 上升/下降分别限制单位/秒。首次从 Initial 移动一个 Ts；Reset 输出 Initial；Track 优先输出 TrackValue。速率可为 0。 |
| WB_LEAD_LAG | In, LeadTime=0, LagTime=1 | H(s)=(1+LeadTime×s)/(1+LagTime×s)，后向欧拉：y=(LagTime×yprev+Ts×x+LeadTime×(x−xprev))/(LagTime+Ts)。首次/复位输出当前输入。时间均可为 0。 |
| WB_INTEGRATOR | In, Initial=0, Hold, MinOut=-1E100, MaxOut=1E100 | 矩形积分，每有效扫描加 In×Ts 后限幅；首次也积分。Hold 保持但仍校验；Reset 输出限幅 Initial 并且该扫描不积分。 |
| WB_DERIVATIVE | In, FilterTime=0.01 | y=(FilterTime×yprev+x−xprev)/(FilterTime+Ts)，首次/复位输出 0 并采集当前输入，避免启动冲击。FilterTime=0 为差分。 |
| WB_SCALE | In, RawMin=0, RawMax=100, EngMin=0, EngMax=100, Clamp=TRUE | 两个范围必须严格递增，线性插值，默认截断范围；Clamp=FALSE 允许外推但禁止非有限结果。无历史状态，Reset 等同正常计算。 |
| WB_DEADBAND | In, Width=1, Hysteresis=0 | Width 为半宽，Hysteresis 在 0..Width；绝对输入大于 Width+Hysteresis 进入激活态，降至 Width−Hysteresis 以下退出。激活时输出 sign(In)×max(abs(In)−Width,0)，否则输出0。Reset 从非激活态重新判断当前输入。 |
| WB_TIME_PROPORTION | Duty, Period=1, FaultOutput=FALSE | Duty 为 0..1 占空比，自动限幅；0<Ts<=Period。首次/Reset 相位0，当前输出在归一化相位小于 Duty 时开启，然后累积相位。Phase 为下次扫描使用的归一化相位 [0,1)。无独立硬件 PWM 时钟，精度受 PLC 扫描间隔限制。禁用清零相位，错误保持相位。 |

PID Kp 单位为输出/误差，Ki 为输出/(误差·秒)，Kd 为输出·秒/误差。与常见 Ti/Td 表示法换算为 Ki=Kp/Ti，Kd=Kp×Td。Kp/Ki/Kd 必须非负，用 Reverse=TRUE 改变反馈方向；前馈不随 Reverse 改变。

## 实际 ST 生成代码测试

先在项目根运行：

```powershell
& 'C:/Program Files/nodejs/node.exe' tools/control-library/test.mjs
```

该命令通过项目已固定的 STruC++ 0.7.0 生成 `artifacts/control-library-test/generated.cpp` 和头文件，复制官方运行库头文件及独立 C++ 测试；不会用另一语言重写算法。再在有 g++ 的隔离目录内执行：

```sh
g++ -std=c++17 -O0 -Iinclude generated.cpp test.cpp -o control-test
./control-test
```

测试包括每块采样错误、首次错误输出范围、PID 正反作用/饱和/恢复/手自动切换/微分与前馈语义、串级、滑动窗口预热和变更、滤波解析向量、通断计时、PWM 周期、NaN/Infinity/中间溢出；固定种子 2000 次 PID 有界随机扫描及一阶过程闭环收敛。测试只调用独立生成的实例，不连接硬件。

# MCP 运动会话通道

`motion_gateway.py` 是 MCP 宿主使用的 Linux 辅助进程，自研代码沿用 MIT。需要 Python **3.11+**（验证 EcMotion/.NET 的高精度 ISO 时间戳）。仅使用标准库。部署时保留目录结构：`linux/mcp/motion_gateway.py` 和 `linux/axis/axis_worker.py`，复用后者清单、v4 编解码及实际采样/回零收据验证，不改变现有 PLC 工作进程。

启动参数由操作者固定，模型请求不能覆盖：

```
python3 linux/mcp/motion_gateway.py --manifest /approved/axis-manifest.json --controller-socket /run/ecmotion/controller.sock --backend EcMotion
```

不创建监听端口；stdin/stdout 为逐行 JSON。stderr 仅用于进程错误；不输出租约令牌。后端 `EcMotionSimulator` 必须显式选择，返回结果一直带该标记。单进程最多 4096 个请求身份、最多 4096 个操作，输入每行最多 65536 字节，排队最多 16 行、待输出最多 8 个响应；停止/释放优先处理并取消更早排队的动作，输入过载或输出背压不阻塞轮询，通道会停止控制。输入断开、协议异常、控制期限到达时尝试停止本会话拥有的轴并释放租约，不重连重放。

## 请求契约

每行必须带唯一的 `requestId`（1..128 个可打印 ASCII 字符）；响应始终返回对应 `requestId`、`success` 和 `state`。拒绝未知字段。

| action | 参数 | 结果 |
|---|---|---|
| capabilities | 无 | 能力、映射身份、后端；Continuous=false、MotionCycleApplied=false |
| status | 无 | 工程轴名称/单位、实际采样及 sampleCycle；不申请租约 |
| acquire | durationMilliseconds 可选，250..30000，默认1000 | 申请独占控制，最多控制30秒；已持有不延长期限；PLC/其他客户端占有则 Conflict |
| release | 无 | 停止拥有的轴并释放租约（控制器会应用安全状态，可能关闭轴） |
| execute | operationId、axis、kind；position、velocity、durationMilliseconds 按动作提供 | 返回 Accepted 及 operation；kind 为 Enable/Disable/Reset/Home/MoveAbsolute/Jog |
| operation | operationId | 操作当前状态及已验证的实际采样 |
| stop | operationId、axis | 对本会话控制轴发普通受控停止，可以抢占该轴忙动作 |

`axis` 必须精确匹配启动清单中的逻辑名称。指令身份和请求身份不得重复；读取既有结果应使用 `operation`。状态分别为 Accepted、Running、Completed、Failed、Aborted、Unknown；提交成功不等于运动完成。位置指令需要控制器返回新的 MotionId，终态采样必须匹配该 MotionId。物理回零通过现有 native scan-batch 动作收据验证 AppliedCycle，没有伪造 PLC 扫描数据或调用私有扫描帧接口。未提供实际 native 证明的物理后端拒绝相关能力。显式机械/旋转配置仍由既有门禁拒绝，离线改参数不等于设备已应用。物理轴的显式加速度、减速度、Jerk 上限不能由现有协议确认已应用，因此相关 Home/MoveAbsolute/Jog 返回 capability_missing；Stop/Disable 保持可用。所有物理完成判断还要求实际周期严格晚于提交前周期，避免已经回零/静止的旧采样误报新动作完成。

操作前验证实际状态新鲜度、映射、位置限位和可选速度上限。回零需要已使能静止轴；定位和速度运行需要已使能且已回零静止轴。停止是普通控制功能，不是硬件急停。

## 有期限速度运行

当前物理后端无法证明已应用的停止曲线与制动距离，**真实轴 Jog 返回 capability_missing**。最大减速度不是实际制动减速度保证，不能用它伪造停止距离确认。以下期限机制目前只用于明确标记为非物理的隔离仿真；真实轴需后端增加原生定时运动和可靠停止界限后再开放。

EcMotion v4 AxisCommand 没有独立的持续时间字段。本通道利用**控制器已有租约看门狗**提供设备侧最大运行期限：执行 Jog 后不再发送心跳，不允许其他动作延长该租约；本进程也持续轮询并在期限到达时尝试停止。宿主应在申请控制权时提供与速度动作相同的 `durationMilliseconds`。指令仅在实际租约剩余时长不大于请求时长时接受，并返回 `maximumRemainingMilliseconds`。这是最大期限，不能宣称严格运行恰好 N 毫秒；准备动作耗时及设备看门狗响应会影响实际终止时刻。需要精确加工时长时必须增加设备侧的原生定时运动能力。

示例（每条独立一行，申请后轴须已使能/回零；释放会应用安全状态，不能在 Jog 前偷偷释放重申请）：

```
{"requestId":"a","action":"acquire","durationMilliseconds":1000}
{"requestId":"b","action":"execute","operationId":"jog-1","axis":"Axis1","kind":"Jog","velocity":1,"durationMilliseconds":1000}
{"requestId":"c","action":"operation","operationId":"jog-1"}
```

## 验证

```
python3 -m unittest discover -s linux/mcp -p 'test_*.py'
```

协议测试验证无启动租约、PLC 冲突、请求去重、限位、错误实际采样、控制期限、MotionId 和物理回零证明。`test_simulator.py` 可使用仓库忽略目录 `artifacts/ecmotion-simulator-build/EcMotion.Simulator.dll`，通过原项目的隔离复制仿真器验证真正 v4 连接、使能/回零/定位、无输入时的 Jog 看门狗、输出背压下停止/释放和进程退出。不访问实体设备；缺少该隔离构建则明确跳过。Windows Python 缺少 AF_UNIX 地址支持时，仅测试使用忽略目录下的 Windows 原生 socket 适配夹具，生产通道运行在 Linux。

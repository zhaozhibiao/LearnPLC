# PLC 通信工作进程

本目录是原创 MIT 许可代码，仅依赖 Python 3.11+ 标准库。实际串口和原生 IPC 运行于 Linux；Windows 可运行协议及 TCP 回环测试。

```sh
python3 communication_worker.py --manifest /absolute/communication-manifest.json \
  --socket /run/user/1000/st-communication/candidate/worker.sock
```

`--capabilities` 可指定同一私有目录内的能力文件；默认是 `<socket>.capabilities.json`。socket 父目录不存在时创建为 0700；已经存在时必须属于当前用户且权限恰为 0700。socket、能力文件权限为 0600。只接受同 UID 的 AF_UNIX 客户端，同时仅一个连接。拒绝替换已存在的 socket，部署者应在确认旧进程退出后清理残留 socket。

一个进程对应一个不可变候选清单。**程序重启或切换时应同时重启此进程**：它跨连接保留最高扫描号和请求号来防止重放。不可让重置为 1 的新程序复用旧进程。启动仅校验清单并公布 IPC 就绪，不连接 TCP 或打开串口。无需 PLC 已连接才能启动。

## 功能和边界

- Modbus TCP/RTU 主站：功能码 01/02/03/04、05/06、0F/10、16/17（十六进制）。地址零基；协议寄存器、长度和数据均使用大端线序。RTU CRC 低字节先发，广播仅允许写入且不等待响应。
- TCP 客户端：显式连接、发送、接收、关闭。串口自由协议：首次操作打开，RS232 或用户指定的 RS485 自动方向转换器 / Linux KernelRts。无主站以外角色，没有监听 TCP 端口。
- 原始流、固定长度、分隔符、16 位大端长度前缀（不含前缀）、空闲间隔组帧。分隔符收取后剥离，发送时自动添加；长度前缀同理。Raw 接收返回当前可读的一个片段，不代表完整消息。固定长度发送要求恰好满足配置。单次数据最多 512 字节，二进制零字节保留。
- 最多 16 个通道；各通道最多一个后台操作，最多 16 个活动后台线程。TCP/串口等待不会阻塞扫描 IPC。一次请求最多执行一次，不重试、不自动重连；重复保持相同请求号不会重发。
- 同一串口配置不得重复；打开时使用非阻塞文件锁及 TIOCEXCL，要求字符设备。RTU 请求前观察 t3.5 接收静默；接收未完成帧跨 t1.5 静默拒绝，完整帧等待 t3.5 并拒绝尾随字节。19200 及以下按字符时长，高波特率使用 750µs / 1.750ms。用户态只能观察驱动交付的数据块，不能恢复 USB 已合并字节的物理时间戳；POSIX/USB 调度、电气方向和现场帧间隔仍需真实设备验收。
- KernelRts 使用 Linux `TIOCSRS485`；内核不支持会明确失败，不退化为自动方向。RTS 延时单位由微秒向上取整为内核毫秒。Automatic 表示用户已经选用自动方向硬件，不能检测其真实能力。配置的前后延时及 TX 队列排空受同一超时和取消约束。

## 原生接口

接口版本 `workbench-communication-v1`。所有整数均为无符号 64 位小端。

输入恰好 9360 字节：`<QQ`（Scan、Flags）后接 16 个 `<9Q512s` 通道槽：Request、Action、Function、Unit、Address、Quantity、ReadAddress、ReadQuantity、Length、Data。Flags 必须等于 1，只有严格递增的成功扫描才能更新看门狗或提交新请求。

输出恰好 9088 字节，16 个 `<7Q512s` 槽：Request、Busy、Done、ErrorID、Unknown、Acknowledged、Length、Data。未使用的字节清零。Action：1 Modbus 事务、2 TCP 连接、3 自由协议发送、4 自由协议接收、5 关闭。关闭可取消在途操作。FC17 的 ReadAddress/ReadQuantity 是读区，其 Address/Quantity/Data 是写区。

`Done` 代表本次协议操作完成；自由协议发送仅代表本地成功发送，`Acknowledged=false`，不能证明对端业务动作完成。Modbus 写仅在收到严格验证回显后 `Acknowledged=true`；广播写 `Done=true, Acknowledged=false`。Modbus 读数据去掉功能码、字节计数及 RTU 地址/CRC；写返回空数据。完成数据是不可变快照，通过扫描反馈复制，不直接改写任意 PLC 内存。

| ErrorID | 含义 |
|---|---|
| 6001 | 未配置的通道 |
| 6002 | 扫描失败、断开或看门狗终止在途操作 |
| 6003 | 超时 |
| 6004 | 传输、串口权限/驱动、取消或未就绪 |
| 6005 | 响应协议/CRC/组帧错误 |
| 6006 | 通道繁忙，新请求拒绝，原操作继续 |
| 6007 | 参数、功能码或通道协议不匹配 |
| 6008 | 已发送写入，结果不明；Unknown=true |
| 6009 | 请求号回退，拒绝重放 |
| 6201–6455 | 6200 + 设备返回的 Modbus 异常码 |

写入发送后遇到超时、断开或无效响应不会重发；读操作也无自动重试。传输/协议失败会关闭并毒化通道，必须使用新请求号执行显式关闭后才能重新发起。标准异常响应是明确失败，不视为未知写入。扫描看门狗为 `max(250ms, 3 × PLC 周期)`；独立 tick 不受不完整输入、网络等待或 IPC 对端不读取影响。失效或 EOF 取消后台、关闭句柄；之后只有新的成功递增扫描及新请求号才可重新执行。

能力文件每 100ms 原子更新，字段为 SchemaVersion、InterfaceVersion、ManifestSha256（原始清单字节 SHA256）、PID、WorkerReady、LastScan、ScanHealthy。WorkerReady 表示 IPC 已建立；**不代表远端设备可达或物理通信已验证**。部署校验还应检查文件 UID、权限、更新时间、进程存活及清单身份。

## 验证

安装辅助脚本 `install-client.py` 需要显式 `--apply`，仅在 Runtime 已停止时写入用户级服务及配置，不会自行启动服务。它一起分发 `coordinator.py`、`axis_gate.py`、`communication_gate.py`、`motion_cycle.py` 和固定 Runtime 源码摘要，不从候选包导入验证程序。安装前应审核变更并备份既有配置，安装后手动执行 `systemctl --user daemon-reload`。

候选已 stage 后，用已安装的 `communication-client/service_lifecycle.py prepare-candidate --build <64位BuildHash>` 准备相同清单的 Worker；此操作要求 Runtime 服务可查询、PLC 已明确停止（STOPPED/EMPTY），只建立本机 IPC，不连接通信对端，也不会代替操作者停止 PLC。再执行候选编译/激活；若放弃候选并恢复旧程序，应重启 Runtime 服务以恢复原 Worker。Runtime 服务每次启动的 ExecStartPre 按 active.json 重置匹配的 Worker；ExecStopPost 停止 Worker。尚未安装此后端时，上位机明确显示通信未就绪，禁止激活，不能把编译成功当作现场通信已接通。

`verify_candidate.py` 用固定 Runtime 源码在新 `/tmp` 目录编译实际候选库，不加载或激活它。`verify_e2e.py <候选.zip> --harness tools/communication-bridge/test_e2e.cpp` 使用实际 ST、原生桥及随包 Worker 连接独立 TCP 回环对端，验证二进制回复、保持 Execute 不重发及扫描看门狗断开。它仅接受开发者生成的可信测试包，并需要 g++。

```sh
python3 -m unittest discover -s linux/communication -v
```

测试使用独立构造的 TCP 对端、Linux PTY 和 AF_UNIX；覆盖真实分包、粘包、二进制、CRC、广播、回显/数量边界、事务号、超时、写结果不明、保持请求、繁忙、扫描回退和看门狗。Linux 专项在 Windows 明确跳过。测试不连接物理串口、PLC Runtime 或现场 IO。

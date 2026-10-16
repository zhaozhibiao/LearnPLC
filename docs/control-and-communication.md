# 控制算法与 PLC 通信

本期支持 Linux 端 Modbus TCP/RTU 主站、RS485/RS232 自由协议点对点收发和 TCP 客户端。通信在后台进程执行，ST 扫描只交换有界请求及反馈。没有 Modbus 从站或 TCP 服务端。

## 工程站操作

1. 在“硬件组态 → PLC 通信通道”添加通道。网络组态页显示同一配置的摘要，并可返回配置。名称例如 `Meter`，生成常量 `COMM_Meter`；通道最多 16 个，按名称排序编号。
2. TCP 填目标固定 IP 和端口；串口填 Linux 设备路径、波特率、校验、停止位。RTU 无校验使用 8N2。RS485 的 Automatic 适用于自动方向转换器，KernelRts 需要 Linux 驱动支持。同一串口不能分配给两个通道。
3. 在右侧资源库的“通信”类别拖入相应通道的指令。默认 `Execute=FALSE`，需要程序产生上升沿才提交；每次命令完成后先复位 Execute，再发下次命令。
4. 在“控制算法”类别拖入 PID、滤波、积分等功能块。参数中的 `Ts` 单位是秒，模板按当前工程任务周期生成；之后若改变任务周期，也应同步更新已插入调用的 Ts。先设置量程、方向和增益，再打开 Enable。
5. 检查、编译打包并下载。通信候选必须有匹配清单的 Linux Worker 就绪，才能激活。现有下载流程继续保持原 PLC 运行或停止状态。

所有通信数据均为 `ARRAY[0..511] OF BYTE`，长度单独指定；文本编码由用户程序处理。Modbus 地址从 0 开始，寄存器使用大端线序。跨寄存器浮点数及 32 位数的字序由用户按设备文档转换。

功能码和报文边界依据 [Modbus 官方应用协议 V1.1b3](https://www.modbus.org/file/secure/modbusprotocolspecification.pdf) 核对。

## ST 接口

`WB_MB_REQUEST` 在 Modbus 通道发起一次完整事务。FunctionCode 为十进制：1/2/3/4、5/6、15/16、22/23。Address/Quantity 是主读写区；23 的 ReadAddress/ReadQuantity 是读区。DataLength 是写入字节数。读取返回去掉协议头的字节数据。

TCP 使用 `WB_TCP_CONNECT`、`WB_TCP_SEND`、`WB_TCP_RECEIVE`、`WB_TCP_CLOSE`。串口使用 `WB_SERIAL_SEND`、`WB_SERIAL_RECEIVE`，首次操作按组态打开。组帧支持 Raw、Fixed、Delimiter、Length16、Idle；原始流一次返回的片段不保证是完整业务报文。

`WB_COMM_CLOSE` 可关闭任意协议通道并取消在途操作。传输故障后先显式关闭，再用新的上升沿重新发起；Modbus 与串口资源也提供此恢复指令。关闭不能撤销已经发送到现场的写入。

公共反馈为 Busy、Done、Error、ErrorID、Unknown、Acknowledged、RequestId、ReceivedLength、ReceivedData。Done 是本次通信操作完成；TCP/串口发送成功不代表远端设备执行了业务动作。RTU 广播写没有应答，Acknowledged 为 FALSE。写入结果 Unknown 时应查询现场状态，禁止自动重发。Worker 不自动重试或重连。

控制库包含位置式 PID、增量式 PID、串级 PID、双位控制、低通、移动平均、中值、单变量卡尔曼、变化率限制、超前滞后、积分、微分、量程转换、死区和时间比例控制。全部接口、公式、复位及异常行为见 [控制库说明](../tools/control-library/README.md)。算法参数需要按实际对象整定，自动测试没有替代现场整定。

## Linux 和 MCP

Worker 的权限、启动命令、协议与错误表见 [通信后端说明](../linux/communication/README.md)。候选与 Worker 必须保持同一生命周期，切换或重新启动程序时重启 Worker，避免复用旧扫描号和请求号。此源码更新本身不会安装或重启现场 Runtime。

MCP 新增 `communication_configure` 配置草稿，以及 `control_communication_catalog` 返回真实库接口与模板。`st_insert_instruction` 支持这些模板 ID；配置、插入仍要求当前 expectedRevision。通信写入由已编译部署的 ST 程序执行，MCP 没有任意串口或主机写入代理。

自研 ST 算法、通信后台及原生桥采用 MIT；没有新增第三方通信库。独立编译适配工具继续按现有 GPL 工具许可供应。完整发行义务与既有发行门禁见 [许可记录](licenses.md)。

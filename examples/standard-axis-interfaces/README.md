# 标准工程轴接口：简洁往返

打开 `StandardInterfaces.stproj`。这是目前推荐的接口示例；原 library-axis-turn 工程作为旧写法对照保留。

主程序只保留动作顺序：正转 1.5 圈 → 等待 2 秒 → 反转 1 圈。到位直接使用 `ForwardMove.Done` / `ReverseMove.Done`；故障记录功能块原始 `ErrorID`，没有重复判断位置误差、速度阈值、71xx 错误和停止确认计时器。位置范围及速度保护均由工程硬件组态和实际核心执行。

1. 下载匹配硬件后下载程序，默认不使能、不回零、不运动。工程单位 rev，减速比 1:1，速度默认 0.5 rev/s（30 rpm），工程限速 1 rev/s，位置范围 -2..+2 rev。
2. `PowerEnable=TRUE`，观察 `Power.Status`。回零时让 `HomeExecute` 从 FALSE 到 TRUE，观察 `Home.Done` 后写回 FALSE；此工程为当前位置建立临时原点。
3. `CycleExecute` 上升沿启动一次，保持 TRUE 不会重复。观察 `ForwardMove.Busy/Done/Error/ErrorID/CommandAborted`、`ReverseMove` 的同名接口。CycleExecute 持续为 TRUE 时，业务 Done 保持；提前撤销只撤销启动信号，不取消已开始的流程。
4. `StopExecute=TRUE` 停止，观察 `Stop.Done`；保持 TRUE 时 `Stop.Busy` 仍为 TRUE 且轴保持锁定。写回 FALSE 后才允许下一条运动。取消结果显示 `Cancelled`，不会假报往返完成。
5. 错误时业务 `ErrorID` 保留首先观察到的功能块错误。`ResetExecute` 先 FALSE 再 TRUE，等待 `Reset.Done` 并释放启动/停止输入后退出故障步骤，再把 ResetExecute 写回 FALSE。Reset 不自动使能或回零。

接口按支持的 PLCopen 单轴语义实现，自研 MIT 代码；不声称完整 PLCopen 认证，也不支持基本轴命令缓冲/混合。`MC_ReadStatus.Homed` 为扩展输出；旧 `MC_ReadAxisStatus` 保持兼容。

`mcp-client` 通过真实 MCP 创建、写入、保存及编译本工程。`verify_sequence.py` 仅运行生成的 C++ 和内存反馈模型，没有设备连接。新库需重新编译、下载才进入现场 Runtime；此处构建不会更新设备。

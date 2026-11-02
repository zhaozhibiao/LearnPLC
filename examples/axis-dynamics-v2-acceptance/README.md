# 轴动态参数 v2 小行程验收工程

打开 `Acceptance.stproj`。工程沿用现场已确认的硬件组态，指令参数为：定位0.01圈，速度上限3 rpm，A/D=0.2 rev/s²、J=1 rev/s³；Jog为0.005 rev/s。所有命令初始FALSE，不会因启动PLC而自动使能。

PowerEnable使能后，应先确认轴静止，再触发HomeExecute，以已组态的当前位置方式建立临时原点；完成后撤销HomeExecute。CycleExecute上升沿执行正向0.01圈、等待2秒、返回。StopExecute执行普通停止。JogExecute为独立低速速度运动，由StopExecute停止；结束时撤销JogExecute和StopExecute，再撤销PowerEnable。

该工程已通过低速实机验收。记录与限制见 `docs/axis-dynamics-v2-verification.md`。正常工作工程未被此示例替换，测试结束已恢复原工程及RUNNING状态。

# IS620N 空载试转工程

本工程通过本项目真实 MCP stdio 服务创建、组态、写入 ST、保存并调用 STruC++ 0.7.0 编译，调用记录见 `mcp-evidence.json`。不是手写一个 JSON 工程后声称使用了 MCP。

## 实际设备

- Linux：`a@192.168.1.31`，管理网络保留在原网卡。
- 专用 I210：`b4:96:91:77:df:0f`，IgH 1.6.13，`ec_igb`，PREEMPT_RT。
- 从站 0：IS620N，Vendor `00100000` / Product `000C0108` / Revision `00010001`。
- 电机：用户提供铭牌型号 `ISMH1-10B30CB-U230Z`，已脱离传动、空载且固定。
- U2 编码器：20 位、1,048,576 计数/转；实际读取电子齿轮 `6091:1/:2 = 1/1`。
- 无原点开关；用户明确同意本次试转采用当前位置临时原点，驱动器方法 35。

编码器型号、可配置 `1600/1A00` PDO、`606C` 的 RU/s 单位见汇川原版 [IS620N User Guide A04, 19010456](https://www.remontservo.ru/toljatti/pages/publications/article-475/img-article/is620n_series.pdf)，印刷页 8、69、90、98、100、126。此链接为原厂编写手册的镜像，未把文档作为程序代码导入。

原始只读报告：`hardware-probe.txt`、`hardware-profile-probe.txt`。这些报告的 PREOP 状态是启动核心前的时间点。实时轴能力及状态以最新 MCP 实机记录为准。

## ST 程序

用工程站打开 `IS620N.stproj`。`Main.st` 是便于阅读的同内容副本；实际工程使用 `.stversions` 下由 MCP 保存的源文件。

`RunOnce` 和 `ParametersConfirmed` 默认均为 FALSE；冷启动不发送使能、撤使能、回零或位移命令。显式启动后执行：

1. 验证实际反馈、静止、参数和位置范围。
2. 使能；仅在专用方法 35 核心配置已核实后，建立当前位置临时原点。
3. 锁定实际起点，前进 0.01 转（3.6°），速度 0.01 转/秒（0.6 rpm）。
4. 等待、返回锁定起点、等待、停止、撤使能。
5. 只有回程和最终撤使能均证实时才设置 `Passed`。保持 RunOnce=TRUE 不重复启动。

取消、超时、反馈丢失、回零失败、异常位移及运动失败走停止/撤使能路径。结果无法确认时锁存 `OutcomeUnknown`，不报告通过。

本工程使用基本轴映射到**预先核实组态的** `Axis1`。机械换算、实际加减速曲线和保护参数来自专用核心配置 `linux/ethercat-i210/native-profile/config/is620n-machine.json`，不冒充 MCP 已实现机械参数下发。MCP 轴清单仅声明当前接口能确认的基本位置/速度范围，显式机械应用的原有门禁保持不变。

## 验证与实际执行的区别

- ST 编译及 16 项纯内存场景验证已执行，测试源为 `test_sequence.cpp`、`verify_sequence.py`。
- 实体控制由 `mcp-motion-client` 调用真实 MCP 运动工具执行；完整往返已通过，原始成功记录为 `mcp-motion-success.json`。初次发现的问题及中断结果另存 `mcp-motion-attempt-*.json`。
- 同一份 MCP 编译的 ST 代码也已在独立验证进程中通过真实轴桥完成一次往返，保持 RunOnce 后没有重复运动，结果为 `ST_PHYSICAL_ROUNDTRIP_PASS count=1 disabled=true`。记录见 `st-motion-evidence.json`。
- **没有替换现有 OpenPLC 的运行程序**。这次 ST 实机验证是独立执行所生成的 PLC 代码，不能据此声称已完成工程站一键下载/激活这份测试工程。
- 最终 `hardware-final-state.json`：实际驱动器 Disabled、速度 0、报警 0、总线就绪、无软件超速/跟随误差/限位故障。`mcp-final-state.json` 确认没有持有控制租约。
- `mcp-motion-client` 默认只读。`--execute-once` 会真实使能/回零/运动，只能在已核实的本次空载组态下使用。

重新通过 MCP 写入/编译（工作目录为仓库根目录）：

```powershell
dotnet run --project examples/is620n-test/mcp-client/Client.csproj -c Release
```

Linux 上对 MCP 构建输出进行无硬件场景验证：

```sh
python3 examples/is620n-test/verify_sequence.py /path/to/mcp-build/output
```

`verify_live.py` 默认只编译真实 ST/轴桥验证程序。它的 `--execute-once` 会打开固定物理核心套接字，核对已组态配置哈希和实体能力后真实执行一次 ST 测试，不能用于未确认的机械连接。

MCP 连接配置使用忽略目录中的 `artifacts/mcp-is620n.json`，只允许逻辑轴 `IS620N`、实体后端 `EcMotion`，不授予 PLC 下载/启停权限。密钥与主机指纹文件不放入示例源码。

## 专用核心

工程站下载通道的实体轴绑定由 `linux/ethercat-i210/configure-is620n-runtime-axis.py` 配置：部署协调器显式使用 `axis_backend=EcMotion`，Runtime 环境指向用户私有轴桥接套接字，桥接服务再连接 `/run/st-workbench-is620n/controller.sock`。桥接启动只查询状态，不使能、不运动；不自动重连或重放命令。候选轴清单必须与已核实的配置完全一致，真实核心退出后能力会撤销，不能据已保存的配置宣称实体轴就绪。

后续发现 PLC 客户端 EOF 会让旧轴桥直接退出并移除套接字。会话生命周期修正已通过 32 项无硬件测试并部署：正常断开只清理旧 PLC 会话并保留监听，同一实体连接上重新核实能力；不重连失效的实体核心、不重放任何旧请求。失去轴通道的 PLC 已正常切至 STOPPED，活动版本未更换。现场复核已通过：连续 3 次零命令 PLC 会话均正常结束，桥接 PID 不变、套接字保留，真实轴能力继续就绪，两个候选门禁均通过。记录为 `runtime-axis-lifecycle-live.json`，该检查不启动 PLC、不获取轴控制权、不发送运动命令。

本次下载候选 `3CB704…` 的轴清单已比对一致；配置修复期间专用核心到达原定 30 分钟时限（systemd `Result=timeout`）。操作者已重新启动核心，私有轴桥接随后接通；使用实际部署门禁检查通过，并重新编译同一候选得到 `axis_ready=true`，记录见 `runtime-axis-ready.json`。Runtime 仍为 STOPPED，活动版本未改变，未激活候选、未启动 PLC、未发送运动命令。该历史核验之后又发现 30 分钟临时限制导致通道退出；现已将 `RuntimeMaxSec` 改为 `infinity` 并实际重载确认，持续运行至手动停止或主机重启，未配置开机自启。通信看门狗及轴保护保持不变。

适配在独立源码副本中完成，未修改 `D:/ethercat主站` 原项目。核心改动保存在 `linux/ethercat-i210/is620n-native.patch`，原文件哈希记录在 `is620n-native-source.json`；配置、证据验证器和附加测试在 `native-profile`。

历史运行配置使用 CSP RxPDO `1600`（6040/607A/60FF/6060）。2026-10-23 候选修复将 IS620N 的速度通道改为 `0x60B1` 速度偏置，并通过工程站编译器生成 RxPDO `1600`（6040/607A/60B1/6060）、TxPDO `1A00`（603F/6041/6064/606C/6061）；新映射已在禁用轴的真实启动中达到 OP/WKC 完整，未进行新版本运动验收。

低速采样实测速度按约 0.015 转/秒阶跃跳变。实际超速保护设为 0.05 转/秒（3 rpm），指令速度仍限制为 0.01 转/秒。核心新增的显式 `positionStabilityCompletion` 选项仅在本配置开启：轨迹必须处于 Hold，所有位置采样必须在目标 ±0.0001 转内，并且 100 ms 内位置变化范围不超过 0.00005 转。未开启时保留原有速度连续到位算法；原始速度反馈、回零速度联锁及超速保护不改变。

受控运动超速监控根据驱动器状态字的真实 Operation Enabled 位（bit 2）执行，覆盖 Quick Stop Active（0x0007），不能仅根据软件的使能请求关闭。已撤使能的空载磁阻定位不会锁存为受控运动超速；原始反馈保持不变，非有限反馈仍报错，已有故障仍锁存。两条 Enable 入口都会拒绝实际转速超过保护上限的转子。

MCP 实机测试在维持使能时核对返回位置误差 ≤0.0001 转。撤使能后空载转子不再保持闭环位置，可能落入磁阻定位点；最终单独记录这个未使能位置，并验证连续静止和 Disabled。不能将撤使能后的磁阻位移当作控制定位精度。

启动脚本需要操作者 sudo：`/home/a/st-workbench-i210/start-is620n-commissioning.sh`。可执行程序安装到 `/opt/st-workbench-is620n`，套接字与临时配置位于 `/run/st-workbench-is620n`。核心每次默认未使能、不运动；服务持续运行至手动停止或主机重启。停止专用测试核心：

```sh
sudo systemctl stop st-workbench-is620n.service
```

这些示例、测试和适配补丁的新增代码采用 MIT；既有 OpenPLC、编译器和 IgH 的授权分别沿用项目现有授权清单，不能因为接入了 MIT 代码就将它们改称 MIT。

最终审查新增 Quick Stop Active 超速回归：旧版本测试明确失败，修正后 9 项 CTest 通过。新核心 SHA256 为 `0919aeb1864d7b5ea9d0ff869f87379d5c3786a7c306a350ea2d11b1c886fdcd`；此前实体往返记录保留其实际执行的 `cc478b…` 版本，不将离线回归冒充新版本实体运动测试。新版本已加载，服务运行路径及安装后二进制 SHA256 一致；`hardware-final-revision-state.json` 保存 40 个只读样本，全部 Disabled、无故障、总线就绪；39 个速度为 0，1 个原始速度样本为 -0.0149994 转/秒，最后速度为 0。此次未发送任何使能或运动命令。

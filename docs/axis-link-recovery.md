# 轴控接口、故障恢复与链路验证

日期：2026-11-02（Asia/Shanghai）。本轮保留Python协调桥和C++实时核心，不改变轨迹、EtherCAT周期、机械参数或运动保护阈值。

## 本轮完成

- v1/v2原生通信都使用整帧截止时间（设备现有配置200ms），零碎回包不能不断延长等待。超时、截断、错序、重复字段、过深JSON、数值溢出和缺失有效指令回执都会关闭并永久撤销该连接。
- 运动结果未知不重试；已断开的连接不能再发送Stop、Release或原动作。关闭连接及既有核心租约保护负责收尾；新连接必须由明确的新通道会话建立。
- 有效的状态/参数拒绝保留连接；驱动故障不等同于通信断开，仍能读取当前反馈和继续合法恢复操作。
- 通道首次故障记录固定原因及恢复建议，不记录命令载荷、租约令牌或密码。工程站下载/就绪错误会带中文原因，只有确实需要重建通道才提示恢复。
- 增加原生往返次数、失败次数和耗时统计；统计变化不触发每扫描周期写盘，继续按1秒心跳发布，关键状态撤销立即发布。

## 操作入口

仅查看链路，可在设备上运行发布目录中的只读探针：

```bash
python3 /home/a/st-workbench-i210/axis-link-20261102/linux/axis/axis_link_probe.py --samples 200 --interval-ms 10
```

该工具只发送Hello和ReadSnapshot，不获取租约、不使能、不回零、不写变量。PASS表示读取链路及反馈时间有效，是否能运动另看RuntimeReady、IoReady和MotionReady。

显式恢复当前提供普通用户命令入口，未新增工程站按钮：

1. 在工程站停止PLC，撤使能并确认所有轴静止。驱动故障可以保留，但必须能取得实际撤使能、静止及安全IO输出的新鲜反馈。
2. 以用户a执行恢复工具，传入当前活动工程的完整BuildHash：

```bash
python3 /home/a/st-workbench-deployment/axis_recovery.py --expected-build "当前活动工程的完整BuildHash"
```

不需要sudo。它核对程序库、已应用硬件指纹、轴映射、核心PID和配置规定的静止窗口，只重启固定Runtime用户服务及轴通道。重启后重复物理状态检查，成功返回`plc_state=STOPPED`、`motion_restarted=false`。随后由操作人员决定是否运行PLC。

运行中、身份不一致、总线不可读、反馈过期/冻结、轴未实际撤使能、仍在运动或存在未完成程序事务时，恢复被拒绝。恢复结果未确认时不会自动重试或自动运行，也不清除驱动报警。

## 诊断字段

私有能力文件新增：`FailureCode`、`FailureReason`、`RecoveryRequired`、`RecoveryAction`、`LinkMetrics`。原v1/v2图像和ST接口没有变化。

`LinkMetrics.Requests`仅保留有限类别的Count/Failures/LastMilliseconds/MaxMilliseconds/MeanMilliseconds。Failures包含有效业务拒绝，不能单独用来判断断线；应结合RecoveryRequired及连接State。查询次数与耗时是管理层统计，不是实时插补周期、EtherCAT报文延迟或驱动器位置响应时间。

## 验证证据

隔离Linux目录：`/home/a/st-workbench-i210/axis-link-20261102`。

- Linux轴桥回归84项通过，无跳过；其中真实套接字注入超时、慢速部分帧、截断和身份错误，验证只发送一次、连接立即失效，明确的新只读会话使用新序号。
- Linux部署/恢复回归150项通过，无跳过，包括故障但实际撤使能的恢复、反馈鲜度、核心/程序身份变化、恢复后物理状态不安全的拒绝。
- 独立代码审查及真实STruC++新旧接口打包测试通过；新候选在Linux编译且导出校验通过。C++扫描桥未改动，仅增加只读测量工具。
- 显式恢复流程已在设备验证：恢复结果保持PLC停止；发布流程随后单独恢复原先RUNNING状态。不是对旧运动的重放。

实机只读测量：

| 测量 | 样本 | 平均 | P99 | 最大 |
|---|---:|---:|---:|---:|
| Linux原生只读查询往返 | 200 | 0.174ms | 0.201ms | 0.205ms |
| 实际C++桥→Python零动作帧往返 | 200 | 0.874ms | 1.393ms | 1.400ms |

原生反馈最大采样年龄约1.145ms。C++桥测量走独立私有通道，全部动作请求号为0，工作进程记录仅含Hello/ReadSnapshot，无控制租约请求。它包含桥接线程排队及通信，仍不代表运动指令到伺服的延迟。

发布后的实际v1通道观测：查询、心跳和初始化指令均无失败；1.5秒只观察到2次能力文件更新，没有高频写盘。生产程序按原逻辑持有自己的控制租约，与只读探针分离。

证据文件：`artifacts/axis-link-20261102/`下的`native-probe.json`、`bridge-probe.json`、`live-diagnostics.json`、`recovery-result.json`、`publication-result.json`和`final-state.json`。

## 最终设备状态与回退

原活动工程保持`5E18C2D4EDADEF29952DCE41D390392EA239D84021826890600B186091F3B0CE`，程序库SHA256保持`2921d4ef3b97b2d4e5b0b44d9cb1a2ff3af464a217e8f312b3542dd8d376939d`。
PLC已恢复RUNNING；轴Disabled、无故障、无运动。C++实时核心未更换。

v1已实际运行新工作进程；v2已安装与新候选身份匹配的工作进程，隔离只读扫描桥验证通过，未替换原活动程序。候选Build为`1496BE4C11E8D3C0710CB508D9C75A764EB7E814F170E16C8306FB6947BF166A`，后续下载规划已验证正常。

旧工作进程、v2清单/准备记录及管理模块备份位于设备`/home/a/st-workbench-i210/axis-link-20261102/publication-backup`。回退前同样需停PLC、确认轴实际撤使能并核对备份校验值，再恢复同组文件和通道；不能在运行中只换一个文件。

本轮未在真实核心上强行断开活动运动连接，未执行定位/点动或高速、带载、整机重启验收。故障注入在隔离环境完成；实机执行只读测量及受控通道恢复。既有低速速度反馈量化、InVelocity判据和Home/Reset准入边界未在本轮修改。

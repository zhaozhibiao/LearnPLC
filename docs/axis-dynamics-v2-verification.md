# 轴控动态参数 v2 验收记录

日期：2026-11-01。设备：`a@192.168.1.31`，IS620N / ISMH1-10B30CB-U230Z。

## 已实现

- `MC_MoveAbsolute`、`MC_MoveVelocity`：Acceleration / Deceleration / Jerk；`MC_Stop`：Deceleration / Jerk。
- Execute 上升沿锁存；Busy 时改输入不改变当前动作。0 继承明确的轴默认值，非法值及超工程上限值拒绝，不静默截断。
- v1 保持原2576/5632字节接口；v2独立握手、导出、通道，4112/6656字节。拒绝回执不覆盖已接受动作的进度、Done证明和Stop持锁。
- ST补全/指令库/MCP元数据、实际编译与打包、Linux工作进程、实时单命令和扫描批处理、版本准入、激活后轴桥恢复贯通。
- 原保护性停止、撤使能、时序、驱动跟随及限位保护未放宽。

## 构建与回归

Linux隔离目录：`/home/a/st-workbench-i210/axis-dynamics-v2-20261101`。

- 原生Release：22/22 CTest。
- Linux轴工作进程与用户安装：64项通过，无跳过。
- Linux部署与恢复衔接：137项通过，无跳过。
- Linux权限安装/组合回滚12项、硬件应用/恢复38项、权限服务13项通过。
- 真实STruC++生成代码运行测试、实际扫描垫片集成、v1/v2传输测试通过。
- Core、MCP全套自测及真实STruC++包测试通过；App构建0警告0错误。App构建关闭了受目录权限影响的Avalonia统计任务，不改变产品编译内容。
- durable overlay、构建快照与source-manifest全部校验一致。

## 已部署

核心：`/opt/st-workbench-ethercat/ecmotion-rt-f0de2ddd4c94`。
SHA256：`f0de2ddd4c943231099b322041471c8748380175229a7582982b08680511a9b4`。

实际Hello确认：`workbench_motion_migration_v1`、`workbench_axis_trace_v1`、`workbench_axis_move_profile_v2`、`workbench_axis_velocity_profile_v2`。
硬件指纹保持：`6db0f01ba15b29c003b07d10e2fcbe06d87fd50721604d8cf62e99f62e2a212e`。

首次迁移前完整备份：
`/var/backups/st-workbench-runtime/20261101T220456.525994Z-748332fd5f14470d8dd27159e3e4050c`。
配套broker备份在`/var/backups/st-workbench-axis-dynamics-v2/`同名目录。
修正Unix LF脚本启动格式后另留备份：
`/var/backups/st-workbench-runtime/20261101T221805.682036Z-4b9fedc9f4dd4ede83611795de0cba93`。
恢复入口：`/usr/local/libexec/stworkbench-axis-dynamics-v2-rollback`，操作流程见发布目录README。

用户态模块、原工程源代码及目录权限备份位于隔离目录，未记录密码或令牌。原sudo临时授权文件未更改。

## 实机结果

工程：`examples/axis-dynamics-v2-acceptance/Acceptance.stproj`。
最终测试Build：`3D9E9C77865C85963B7A1AD3EBC0480908C3B21B01E6EFFB5299EFFF762949F9`。
报告：`artifacts/axis-v2-device-acceptance/final-acceptance.json`；设备原件：`st-acceptance-20261101-173822.json`。

实际使用正常暂存→Linux编译→组态规划/应用→程序激活流程，未手工替换PLC库。
正常运行PLC后，仅通过绑定MD5的在线BOOL写入触发标准功能块，未逐周期写伺服目标。

- 0.01圈正向/返回；速度上限0.05 rev/s（3 rpm），A/D=0.2 rev/s²，J=1 rev/s³。
- 两个Done端点误差：+0.000085106和−0.000082970圈，均在工程0.0001圈容差内。
- 第二次定位中执行普通Stop，确认取消、停止完成和释放。
- `MC_MoveVelocity`设定0.005 rev/s，确认Busy、ContinuousMotion及实际位置前进，再由普通Stop停稳。
- 撤使能成功；测试最终PASS，采样788条，PLC进程在运动测试期间保持不变。
- 采样最大驱动跟随误差0.001983643圈；这是本次低速记录，不能据此推导高速阈值或额定性能。

测试结束后，已恢复原工程Build：
`5E18C2D4EDADEF29952DCE41D390392EA239D84021826890600B186091F3B0CE`。
原库SHA256保持：`2921d4ef3b97b2d4e5b0b44d9cb1a2ff3af464a217e8f312b3542dd8d376939d`。
PLC恢复原RUNNING状态；新核心就绪；轴Disabled且无故障。

## 本轮修正与边界

发现并修复了：普通Stop/Jog包含不适用的null位置/速度字段，引发原生JSON解析失败，进而使worker保护性释放控制的问题。新worker省略这些字段，加入了严格原生形状回归测试。新包和已安装worker哈希一致。

首次回零在Power确认后1.7ms即提交，出现2005；另一次无故障复位请求也被现有准入拒绝。原生Home/Reset门槛未改，未扩大容差。验收流程在无故障启动时省去多余Reset，并在Home前验证组态规定的100ms位置静止窗口及最新速度门槛，随后回零成功。驱动速度反馈存在约0.015 rev/s量化跳变，不能把这次成功视为所有回零场景可靠性的证明。

MoveVelocity本轮验证了动态命令接受、实际运动和Stop中断；未验证其稳速精度。测试在加速阶段停止，InVelocity未置位；现有InVelocity采用固定极窄速度比较容差，与该驱动的速度量化分辨率并不匹配，后续应独立改为工程明确配置的速度到达判据。

本轮未执行1000/3000 rpm、连续路径、多轴联动或重启整机验收。组合回滚完成校验与自动化测试，未为演示而实际回退已通过的现场版本。


最终下载门禁已在设备实际部署目录验证：不兼容早期v2工作进程源码的候选在只读就绪检查阶段被拒绝，PLC未被停止；新的已验证ST程序能复用已准备的同版本轴桥。任务周期/逻辑映射变更通过正常停止和静止检查应用，并原子更新准备记录；损坏来源或写入失败保持拒绝状态。最终137项Linux部署测试均通过，原工程仍RUNNING。

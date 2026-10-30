# 运动核心开源实现对照审查（2026-10-29）

## 范围和现场状态

停止进一步扫参数、升速。最新一组100%内部前馈在12 rpm完成两次往返，核心峰值误差0.0024293/0.0028057 rev；收尾15秒连续原始零速判定失败，自动参数恢复因此未执行。随后独立核验未使能、位置窗口稳定后，恢复2008:14=0，读回2005:14=1，603F=0，6041=0x8250。恢复证据 artifacts/cyclic-trace-20261023/restore-after-window.json。总线服务 st-workbench-is620n-tuning-bus 常驻，当前不使能不运动。没有替换生产安装。

基线12 rpm、a=0.25 rev/s²时，0.005 rev/20ms跟随保护触发；50%内部前馈一次通过，峰值0.0037327 rev。上述结果显示前馈有影响，不足以证明核心完全正确或高速能力已验证。未试3000 rpm，物理一转独立校验仍未完成。

## 第一方参考

- LinuxCNC CiA402接口源码：https://github.com/dbraun1981/hal-cia402/blob/main/cia402.comp 。read_all/write_all分离，pos_scale和velo_scale独立，状态字与模式反馈参与控制字输出；GPL标注在源文件头，作为实现对照，不复制进本项目。
- LinuxCNC运动主循环：https://github.com/LinuxCNC/linuxcnc/blob/master/src/emc/motion/control.c 。process_inputs先读取反馈并计算跟随误差，再执行后续运动计算；误差门限含min/max并随指令速度调整。本次不照搬动态阈值来消除报警。
- LinuxCNC EtherCAT驱动：https://github.com/linuxcnc-ethercat/linuxcnc-ethercat/blob/master/src/lcec_main.c 。周期中显式管理应用时间、参考钟同步、从站钟同步、WKC、DC同步监控；最新master包含额外PLL与相位管理，后续比较必须固定具体commit，不能将不同版本当统一算法。
- ROS2 CiA402驱动配置：https://icube-robotics.github.io/ethercat_driver_ros2/user_guide/config_cia402_drive.html 。管理状态转换、复位、CSP/CSV/CST/HM模式、未受控目标位置；本文核实了官方文档，尚未成功取得其实现文件，不能据此声称代码级审查完成。
- Ruckig：https://github.com/pantor/ruckig 。状态到状态规划约束速度、加速度、jerk；公开仓库LICENSE为MIT：https://github.com/pantor/ruckig/blob/main/LICENSE 。仅作为可选规划器，不能替代CiA402、DC和驱动器整定；不把社区路径功能当成本地连续加工轨迹的完整替代品。
- IgH接口官方文档：https://docs.etherlab.org/ethercat/1.7/doxygen/group__ApplicationInterface.html 。需要对照现场安装版本，不能盲改时钟调用顺序。

## 本项目已核实的差异

当前运行二进制SHA256 5b64005f25bd3ca5578f7e1b562454d5f1b0c5526b284d44eddbb9fd10608d4e。以下定位使用此前精确诊断基础树 artifacts/is620n-native/src/EcMotion.Igh，带追踪版本通过独立叠加添加记录器；生产补丁和基础树不能混为一谈。

### 1. 跟随误差比较时序（优先）

axis_feedback.cpp先读取实际位置，然后setpoint_.update()，再以新生成、尚未发送的目标减本次接收反馈，并把此差值送入motion_safety。LinuxCNC在process_inputs阶段计算误差，新的运动指令在后续生成。

本项目存在至少一个周期的比较语义差异，应同时保存上次已发送目标、本周期将发送目标、反馈采样周期。不能直接假定驱动器物理采样延迟恰好1ms，需用DC/PDO相位验证后定义对齐方式，不可随意平移曲线掩盖真实滞后。

实际12 rpm触发前峰值帧：cycle6572，previous_target87976、sent_target88185、actual82699、demand87346、drive_error4685。新目标-反馈5486 counts约0.005232 rev；前次目标-反馈5277 counts约0.005033 rev；内部需求-反馈4647 counts约0.004432 rev。0x60F4=4685与直接差值并不完全一致，说明这些驱动内部对象不能假定在相同内部时刻更新。时序修正不能解释或消除全部误差。

### 2. 位置、速度、速度偏置比例耦合（优先核验）

本项目将6064/606C共同除以counts_per_engineering_unit，也用该值生成60B1。LinuxCNC CiA402将位置和速度尺度独立。这说明通用适配能力存在缺口；不证明现场IS620N的比例已错误。需按现场驱动器单位定义分别核对6064、606C、60B1，以及电子齿轮、机械转换和极性。现有1:1齿轮读回不能代替物理一转校验。

### 3. DC时基、相位与超时处理（审查中）

本项目绝对时钟等待，用计划wakeup值作为application_time，每周期同步参考钟/从站钟，sync0 shift=0；LinuxCNC实现另行维护app/mono相关性和同步策略。区别不是错误的充分证据。现有一次12rpm运行无执行超周期，但该运行日志DC历史峰值曾约1ms，稳态数值较低；必须区分启动瞬态与稳态，并核实PDO送达相对SYNC0的裕量。当前超周期仅计数，循环后仍需审查是否会追赶执行过密周期。

### 4. 轨迹生成器边界（确定能力缺口）

motion_setpoint.cpp为加减速度受限的离散生成器，无jerk约束；过目标时直接将位置钳到终点、速度置零。需离线验证最后一步速度/加速度连续性、反向与短行程、Stop行为。Ruckig可作为状态到状态规划候选，但先修正周期/单位和状态语义，再决定是否替换，不能把换规划器当成消除所有伺服滞后的方法。

### 5. 生命周期和观测（确定工程缺口）

临时脚本过去停止核心导致E08；目前独立总线保持未使能运行，尚未形成生产服务完整生命周期。Trace在启动时开始计数，满后停止记录，只能停核心后导出；多轮试验会丢失后续有效窗口，已有部分前馈对照仅有网络快照+核心实时峰值，不可称为全部同周期波形。需要可触发、可重装载的有界采集和非实时导出，不能为看波形反复停总线。

## 后续修复顺序和验收

1. 冻结现场和源码版本、保存机械/单位/PDO/DC/前馈配置；将只读采集和参数恢复做成可靠工具。
2. 离线测试再修正反馈与已发送指令的时序关联；保留原误差作为诊断，禁止仅更换指标宣布故障已修复。
3. 明确位置、速度反馈和偏置独立比例与来源，加入比例闭环和极性测试；未确认单位时禁止高速。
4. 改造诊断采集为不停止总线即可触发/导出，记录时基、DC、WKC、原始目标/反馈/需求/误差/转矩。
5. 审查CiA402使能、HM回CSP、Done/Busy/Aborted、Stop/Disable与总线状态；不使用反复Reset掩盖故障。
6. 验证轨迹边界，必要时引入MIT Ruckig单轴状态规划，持续路径另行验收。
7. 恢复同一基线配置后做有目的的分档复验：先验证时序和比例修复，再整定环路/前馈。每档记录实际达到速度、加减速与匀速误差、转矩、DC、位置范围和完整收尾。达到12rpm不代表高速测试合格。

目前研究和差异清单已完成；上述核心修复尚未实施，跟随误差问题未宣布解决。

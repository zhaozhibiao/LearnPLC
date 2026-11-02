# Motion correction

修复对应 docs/2026-10-29-motion-core-open-source-review.md。源码叠加基于 artifacts/cyclic-trace-20261023/source-snapshot.tar.gz 的精确诊断树，不能直接套到任意生产版本。install_overlay.py 在任何写入前验证全部原始/目标哈希，重复运行无额外修改。原核心、IgH及依赖的许可证保持原状；本轮未复制LinuxCNC GPL实现。

## 构建

在独立精确源码目录执行 `python3 motion-correction/install_overlay.py ROOT`，然后执行既有CMake Release构建与CTest。14项核心测试含单位缩放、已发送目标归属、CiA402状态/安全回归、冻结导出/重装握手测试。

## 周期诊断

启用 --axis-trace-csv PATH。对核心主进程发送 SIGUSR1：冻结采样，RT循环确认后普通线程导出 PATH.capture-N.csv，生产PDO循环继续。等待导出完成后SIGUSR2：请求重装。不得把两个信号当成可无间隔批量排队的API。重装未确认时冻结会拒绝并记录日志，不能假定已执行。文件独占创建，不覆盖旧文件；已有同名文件时更换PATH。只有明确完成导出后才能重装，导出异常时保留冻结数据供检查。进程正常退出仍可导出PATH；SIGKILL/掉电无RAM持久化保证。

cycle_target/cycle_error现在对应上一周期实际写入的量化PDO目标；sent_target_raw是本周期待发送的新目标。两者不可混用。这个归属不等于已补偿驱动内部采样和同步延迟。

## 独立单位

每轴新增 velocityFeedbackRawPerUnitPerSecond 与 velocityOffsetRawPerUnitPerSecond，显式值必须正且有限；旧文件缺省继承机械位置比例，保留兼容性。现场验证配置仍为1048576，未通过改比例缩小误差。物理独立一转标定尚未完成。

## 现场工具范围

commission_disabled.py、commission_profile.py、verify_motion.py、verify_commissioned_motion.py是针对本次设备、目录、单轴空载台架的诊断工具，不是通用无人值守调试服务。startup SDO组态明确写入IS620N内部速度前馈来源1、增益1000(100%)，主站偏置增益0，避免重复补偿。加速度0.5 rev/s²，软限±1 rev，最高指令0.5 rev/s，实速保护1 rev/s，跟随保护0.005 rev/20ms；这些是已测试台架范围，不是电机额定性能结论。

运动结束只Stop/Disable并确认位置稳定，总线保留运行。旧工具按主站feedforward_velocity分类相位，在主站偏置关闭时无效；阶段判断使用原始CSV目标增量，禁止引用旧JSON中的sampledPhasePeak/hold作为动态阶段证据。

实际数据、组态、源码归档和测试报告：artifacts/motion-correction-20261029。当前运行 st-workbench-motion-correction-commissioned，独立socket /run/st-workbench-motion-correction-commissioned/controller.sock。正式工程站服务/激活身份未替换；旧正式候选源文件已缺失，不能用诊断目录冒充正式工程包。

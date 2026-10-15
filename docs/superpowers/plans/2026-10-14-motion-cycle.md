# Motion cycle configuration implementation plan

Goal: 在同一周期页保存独立轴控周期，并完成候选门禁与只读 Linux 诊断。

1. Core 测试先行：旧身份一致、配置持久化、快照冻结、范围/轴组/路径采样匹配；新增 MotionCycleConfiguration，集成 ProjectValidator 和 BuildSnapshot。
2. Toolchain：启用配置时只新增 motion-cycle.json 和版本标记，库存/身份覆盖；禁用与旧工程包不增加字段。真实编译验证。
3. Linux：测试候选配置验证与激活拒绝；新增只读 kernel/service/thread 诊断，动作 realtime-status 不修改设备状态；协调器集成，保持原编译/运行行为。
4. UI：周期页添加启用、轴组、周期、优先级、CPU、超时设置；应用时验证并回滚；刷新只读诊断，关闭/切换工程清理陈旧状态。候选未就绪禁用激活。
5. 回归构建/核心/桌面/协调器，真实 ST 候选；只读实机诊断并原子同步已验证协调器及模块，不启停 PLC 或运动服务。审查、文档和截图。

在当前目录本会话顺序执行，用户已授权逐步实施，不新建工作树或额外审批。

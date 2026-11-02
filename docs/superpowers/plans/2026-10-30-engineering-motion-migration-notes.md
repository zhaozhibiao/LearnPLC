# 工程模型与候选包迁移实现记录

本记录仅覆盖离线工程配置与打包，不表示正式机器迁移或运动验收完成。

## 配置来源与兼容

- 平滑定位使用既有 `AxisConfiguration.MaximumJerk`（工程单位/s³）。留空不输出 `maximumJerk`，沿用旧规划；正值输出到机器轴。支持边界由运动核心决定。
- `EtherCatAxisRuntimeSettings` 增加可空字段：`DriveDemandProtection`、`DriveErrorOnly`、`DemandDeliveryEnforced`、`DemandDeliveryTolerance`、`VelocityFeedbackRawPerUnitPerSecond`、`VelocityOffsetRawPerUnitPerSecond`、`VelocityFeedforwardGain`、`FollowingErrorRawPerUnit`、`PositionDemandRawPerUnit`、`DriveFollowingErrorWindowRaw`。空值不序列化、不静默启用新策略。
- 速度比例分别表示606C反馈和60B1/60FF输出每工程单位每秒对应的原始值；6062/60F4比例表示每工程单位对应的原始值。前馈增益无量纲，交付容差使用工程单位。
- 主站辅助窗口/时间复用 `MaximumFollowingError` / `FollowingErrorTimeoutMilliseconds`；`DriveErrorOnly` 为只用60F4的辅助监控。驱动6065窗口独立使用原始计数，不假定驱动支持6066内部延时。
- 保护启用要求两个显式PDO键与正有限单位比例；缺6062/60F4、方向/类型/位宽错误时拒绝生成可运行配置。
- 已确认身份的IS620N专用映射使用60B1、6062及60F4；通用CiA402只使用ESI声明的对象，不强加专用映射。

## 驱动窗口与时序

- `DriveFollowingErrorWindowRaw` 为空不写6065。正值仅允许已确认IS620N身份生成PS启动写入（4字节小端）；与ESI6065重复时阻止。没有EEPROM写入。
- `EtherCatStationRuntimeSettings.DcSync0ShiftNanoseconds` 可选覆盖ESI相位；默认留空，无全局670us常量。
- `EtherCatRuntimeSettings.CycleTiming` 输出R2M模型、边界裕量、剩余发送预算及连续就绪周期数。要求参考从站和轴DC周期/相位一致，并与原生DeliveryMonitor预算约束一致。
- 原工程速度、加减速度、软限位不变。未使用台架±1000圈或55rev/s测试上限作为默认。

## UI、保存与身份

- 原轴属性/运行参数区域显示新字段的单位和作用；三态未定及空文本保留旧配置行为。DC与时序位于原总线组态区域。
- 使用现有ProjectStore、MCP克隆/Schema和硬件上传重建路径保留新字段。应用旧字段时保留已有运行配置的其他属性。
- 启用新字段的机器配置根声明 `requiredRuntimeCapabilities: ["workbench_motion_migration_v1"]`；ST和硬件包Manifest通过 `RequiredRuntimeCapabilities` 暴露要求。机器文件进入既有拓扑指纹、文件清单、硬件哈希与工程归档，不重写旧包。

## 验证和待完成

- Core可执行自测：新/旧序列化、jerk输出、显式PDO、单位比例、非法值拒绝、6065启动生成、DC覆盖、R2M边界/0相位及身份变化。
- App可执行自测：轴属性新控件读写和桌面保存重开；硬件上传保留新字段并保留原取消/编辑/目标变化/无效配置拒绝。
- MCP可执行自测：Schema、更新/read、保存重开保留新字段及原完整协议/模拟控制测试。
- Toolchain `--generic-ethercat-only`：真实ST编译打包、硬件包和ST包能力声明、精确机器文件及清单/指纹。
- **尚未完成：**6065实机读回与原值回滚记录；正式运行端能力检查、worker/协调器部署、在线身份与运动验收由其他迁移任务处理。本修改未连接设备、发出运动或变更Linux部署文件。

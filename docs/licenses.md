# 商用授权记录

2026-10-14 基础轴控新增部分：轴绑定、资源模板、ST 功能块、原生扫描桥和 Python EcMotion 协议工作进程为自研 MIT 源码，许可证保存在 src/LICENSE、tools/axis-bridge/LICENSE、linux/axis/LICENSE。compile-axis.mjs 属于独立 GPL-3.0-or-later 编译适配工具。接口通过进程间协议连接现有运动核心，没有将 IgH 源码或库链接入桌面或新轴控候选。原项目仿真器只在隔离验收目录使用，不进入产品候选包。现有运动核心及其依赖的分发义务仍需独立履行。

自研工程站和设备协调器采用本仓库 MIT 许可。桌面没有嵌入 GPL 编译器；ST 解析、类型检查及代码生成由独立 GPL 工具进程完成。所选组件允许商业使用，发行时必须履行各组件的通知、源码和修改说明义务。

| 组件 | 固定版本 | 授权及处理 |
|---|---|---|
| Avalonia、AvaloniaEdit及桌面依赖 | 12.1.3 / 12.0.0，见版本清单 | NuGet 元数据以 MIT 为主；ANGLE 包为 BSD 条款。保留原始许可证及原生依赖通知 |
| OpenPLC Runtime | v4.3.0，a9e38d901fb87deb5a8cf778b52639b9387834d0 | 根 MIT；隔离补丁源码随项目提供 |
| STruCpp CLI/API | 0.7.0 | GPL-3.0-or-later。可以商业使用和发行；独立工具及其修改提供对应源码、构建材料和 GPL 文本 |
| tools/compiler-adapter | 当前源码 | 独立 GPL-3.0-or-later 工具，源码及许可证在目录中 |
| 生成程序所用运行库/标准库 | 0.7.0 | 采用逐文件注明 STruCpp runtime exception 的文件，完整 COPYING.RUNTIME 随候选包保留 |
| 自研运行库兼容头 | tools/runtime-compat | MIT，独立按公开接口实现并通过 Linux 行为和真实候选编译验证 |
| Runtime Python 依赖 | 设备实际版本清单 | MIT、BSD、Apache、PSF 等；bidict 为 MPL-2.0，按文件范围保留通知、提供对应源码/修改，不要求自研工程站整体改为 MPL |

此前发现上游 iec_fault.hpp、iec_global.hpp 缺少逐文件运行库例外通知。候选包现在使用 independently authored MIT 兼容实现，不分发这两个未标注原件。包内记录替代文件摘要及许可证，UnmarkedRuntimeHeaders 已为空。其余上游文件不擅自补写授权头。

`RuntimeHeaderRedistributionCleared=true` 仅表示当前包的头文件覆盖检查通过。`CommercialRedistributionCleared=false` 保留为最终发行物门禁：当前交付的是可开发、测试的源码工作区，尚未制作完整闭源产品发行包。不能将商业使用许可与已经完成所有分发义务混为一谈；进程隔离也不是任意组合自动免责的保证。

实际桌面依赖清单：[workstation-dependency-licenses.json](workstation-dependency-licenses.json)。设备实际 Python 依赖及许可证文件摘要：[runtime-dependency-licenses.json](runtime-dependency-licenses.json)。正式发布需把第三方原始通知、GPL 工具对应源码及构建方式、MPL 文件源码/修改材料随发行物供应；新加入 EtherCAT 主站、驱动及第三方运动库需单独核查。

2026-10-14 连续路径新增部分：工程路径模型、DXF 迁移/规划、路径 UI、原生扫描桥、ST 功能块及虚拟工作进程为自研 MIT 源码，完整许可证分别保存在 src/LICENSE、tools/motion-bridge/LICENSE 和 linux/motion/LICENSE。compile-motion.mjs 仍属于独立 GPL 编译适配工具，不链接进桌面。未增加商用运动库；未将原 IgH 核心链接到新路径候选。实机接入 IgH 时仍需履行原 GPL/LGPL 边界和分发义务。

原始来源：[Runtime 固定提交许可](https://github.com/Autonomy-Logic/openplc-runtime/blob/a9e38d901fb87deb5a8cf778b52639b9387834d0/LICENSE)、[STruCpp COPYING](https://github.com/Autonomy-Logic/STruCpp/blob/v0.7.0/COPYING)、[运行库例外](https://github.com/Autonomy-Logic/STruCpp/blob/v0.7.0/COPYING.RUNTIME)。本机编译器原始完整源码包位于 `.tools/strucpp-source-v0.7.0.zip`，发行目录位于 `.tools/strucpp-0.7.0`；它们不提交 Git。

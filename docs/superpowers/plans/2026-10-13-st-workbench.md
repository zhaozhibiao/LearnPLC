# ST 工程站实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement and review the tasks. 使用测试驱动开发；独立文件可由不同执行者完成，核心模型为共享契约。

**Goal:** 在本目录实现独立 ST 工程站及可验证的编译、通信和运动边界。

**Architecture:** Avalonia 桌面 UI 调用独立核心、外部编译工具和 Runtime HTTPS 适配器。自有轴控桥接通过明确请求与快照契约工作；未完成真实 ABI 联调时拒绝活动设备下载。

**Tech Stack:** .NET 10、Avalonia 12、AvaloniaEdit、外部 STruCpp CLI；测试使用无第三方依赖的可执行断言程序。

## 任务 1：工程核心和编辑器

- [x] 核心契约：`src/StWorkbench.Core/ProjectModel.cs`，已定义 ProjectItem/PlcTask/StProject/SourceDocument/BuildSnapshot/Diagnostic，其他模块依此编译。
- [x] 先执行 `dotnet run --project tests/StWorkbench.Core.Tests`，观察未实现保存导致失败。
- [x] 实现 ProjectStore 的同目录原子替换、完整读取、源路径边界校验及失败保护；清单保存不可变版本目录下的源文件路径，避免多文件写入破坏上一版本。
- [x] 实现 ProjectValidator：schema、标识、名称、路径、任务绑定、单周期限制、IO/轴资源检查；Capture 对源内容和硬件生成稳定 SHA256 并复制可变任务。
- [x] 新增 StLanguageService 的词法索引，忽略字符串和注释中的伪声明；提供补全/签名/定义定位，并明确它不是编译器。
- [x] 建立 `src/StWorkbench.App`，实现工程树、源编辑器、打开/保存/新建、编译诊断定位、任务与硬件配置、连接设置、日志和在线变量页面。
- [x] 用 Avalonia Headless 验证界面加载及基本交互；运行完整测试和 Release 构建。

## 任务 2：外部编译和产物

- [x] 在 `tests/StWorkbench.Toolchain.Tests` 先写失败验证，再实现 `src/StWorkbench.Toolchain`。
- [x] 使用 ProcessStartInfo.ArgumentList 调用配置的独立编译器，异步收集输出、限制超时、取消时终止进程树；禁止 shell 拼接。
- [x] 隔离构建目录，消费 BuildSnapshot；要求退出码成功且生成文件存在；解析有位置的诊断，未解析错误保留原文。
- [x] 记录编译器实际版本和源哈希；本地编译产物明确不是已验证的 OpenPLC 包。只从经审核的兼容配置生成可下载包，不自行猜测 ABI。
- [x] 测试外部测试进程的成功/失败/空产物/路径含空格/取消，以及源身份和编译器身份不混淆。

## 任务 3：Runtime 通信及在线身份

- [x] 在 `tests/StWorkbench.OpenPlc.Tests` 先写失败断言，再实现 `src/StWorkbench.OpenPlc`。
- [x] HTTPS 地址校验、JWT 登录、状态、日志、编译状态读取；不关闭证书验证，不存储密码/JWT 到工程。
- [x] 下载接口要求兼容配置明确证明暂存/激活能力；否则拒绝写入真实设备。上传、编译成功、激活和运行状态分开；单次操作不自动重试。
- [x] 实现符号身份匹配、只读监视模型、实例路径、数据质量、有限趋势缓存，错误/失联使用不可用值。
- [x] 用可控 HttpMessageHandler 验证真实 HTTP 请求格式、认证失败、身份不匹配、超时和危险上传拒绝。

## 任务 4：运动与硬件边界

- [x] 在 `tests/StWorkbench.Motion.Tests` 先写失败验证，再实现 `src/StWorkbench.Motion`。
- [x] Execute 上升沿产生唯一请求；Busy/Done/Error、完成锁存、会话/请求匹配、拒绝过期反馈、队列满、控制权及路径固定版本。
- [x] 定义异步有界桥接传输接口及只读快照；无实际 Linux 适配器时明确不可用，不生成假轴动作。
- [x] 提供 ST 功能块接口示例及对接说明，标记桥接运行库未安装时不可编译下载。

## 验收和交付

- [x] 创建 `STWorkbench.slnx`、启动脚本、统一验证脚本和完整使用说明。
- [x] 收集实际采用组件的许可声明与锁定版本；GPL 编译器独立供应，并核查运行库例外文件。
- [x] 执行 `dotnet build STWorkbench.slnx -c Release` 和全部测试项目；审查安全边界及保存一致性。
- [x] 本地测试和真机联调分开报告。缺少设备参数时列明下载 ABI、Linux 编译、激活恢复及轴运动的未验收项。

已实施记录：Linux ABI、SSH 协调器事务和 SocketIO 只读调试已补充；商用材料与物理轴未验收范围见 docs/verification.md 和 docs/licenses.md。


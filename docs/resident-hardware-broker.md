# 常驻组态代理与普通用户授权

工程站通过已验证主机身份的 SSH 连接普通账号，设备协调器再访问本机 Unix socket。管理员仅在安装、升级和调整授权用户时操作；日常应用组态不再发送管理员密码。

## 已部署状态

2026-10-20，`192.168.1.31` 已安装并启动 `stworkbench-hardware.service`，其 `stworkbench-hardware.socket` 开机启用。普通账号 `a` 已加入 `stworkbench-hardware` 组。socket 为 `/run/st-workbench-hardware/control.sock`，归属 `root:stworkbench-hardware`，权限 `0660`。

新连接自动取得用户组身份；已有 SSH 会话应重连。重启工程站以加载移除密码弹窗后的版本。硬件页保留核心重启确认，实际应用仍要求 PLC 停止、所有轴撤使能且无故障。

已实测：UID1000 通过普通通道查询、校验和应用同一份 IS620N 组态，未调用 sudo；运动核心重启后指纹一致、实时反馈就绪，PLC 保持停止，轴为 Disabled、无故障，已接收运动命令序号为0。重复请求被拒绝且没有再次重启核心。记录见 `examples/generic-ethercat/resident-broker-verification.json`。本次未激活新 PLC 程序、未执行电机运动；旧活动程序与新硬件仍不匹配，需要下载匹配工程再运行。

## 权限边界

- 内核 socket 权限控制入口，代理再用 `SO_PEERCRED` 核对真实 UID 与授权组；客户端也核验对端 UID 为 root。
- 仅提供 `status`、`validate`、`apply`；后两者只接受固定部署目录内的64位构建哈希。请求不接受命令、脚本、服务名、执行路径或密码。
- 代理和组态助手均为 root 拥有、普通用户不可修改的文件。实际执行文件与组态伴随文件继续执行哈希及 schema 校验。
- 代理以 root 运行，运动核心的现有运行身份保持不变。本次没有实施运动核心降权，也没有配置免密码 sudo 或开放网络管理端口。
- 运行状态来自固定本地 HTTPS OpenPLC 接口，正常验证 CA 与主机名；代理不会抢占 Web 服务独占的底层 PLC socket。
- 代理最多同时服务4个连接，请求上限4KiB、读取总时限3秒，响应上限1MiB；应用有独立进程锁。日志记录调用 UID、动作、请求 ID 和结果。
- 请求 ID 在操作前持久写入 root 独占的 `/var/lib/st-workbench-ethercat/broker-requests.json` 并落盘；失败、断连、代理重启后也不自动重放。最多保留4096个应用请求，达到上限时拒绝继续应用，由管理员维护。维护删除记录会清除历史防重放信息，不能在结果未知时清理后重试。

协调器负责停止、重建普通用户轴桥；代理负责受限特权组态操作。常规应从工程站或既有 MCP 的部署流程调用，以完成两边的身份与就绪检查。代理不可用时先报错，尚未停止轴桥；应用中断则查询实际状态，不自动重试。

## 安装与维护

首次设备准备先按通用核心说明安装固定二进制及助手，再将 `linux/ethercat-i210` 下的 `hardware-broker.py`、两个 `stworkbench-hardware` 单元文件和 `install-resident-broker.sh` 放到设备固定 staging 目录 `/home/a/st-workbench-i210/resident-broker/`，由管理员执行安装器。安装器核验固定 SHA256、root 路径与权限，只授权账号 `a` 并启用本地通道，不改变 PLC 或运动核心状态。

安装器若发现代理已经运行，会提示维护时重启代理以加载新代码；不会中断正在进行的应用。升级时必须先停止 PLC 并确认没有正在执行的组态事务，再由管理员重启代理。撤销授权应由管理员移除该用户的 `stworkbench-hardware` 组成员资格并关闭其已有会话。

Linux 启动后 socket 会恢复；运动核心是否已经就绪仍以实际查询为准，常驻管理代理不会擅自恢复 PLC 运行或轴使能。

新代码 MIT；既有 OpenPLC、编译器与 EtherCAT 依赖的许可保持不变。

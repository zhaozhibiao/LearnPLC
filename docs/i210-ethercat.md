# I210 EtherCAT 专用网卡准备记录

2026-10-19，对 a@192.168.1.31 实际检查：

- 管理网卡 enp2s0，MAC 8c:ec:4b:42:09:1a，IP 192.168.1.31/24，驱动 r8169。SSH 回程经过此接口。
- 新增 Intel I210-T1：enp3s0，MAC b4:96:91:77:df:0f，PCI 0000:03:00.0，设备 8086:1533，当前原生 Linux 驱动 igb。
- 内核 6.12.111+deb13-rt-amd64，PREEMPT_RT，已安装匹配内核头文件。
- NetworkManager 为 I210 建有自动连接配置 Wired connection 2，UUID 252c14c9-0687-33a1-8a78-de58cbf70d1a；预检只允许其没有 IPv4、全局 IPv6 或管理路由，允许自动生成的 IPv6 链路本地地址。
- 未发现已安装的 IgH 主站命令或当前内核的 ec_master/ec_igb 模块；EtherCAT/运动核心服务未运行。

已在 `/home/a/st-workbench-i210/ethercat-1.6.13` 独立编译 IgH 1.6.13，源提交固定为 `61cc654f5b721ddd54df0f58bdd34106d91c5359`；源码来自设备既有、提交匹配的官方仓库副本，没有修改原副本。使用当前实时内核头文件，构建 ec_master.ko 和 ec_igb.ko，vermagic 校验通过。构建日志 `/home/a/st-workbench-i210/build.log`，构建文件 SHA256 清单 `built-files.sha256`。IgH 原组件仍保留其 GPL/LGPL 许可，自研安装脚本为 MIT。

配置脚本源文件：[configure-i210.sh](../linux/ethercat-i210/configure-i210.sh)。设备上已上传至 `/home/a/st-workbench-i210/configure-i210.sh`，`bash -n` 与 `--check` 已通过。

**2026-10-19 后续核验：用户执行安装后，系统安装和专用网卡切换已成功。** ethercat.service 已 enabled、active (exited)，I210 PCI 驱动为 ec_igb，ec_master/ec_igb 已加载，主站绑定 b4:96:91:77:df:0f；SSH 管理接口仍是 enp2s0。最初安装瞬间的主站记录为 Idle、0 从站、Link DOWN，该快照不代表稍后的实时链路状态。尚未启动运动核心或 PLC。

用户报告的 `SSL error ... bss_file.c ... No such file or directory` 已定位到内核模块安装签名步骤：当前头文件 auto.conf 设置 CONFIG_MODULE_SIG_ALL=y，签名 key 为 output/signing_key.pem，实际 key 和 certs/signing_key.x509 均不存在。Makefile.modinst 第108–109行对外部模块签名失败使用 `|| true`，所以未中断安装；实际加载状态已确认成功。没有禁用 SSL、内核签名验证或 Secure Boot。

另外发现首版脚本把 umask 077 延续至程序/模块安装，导致 /opt/etherlab 目录、模块目录及 depmod 索引权限过严。这是安装脚本问题，已修订后续安装使用 umask 022，并新增定点权限修复：

```sh
sudo bash /home/a/st-workbench-i210/configure-i210.sh --repair-permissions
```

修复仅恢复安装目录/模块文件的标准可读权限、以 umask 022 重建模块索引并刷新只读主站状态记录；备份目录保持私有，不改变 /dev/EtherCAT0 权限，不加载/卸载驱动或启停服务。操作者已执行修复，2026-10-19 后续 SSH 核验完成：程序/模块目录为0755，模块索引及配置为0644，普通账户可运行 `ethercat version`，返回 IgH 1.6.13。

修复后的主站记录 `/run/st-workbench-i210-status.txt` 显示：Master0 绑定指定 I210 MAC、Link UP、Slaves 1、Tx errors 0、Lost frames 0；Phase Idle、Active no，表明总线已发现从站，尚未由运动应用接管进入运行。EtherCAT 服务 active/enabled，ec_igb/ec_master 已加载。管理地址 192.168.1.31/24 与 SSH 回程仍在 enp2s0；I210 原网络配置 autoconnect=no，并由 NetworkManager 专用规则排除。运动核心及旧 PLC 服务仍 inactive。

账户 a 的普通 SSH 密钥不提供系统管理员权限；sudo 的免密码范围仅包含旧 PLC 服务启停，网络、驱动和权限修复仍要求在终端输入密码。

在 Linux 终端执行：

```sh
sudo bash /home/a/st-workbench-i210/configure-i210.sh --apply
```

脚本再次核对 MAC、PCI、I210 型号、管理路由、原生驱动唯一性、内核 ABI 和构建摘要。随后备份配置到 `/var/lib/st-workbench-i210`，安装 IgH，禁用指定 I210 网络配置自动连接，以专属 NetworkManager 规则排除该 MAC，按明确 MAC 配置 MASTER0_DEVICE，使用原生 ec_igb 驱动，启用 EtherCAT 内核服务。不会启动运动控制核心或 PLC 程序，也不会修改 enp2s0 的地址与连接。失败会尝试恢复网卡归属及原配置。

成功时 `/run/st-workbench-i210-status.txt` 提供可读的主站检查结果。再次 SSH 检查 systemctl 状态、PCI 驱动和管理路由，以实际输出判断完成状态。

回滚：

```sh
sudo bash /home/a/st-workbench-i210/configure-i210.sh --rollback
```

回滚停用 EtherCAT 内核服务，恢复普通 igb、原网络配置及自动连接；已安装的主站文件保留在磁盘。当前为固定内核构建，升级内核后须为新内核重新编译并验证模块。

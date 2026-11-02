# 通用 EtherCAT 核心与 IS620N 保护适配

## 2026-10-23 最新候选状态

已修复从站初扫竞态（最多等待 5 秒）和 CSP 速度前馈映射。IS620N 专用配置现在使用 0x60B1，并在工程站编译时生成 RxPDO 0x1600 / TxPDO 0x1A00；通用 CiA402 仍使用 0x60FF。跟随误差门限恢复为 0.005 rev / 20 ms，尚未通过新一轮运动数据调整。

候选核心在目标机独立目录 `/home/a/st-workbench-i210/is620n-native-candidate-20261023` 构建，12/12 CTest 通过。真实启动检查报告总线就绪、WKC 完整、控制周期正常、轴为 Disabled 且未回零，无故障；此检查没有发出 Enable、Home 或 Move，随后已停止候选进程并释放 EtherCAT 主站。原 `/run/st-workbench-is620n` 生产服务仍为 failed，PLC 保持 safe-mode；候选尚未安装到正式服务，也未进行新的实机运动验收。此前运行数据继续作为历史记录，不代表这版候选的运动性能。

`../is620n-native.patch` 只用于用户自有 EcMotion 原项目的隔离副本，原项目 `D:/ethercat主站` 只读。`../is620n-native-source.json` 记录全部 16 个文件：10 个已有文件的原始及修改后 SHA256，以及 6 个新增文件的修改后 SHA256（新增文件 `originalSha256:null`）。补丁包含此前的 Home35 显式许可、超速监控门控、位置稳定完成判定及其回归测试，也包含通用运行时加载器、协议指纹和离线验证依赖。

隔离副本 `artifacts/is620n-native` 需要原项目的 `src/EcMotion.Igh`、`src/EcMotion.SoftPlc` 及 `config/io-map.json`、`config/reference-machine.json`。准备全新副本后，先按照清单核对原始文件 SHA256，再在本仓库根目录检查并应用补丁：

```powershell
git -c core.autocrlf=false apply --check --directory=artifacts/is620n-native linux/ethercat-i210/is620n-native.patch
git -c core.autocrlf=false apply --directory=artifacts/is620n-native linux/ethercat-i210/is620n-native.patch
git -c core.autocrlf=false apply --check --directory=artifacts/is620n-native linux/ethercat-i210/is620n-native-followup.patch
git -c core.autocrlf=false apply --directory=artifacts/is620n-native linux/ethercat-i210/is620n-native-followup.patch
```

第二份补丁修复 2026-10-23 复查发现的 EtherCAT 扫描启动竞态，为 IS620N CSP 加入 0x60B1 速度前馈，并保留通用 CiA402 的 0x60FF 映射。先核对 `is620n-native-followup-source.json` 中的基线哈希，再按顺序应用两份补丁；跟随误差阈值恢复为 0.005 rev / 20 ms。离线编译测试不代表驱动器已接受 0x60B1 PDO 映射，真实启动仍需检查 OP/WKC。

显式关闭本次命令的换行转换，才能与清单中的 LF 文件哈希逐字节一致，不会修改全局 Git 设置。全部新增 native 文件已包含在补丁中，无需另外复制加载器。补丁中的 `generated/is620n_ethercat_config.hpp` 与本目录同名耐久文件一致；参考拓扑 `generated/ethercat_config.hpp` 保持原项目版本，不要用本目录的简化类型模板覆盖它。

独立测试和配置仍需按以下步骤复制，目标均在隔离副本中：

```powershell
Copy-Item linux/ethercat-i210/native-profile/is620n_profile_selftest.cpp artifacts/is620n-native/
Copy-Item linux/ethercat-i210/native-profile/test_is620n_profile.py artifacts/is620n-native/
Copy-Item linux/ethercat-i210/native-profile/validate_is620n_readiness.py artifacts/is620n-native/
Copy-Item linux/ethercat-i210/native-profile/config/is620n-*.json artifacts/is620n-native/config/
```

如需根据记录证据重新生成 IS620N 兼容回退头文件：

```powershell
python artifacts/is620n-native/validate_is620n_readiness.py examples/is620n-test/hardware-probe.txt examples/is620n-test/hardware-profile-probe.txt --evidence artifacts/is620n-native/config/is620n-manufacturer-evidence.json --write-header artifacts/is620n-native/src/EcMotion.Igh/generated/is620n_ethercat_config.hpp
```

Linux 构建使用与内核模块匹配的 `/opt/etherlab`；测试需要 Python3：

```sh
cmake -S src/EcMotion.Igh -B build -DECMOTION_IGH_ROOT=/opt/etherlab -DECMOTION_IS620N_PROFILE=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build -j2
ctest --test-dir build --output-on-failure
```

同一二进制通过 `--ethercat-config` 读取上位站导出的拓扑、PDO、DC 和启动 SDO，不再为每个驱动重编译；不传该选项时保留生成头文件回退。通用配置规范、容量上限和明确拒绝 PDO padding 的说明见 [GENERIC_RUNTIME_CONFIG.md](GENERIC_RUNTIME_CONFIG.md)。替换正在工作的核心前，先运行不会访问主站的整包离线检查：

```sh
build/ecmotion-rt --validate-ethercat-config ethercat-runtime.json --machine-config machine-config.json --io-map io-map.json
```

整包检查会核对 machine/IO 文件 SHA256、配置解析、轴所属从站及 PDO 宽度/方向、运动参数和周期一致性、普通 IO 与轴独占冲突，并绑定 IO 布局。支持 `axes:[]` 的纯 IO 机器；没有轴且没有逻辑 IO 的机器被拒绝。带哈希绑定的配置必须显式提供两个伴随文件。无哈希且不传伴随文件时，可仅检查拓扑文档。

2026-10-20 已验证 **11 项 CTest**：原有 9 项保护/运动/PLC 回归，加上 `ethercat-runtime-config` 和 `ethercat-runtime-cli`。后者覆盖被篡改的伴随文件、错误 schema、缺失 IO 映射、反向软限位、主站/轴周期不一致的拒绝，以及有效 IS620N、纯 IO 配置的接受；测试仅走离线分支。本目录另有 6 项 Python 配置测试。

耐久补丁验证记录：在全新只读原文件副本 `artifacts/generic-native-patch-proof-eb208f68` 上，`git apply --check` 和实际应用均成功，16/16 输出 SHA256 与当前隔离源码完全一致，10/10 原始文件 SHA256 未改变。此步骤未访问远端设备或操作服务。

生成就绪不代替真实 OP/WKC、回零和运动验收。此前 IS620N 真实验收结果见 `examples/is620n-test/README.md`；重新构建后仍须验证并更新安装的二进制与配置哈希。

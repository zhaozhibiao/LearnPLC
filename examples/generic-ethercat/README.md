# IS620N 通用组态示例

打开 `IS620N-Generic.stproj`。程序只读取轴状态、位置、速度，不调用使能、回零或运动功能块。

`IS620N-commissioned.xml` 是本项目根据已记录的 IS620N EtherCAT 身份、PDO、DC 参数重建的测试描述，**不是厂商原始 ESI**。编码器、限位和保护参数仅适用于此前确认的空载固定测试电机，不能直接用于其他机械。

`build-evidence.json` 记录本地真实 ST 编译结果和组态指纹；设备应用及实时状态另存于 `live-verification.json`（存在时）。本地编译通过不等于实机已加载。

2026-10-20 实机验证：通用原生核心已经加载该组态，`runtimeReady/ioReady=true`，Axis1 为 `Disabled`、速度 0、无故障，已应用运动命令序号为 0，PLC 保持停止。Linux 编译候选通过且 `axis_ready=true`，见 `candidate-verification.json`。未激活该只读候选，也未执行本次运动；原活动 PLC 程序与新组态不匹配，禁止直接启动，需下载匹配工程。

固定原生二进制 SHA256：`b760092eda0bad2f3acb1cb25831cb382a5215bf9d7ec773462583477e8b448a`。部署助手采用本地 HTTPS 查询停止状态；不能另连 OpenPLC 由 Web 服务独占的底层状态 socket。

使用步骤及限制见 [通用组态说明](../../docs/generic-ethercat.md)。设备现已安装受限常驻代理，账号 `a` 经用户组授权后可应用组态，无需逐次 sudo；不是任意管理员命令授权。`resident-broker-verification.json` 记录普通账号完整应用及重复请求拒绝的真实结果。组态应用与 PLC 程序激活分开，应用期间必须保持停止和撤使能。

# Web PLC Simulator

一款基于浏览器的轻量级、全互动式 Web PLC 逻辑模拟与教学平台。本项目在前端实现了工业标准的**拓扑排序 (Topological Sort)** 逻辑执行引擎，支持从基础触点到高级定时器、计数器和比较指令的实时仿真，并搭配了生动的电流导通动画与时序图，非常适合作为 PLC 编程原理的互动教学工具。

![React](https://img.shields.io/badge/React-18.x-blue.svg)
![Vite](https://img.shields.io/badge/Vite-5.x-purple.svg)
![React Flow](https://img.shields.io/badge/React%20Flow-11.x-ff1493.svg)
![Zustand](https://img.shields.io/badge/Zustand-4.x-orange.svg)

## ✨ 核心特性

* **⚡ 真正的拓扑执行引擎**：底层不使用简单的宽度优先搜索(BFS)，而是采用符合真实 PLC 周期扫描规则的图论拓扑排序，完美支持环路解除、断路判定以及跨周期状态保持。
* **🎮 热修改参数交互**：与传统的“停止-修改-下载”流程不同，本项目支持在系统 `RUNNING` 状态下，直接通过界面输入框动态修改定时器时长 (`PT`)、计数器目标 (`PV`) 和比较阈值，逻辑引擎在下一毫秒即时响应。
* **🧩 丰富的进阶指令块**：
  * **基础线圈**：常开 (`NO`)、常闭 (`NC`)、上升沿 (`P`)、下降沿 (`N`)。
  * **起保停指令**：置位 (`S`)、复位 (`R`)。
  * **定时器家族**：接通延时 (`TON`)、断开延时 (`TOF`)、脉冲定时器 (`TP`)。
  * **计数器家族**：加计数 (`CTU`)、减计数 (`CTD`)、加减计数 (`CTUD`)。
  * **数据比较**：可变运算符 (`==`, `!=`, `<`, `>`, `<=`, `>=`) 的数值比较指令 (`CMP`)。
* **📚 内置教学关卡题库**：系统内置了 8 个由浅入深的经典 PLC 控制场景题库（例如电机启停、报警控制等）。
* **⏱️ 毫秒级自动化时序验证**：内置的高速测试引擎可将时间流逝加速百倍，瞬间完成数秒乃至数分钟时序动作序列的正确性验证，并生成直观的测试报告。

## 🚀 快速开始

本项目使用 `Vite` 作为构建工具，请确保您的计算机上已安装 [Node.js](https://nodejs.org/)。

### 1. 克隆项目
```bash
git clone https://github.com/您的用户名/您的仓库名.git
cd 您的仓库名/frontend
```

### 2. 安装依赖包
```bash
npm install
```

### 3. 启动开发服务器
```bash
npm run dev
```
运行后，在浏览器中访问控制台输出的地址（通常为 `http://localhost:5173`）即可体验。

## 🏗️ 架构与技术栈

* **前端视图**：React + Vite
* **可视化连线引擎**：[@xyflow/react](https://reactflow.dev/) (原 React Flow) 实现节点拖拽与连线。
* **全局状态管理**：[Zustand](https://github.com/pmndrs/zustand) 处理 UI 状态与逻辑引擎的解耦同步。
* **逻辑核心 (PLC Engine)**：完全由原生 JavaScript 实现的纯函数式图计算引擎 (`plcEngine.js`)，剥离了与界面的强耦合，能够轻易移植到 Node.js 服务端或其他环境。

## 📝 目录结构简析

```text
frontend/
├── src/
│   ├── components/         # React 组件
│   │   ├── nodes/          # 自定义的 React Flow 指令块节点 (Timer, Counter等)
│   │   ├── LadderEditor    # 核心的可视化连线编辑区
│   │   ├── ControlPanel    # 左侧运行控制面板
│   │   ├── TaskPanel       # 右侧教学关卡面板
│   │   └── TimingDiagram   # 底部时序图图表
│   ├── engine/             # 逻辑运算核心
│   │   ├── plcEngine.js    # 拓扑扫描与节点状态计算引擎
│   │   └── test.js         # 高速序列验证引擎
│   ├── store/              # Zustand 状态库
│   │   ├── usePLCStore.js  # 全局状态控制器
│   │   └── taskLibrary.js  # 预设的教学关卡数据配置
│   ├── App.jsx             # 页面主入口
│   └── main.jsx            # React 挂载点
└── package.json
```

## 🤝 贡献指南

欢迎提交 Issue 报告 Bug 或者提出新的指令需求！也欢迎 Fork 本仓库并提交 Pull Request。如果您觉得这个项目对您的教学或学习有帮助，请给一个 ⭐️ Star！

## 📄 许可证

本项目采用 [MIT License](LICENSE) 开源协议。

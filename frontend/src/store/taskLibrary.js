export const TASK_LIBRARY = [
  {
    id: 'unit-1',
    title: '单元 1：基础直接控制 (NO/NC)',
    description: '实现基本的逻辑：X0 闭合时 Y0 通电，X1 闭合时断电。使用常开和常闭接点。',
    initialVariables: { X0: false, X1: false, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 200, y: 100 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'node-x1', type: 'contact', position: { x: 350, y: 100 }, data: { type: 'NC', variable: 'X1' } },
          { id: 'node-y0', type: 'coil', position: { x: 500, y: 100 }, data: { type: 'COIL', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'node-x1' },
      { id: 'e3', source: 'node-x1', target: 'node-y0' },
    ],
    testCases: [
      { input: { X0: false, X1: false }, expected: { Y0: false }, name: '全断开' },
      { input: { X0: true, X1: false }, expected: { Y0: true }, name: 'X0闭合，Y0通电' },
      { input: { X0: true, X1: true }, expected: { Y0: false }, name: 'X1闭合，切断Y0' },
    ],
    testDurationMs: 200,
  },
  {
    id: 'unit-2',
    title: '单元 2：边沿触发指令 (P/N)',
    description: '使用上升沿指令 (P)。X0 从 OFF 变为 ON 的瞬间，Y0 导通一个周期。由于前端显示过快，这里主要验证引擎在后台是否正确捕获。',
    initialVariables: { X0: false, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0-p', type: 'contact', position: { x: 200, y: 100 }, data: { type: 'P', variable: 'X0' } },
          { id: 'node-y0', type: 'coil', position: { x: 350, y: 100 }, data: { type: 'COIL', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0-p' },
      { id: 'e2', source: 'node-x0-p', target: 'node-y0' },
    ],
    testCases: [
      { input: { X0: false }, expected: { Y0: false }, name: '初始OFF' },
      { sequence: [ { X0: false }, { X0: true } ], expectEdge: 'Y0', name: '检测到上升沿脉冲' }
    ],
    testDurationMs: 200,
  },
  {
    id: 'unit-3',
    title: '单元 3：定时器应用 (TON)',
    description: '接通延时 (TON)：当 X0 闭合持续 3 秒后，Y0 才会输出。您可以在节点上直接修改 PT 的值实时观察效果。',
    initialVariables: { X0: false, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 200, y: 100 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'timer-1', type: 'timer', position: { x: 350, y: 70 }, data: { type: 'TON', PT: 3000 } },
          { id: 'node-y0', type: 'coil', position: { x: 550, y: 100 }, data: { type: 'COIL', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'timer-1', targetHandle: 'IN' },
      { id: 'e3', source: 'timer-1', sourceHandle: 'Q', target: 'node-y0' },
    ],
    testCases: [
      { input: { X0: true }, holdMs: 1000, expected: { Y0: false }, name: '1秒后未触发' },
      { input: { X0: true }, holdMs: 3100, expected: { Y0: true }, name: '3秒后触发' },
      { input: { X0: false }, holdMs: 100, expected: { Y0: false }, name: '断开后复位' },
    ],
    testDurationMs: 3500,
  },
  {
    id: 'unit-4',
    title: '单元 4：计数器应用 (CTU)',
    description: '加计数器 (CTU)：X0 每闭合一次计数值加 1。当计数值达到 3 时，Y0 输出。M0 用于复位计数器。您可以在节点上实时修改 PV 预设值。',
    initialVariables: { X0: false, M0: false, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 200, y: 100 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'counter-1', type: 'counter', position: { x: 350, y: 70 }, data: { type: 'CTU', PV: 3, resetVar: 'M0' } },
          { id: 'node-y0', type: 'coil', position: { x: 550, y: 100 }, data: { type: 'COIL', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'counter-1', targetHandle: 'CU' },
      { id: 'e3', source: 'counter-1', sourceHandle: 'Q', target: 'node-y0' },
    ],
    testCases: [
      { sequence: [ {X0: true}, {X0: false}, {X0: true}, {X0: false} ], expected: { Y0: false }, name: '点击2次 (未达到3次)' },
      { sequence: [ {X0: true}, {X0: false} ], expected: { Y0: true }, name: '再点击1次 (触发输出)' },
      { sequence: [ {M0: true}, {M0: false} ], expected: { Y0: false }, name: '触发 M0 复位' },
    ],
    testDurationMs: 1000,
  },
  {
    id: 'unit-5',
    title: '单元 5：置位与复位 (S/R)',
    description: '使用置位 (S) 和复位 (R) 线圈实现电机的启停控制。X0 启动电机 (Y0 置位)，X1 停止电机 (Y0 复位)。',
    initialVariables: { X0: false, X1: false, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 200, y: 50 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'node-y0-s', type: 'coil', position: { x: 400, y: 50 }, data: { type: 'S', variable: 'Y0' } },
          { id: 'node-x1', type: 'contact', position: { x: 200, y: 150 }, data: { type: 'NO', variable: 'X1' } },
          { id: 'node-y0-r', type: 'coil', position: { x: 400, y: 150 }, data: { type: 'R', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'node-y0-s' },
      { id: 'e3', source: 'power', target: 'node-x1' },
      { id: 'e4', source: 'node-x1', target: 'node-y0-r' },
    ],
    testCases: [
      { sequence: [ { X0: true }, { X0: false } ], expected: { Y0: true }, name: 'X0触发置位，Y0保持导通' },
      { sequence: [ { X1: true }, { X1: false } ], expected: { Y0: false }, name: 'X1触发复位，Y0断开' },
    ],
    testDurationMs: 500,
  },
  {
    id: 'unit-6',
    title: '单元 6：其它定时器 (TOF/TP)',
    description: 'TOF 断开延时：X0 闭合时 Y0 导通，X0 断开后，Y0 还会保持导通 2 秒。TP 脉冲：X1 触发后，Y1 输出 3 秒固定宽度的脉冲，中途无论输入怎么变都不影响。您可以随时热修改设定时间。',
    initialVariables: { X0: true, X1: false, Y0: true, Y1: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 200, y: 50 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'timer-tof', type: 'timer', position: { x: 350, y: 20 }, data: { type: 'TOF', PT: 2000 } },
          { id: 'node-y0', type: 'coil', position: { x: 550, y: 50 }, data: { type: 'COIL', variable: 'Y0' } },
          
          { id: 'node-x1', type: 'contact', position: { x: 200, y: 250 }, data: { type: 'NO', variable: 'X1' } },
          { id: 'timer-tp', type: 'timer', position: { x: 350, y: 220 }, data: { type: 'TP', PT: 3000 } },
          { id: 'node-y1', type: 'coil', position: { x: 550, y: 250 }, data: { type: 'COIL', variable: 'Y1' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'timer-tof', targetHandle: 'IN' },
      { id: 'e3', source: 'timer-tof', sourceHandle: 'Q', target: 'node-y0' },
      
      { id: 'e4', source: 'power', target: 'node-x1' },
      { id: 'e5', source: 'node-x1', target: 'timer-tp', targetHandle: 'IN' },
      { id: 'e6', source: 'timer-tp', sourceHandle: 'Q', target: 'node-y1' },
    ],
    testCases: [
      { input: { X0: false }, holdMs: 1000, expected: { Y0: true }, name: 'TOF: 断开1秒后Y0仍导通' },
      { input: { X0: false }, holdMs: 1500, expected: { Y0: false }, name: 'TOF: 断开2秒后Y0关闭' },
      { input: { X1: true }, holdMs: 50, expected: { Y1: true }, name: 'TP: 触发瞬间导通' },
      { input: { X1: false }, holdMs: 1500, expected: { Y1: true }, name: 'TP: 触发后1.5秒仍导通' },
      { input: { X1: false }, holdMs: 1800, expected: { Y1: false }, name: 'TP: 3秒后脉冲结束' },
    ],
    testDurationMs: 4000,
  },
  {
    id: 'unit-7',
    title: '单元 7：减计数器 (CTD)',
    description: 'CTD 从 PV 值开始向下减数，当 CV 达到 0 时 Q 导通。X0 为减计数脉冲 (CD)，M0 为加载预设值 (LD)。刚开始 M0 导通以完成初始加载。',
    initialVariables: { X0: false, M0: true, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 200, y: 100 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'counter-ctd', type: 'counter', position: { x: 350, y: 70 }, data: { type: 'CTD', PV: 2, loadVar: 'M0' } },
          { id: 'node-y0', type: 'coil', position: { x: 550, y: 100 }, data: { type: 'COIL', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'counter-ctd', targetHandle: 'CD' },
      { id: 'e3', source: 'counter-ctd', sourceHandle: 'Q', target: 'node-y0' },
    ],
    testCases: [
      { sequence: [ { M0: false } ], expected: { Y0: false }, name: '取消Load' },
      { sequence: [ { X0: true }, { X0: false } ], expected: { Y0: false }, name: '减1 (CV=1)' },
      { sequence: [ { X0: true }, { X0: false } ], expected: { Y0: true }, name: '再减1 (CV=0, 导通)' },
    ],
    testDurationMs: 1000,
  },
  {
    id: 'unit-8',
    title: '单元 8：数据比较指令 (CMP)',
    description: '使用比较指令判断内部计数 CV 是否达到阈值。您可以动态修改比较节点的运算符(>=, ==, <等) 和目标值！',
    initialVariables: { X0: false, Y0: false },
    initialRungs: [
      {
        id: 'rung-1',
        nodes: [
          { id: 'power', type: 'power', position: { x: 50, y: 100 }, data: {} },
          { id: 'node-x0', type: 'contact', position: { x: 150, y: 50 }, data: { type: 'NO', variable: 'X0' } },
          { id: 'counter-1', type: 'counter', position: { x: 300, y: 20 }, data: { type: 'CTU', PV: 999, cvVar: 'CV_1' } },
          
          { id: 'cmp-1', type: 'cmp', position: { x: 300, y: 250 }, data: { operator: '>=', var1: 'CV_1', isConstant: true, val2: 3 } },
          { id: 'node-y0', type: 'coil', position: { x: 500, y: 250 }, data: { type: 'COIL', variable: 'Y0' } },
        ]
      }
    ],
    initialEdges: [
      { id: 'e1', source: 'power', target: 'node-x0' },
      { id: 'e2', source: 'node-x0', target: 'counter-1', targetHandle: 'CU' },
      { id: 'e3', source: 'power', target: 'cmp-1', targetHandle: 'EN' },
      { id: 'e4', source: 'cmp-1', sourceHandle: 'OUT', target: 'node-y0' },
    ],
    testCases: [
      { sequence: [ {X0: true}, {X0: false}, {X0: true}, {X0: false} ], expected: { Y0: false }, name: '计数值为2 (CV=2 < 3)' },
      { sequence: [ {X0: true}, {X0: false} ], expected: { Y0: true }, name: '计数值为3 (CV=3 >= 3)' },
    ],
    testDurationMs: 1000,
  }
];

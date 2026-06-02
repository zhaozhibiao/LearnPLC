import { create } from 'zustand';
import { evaluatePLC } from '../engine/plcEngine';
import { TASK_LIBRARY } from './taskLibrary';

const initialTask = TASK_LIBRARY[0];

export const usePLCStore = create((set, get) => ({
  currentTaskId: initialTask.id,
  variables: initialTask.initialVariables,
  nodes: initialTask.initialRungs[0].nodes, // We assume 1 rung for simplicity in initial loading
  edges: initialTask.initialEdges,
  rungs: initialTask.initialRungs,
  instances: {}, 
  isRunning: false,
  scanIntervalId: null,
  lastTick: 0,
  history: [], // [{ timestamp: number, variables: { X0: true, ... } }]
  maxHistoryItems: 100, // keep last 100 data points

  // 切换变量状态（例如点击按钮模拟物理输入）
  toggleVariable: (varName) => set((state) => {
    const newVars = {
      ...state.variables,
      [varName]: !state.variables[varName]
    };
    return {
      variables: newVars,
      // If stopped, we might still want to record the change. 
      // Let's just update variables here. History mainly updates in scanCycle or when running.
    };
  }),

  setVariable: (varName, value) => set((state) => ({
    variables: {
      ...state.variables,
      [varName]: value
    }
  })),

  setRungs: (newRungs) => set({ rungs: newRungs }),
  setNodes: (nodesOrUpdater) => set((state) => ({ 
    nodes: typeof nodesOrUpdater === 'function' ? nodesOrUpdater(state.nodes) : nodesOrUpdater 
  })),
  setEdges: (edgesOrUpdater) => set((state) => ({ 
    edges: typeof edgesOrUpdater === 'function' ? edgesOrUpdater(state.edges) : edgesOrUpdater 
  })),

  updateNodeData: (id, newData) => set((state) => ({
    nodes: state.nodes.map(n => n.id === id ? { ...n, data: { ...n.data, ...newData } } : n)
  })),

  loadTask: (taskId) => {
    const task = TASK_LIBRARY.find(t => t.id === taskId);
    if (task) {
      // 停止运行
      const { scanIntervalId } = get();
      if (scanIntervalId) clearInterval(scanIntervalId);

      set({
        currentTaskId: taskId,
        variables: { ...task.initialVariables },
        nodes: task.initialRungs.flatMap(r => r.nodes),
        edges: task.initialEdges,
        rungs: task.initialRungs,
        instances: {},
        history: [],
        isRunning: false,
        scanIntervalId: null,
        lastTick: 0
      });
    }
  },

  // 启动/停止 PLC
  toggleRunState: () => {
    const { isRunning, scanIntervalId } = get();
    if (isRunning) {
      clearInterval(scanIntervalId);
      set({ isRunning: false, scanIntervalId: null, lastTick: 0 });
    } else {
      set({ isRunning: true, lastTick: Date.now() });
      const id = setInterval(() => {
        get().scanCycle();
      }, 50); // increased frequency to 50ms for better timer resolution
      set({ scanIntervalId: id });
    }
  },

  // 执行一次扫描周期
  scanCycle: (mockDeltaTime = null) => {
    const { variables, instances, rungs, history, maxHistoryItems, lastTick, nodes, edges } = get();
    
    const now = Date.now();
    const deltaTime = mockDeltaTime !== null ? mockDeltaTime : (lastTick ? now - lastTick : 0);
    
    const { newVariables, newInstances, nodePowers } = evaluatePLC(variables, instances, nodes, edges, deltaTime);
    
    // 如果变量有变化，才触发更新，避免过度渲染
    let hasChanges = false;
    for (const key in newVariables) {
      if (newVariables[key] !== variables[key]) {
        hasChanges = true;
        break;
      }
    }
    
    // 记录历史（每次扫描都记录，用于生成连续时序图）
    const newHistoryEntry = { timestamp: now, variables: { ...newVariables } };
    const newHistory = [...history, newHistoryEntry];
    if (newHistory.length > maxHistoryItems) {
      newHistory.shift();
    }

    // 根据真实引擎返回的 nodePowers 更新界面状态
    const newNodes = nodes.map(n => ({
      ...n,
      data: { ...n.data, powered: !!nodePowers[n.id] }
    }));

    const newEdges = edges.map(e => {
      const isPowered = !!nodePowers[e.source];
      return {
        ...e,
        animated: isPowered,
        style: isPowered 
          ? { stroke: '#fbbf24', strokeWidth: 3, filter: 'drop-shadow(0 0 5px #fbbf24)' } 
          : { stroke: '#ffffff14', strokeWidth: 2 }
      };
    });

    set({ 
      variables: { ...variables, ...newVariables }, 
      instances: newInstances, 
      history: newHistory, 
      lastTick: mockDeltaTime === null ? now : lastTick,
      nodes: newNodes,
      edges: newEdges
    });
  },

  clearHistory: () => set({ history: [] })
}));

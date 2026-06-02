import { useCallback, useEffect, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  applyNodeChanges,
  applyEdgeChanges,
  addEdge,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';

import { usePLCStore } from '../store/usePLCStore';
import ContactNode from './nodes/ContactNode';
import CoilNode from './nodes/CoilNode';
import PowerNode from './nodes/PowerNode';
import TimerNode from './nodes/TimerNode';
import CounterNode from './nodes/CounterNode';
import CompareNode from './nodes/CompareNode';

const nodeTypes = {
  contact: ContactNode,
  coil: CoilNode,
  power: PowerNode,
  timer_ton: TimerNode,
  timer: TimerNode,
  counter_ctu: CounterNode,
  counter: CounterNode,
  cmp_ge: CompareNode,
  cmp: CompareNode,
};

// Initial nodes will be loaded from the store's rungs

export default function LadderEditor() {
  const { variables, isRunning, nodes, edges, setNodes, setEdges } = usePLCStore();

  const onNodesChange = useCallback(
    (changes) => setNodes(applyNodeChanges(changes, nodes)),
    [nodes, setNodes]
  );
  
  const onEdgesChange = useCallback(
    (changes) => setEdges(applyEdgeChanges(changes, edges)),
    [edges, setEdges]
  );

  const onConnect = useCallback(
    (params) => setEdges(addEdge(params, edges)),
    [edges, setEdges]
  );

  // 视觉状态(powered)现在完全由底层引擎在 usePLCStore 的 scanCycle 中计算并同步到 nodes/edges。
  // 所以这里不再需要前端手动的 while(changed) 的电流传播推导。

  // 将当前的 nodes 和 edges 同步回 store 供后端引擎使用
  // (这里为了快速演示，我们直接在 useEffect 里处理了视觉引擎。如果要严格分离，应把 nodes/edges 传给 evaluatePLC)

  return (
    <div style={{ width: '100%', height: '100%' }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={onConnect}
        nodeTypes={nodeTypes}
        fitView
        className="glass-panel"
        colorMode="dark"
      >
        <Background color="#ffffff" gap={20} size={1} opacity={0.05} />
        <Controls style={{ background: 'var(--panel-bg)', borderColor: 'var(--border-color)' }} />
      </ReactFlow>
    </div>
  );
}

import { Handle, Position } from '@xyflow/react';
import { usePLCStore } from '../../store/usePLCStore';
import './nodes.css';

export default function ContactNode({ id, data }) {
  const isRunning = usePLCStore((state) => state.isRunning);
  // Get the powered state and variable value
  // In a real editor, we'd map this node ID to the engine state.
  // For UI representation, let's just show it.
  
  // Contacts: NO / NC / P / N
  let label = '-| |-';
  if (data.type === 'NC') label = '-|/|-';
  else if (data.type === 'P') label = '-|P|-';
  else if (data.type === 'N') label = '-|N|-';
  
  return (
    <div className={`plc-node ${data.powered ? 'powered' : ''}`}>
      <Handle type="target" position={Position.Left} className="handle" />
      <div className="node-content">
        <span className="node-var">{data.variable}</span>
        <div className="node-symbol">{label}</div>
      </div>
      <Handle type="source" position={Position.Right} className="handle" />
    </div>
  );
}

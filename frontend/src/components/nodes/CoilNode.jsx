import { Handle, Position } from '@xyflow/react';
import './nodes.css';

export default function CoilNode({ id, data }) {
  let label = '-( )-';
  if (data.type === 'S') label = '-(S)-';
  else if (data.type === 'R') label = '-(R)-';

  return (
    <div className={`plc-node ${data.powered ? 'powered' : ''}`}>
      <Handle type="target" position={Position.Left} className="handle" />
      <div className="node-content">
        <span className="node-var">{data.variable}</span>
        <div className="node-symbol">{label}</div>
      </div>
    </div>
  );
}

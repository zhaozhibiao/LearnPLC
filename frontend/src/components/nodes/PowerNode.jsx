import { Handle, Position } from '@xyflow/react';
import './nodes.css';

export default function PowerNode({ id, data }) {
  // Power node is always powered (it's the source rail)
  return (
    <div className="power-rail-node powered">
      <Handle type="source" position={Position.Right} className="handle" />
    </div>
  );
}

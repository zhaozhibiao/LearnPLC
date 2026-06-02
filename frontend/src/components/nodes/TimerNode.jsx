import { Handle, Position } from '@xyflow/react';
import { usePLCStore } from '../../store/usePLCStore';
import './nodes.css';

export default function TimerNode({ id, data }) {
  const instances = usePLCStore((state) => state.instances);
  const updateNodeData = usePLCStore((state) => state.updateNodeData);
  
  // Backward compatibility: old instances used ton_, new use timer_
  const inst = instances[`timer_${id}`] || instances[`ton_${id}`] || { ET: 0, Q: false };
  
  const timerType = data.type || 'TON';
  const etSeconds = (inst.ET / 1000).toFixed(1);
  const ptSeconds = (Number(data.PT) / 1000).toFixed(1);

  const handlePtChange = (e) => {
    const val = parseFloat(e.target.value);
    if (!isNaN(val) && val >= 0) {
      updateNodeData(id, { PT: val * 1000 });
    }
  };

  return (
    <div className={`plc-node function-block ${inst.Q ? 'powered' : ''}`}>
      <div className="fb-header">
        <span className="fb-type">{timerType}</span>
        <span className="fb-name">{data.name || `Timer_${id}`}</span>
      </div>
      
      <div className="fb-body">
        <div className="fb-row">
          <div className="fb-pin-container">
            <Handle type="target" position={Position.Left} id="IN" className="handle handle-in" />
            <span className="fb-pin-label">IN</span>
          </div>
          <div className="fb-pin-container right">
            <span className="fb-pin-label">Q</span>
            <Handle type="source" position={Position.Right} id="Q" className="handle handle-out" />
          </div>
        </div>
        
        <div className="fb-row">
          <div className="fb-pin-container">
            <span className="fb-pin-label">PT</span>
          </div>
          <div className="fb-pin-value-editable">
            <input 
              type="number" 
              className="nodrag nopan"
              value={ptSeconds} 
              onChange={handlePtChange}
              step="0.1"
              min="0"
            />
            <span>s</span>
          </div>
        </div>
        
        <div className="fb-row">
          <div className="fb-pin-container">
            <span className="fb-pin-label">ET</span>
          </div>
          <div className="fb-pin-value">
            {etSeconds}s
          </div>
        </div>
      </div>
    </div>
  );
}

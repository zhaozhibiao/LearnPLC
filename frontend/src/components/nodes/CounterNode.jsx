import { Handle, Position } from '@xyflow/react';
import { usePLCStore } from '../../store/usePLCStore';
import './nodes.css';

export default function CounterNode({ id, data }) {
  const instances = usePLCStore((state) => state.instances);
  const updateNodeData = usePLCStore((state) => state.updateNodeData);
  
  const inst = instances[`counter_${id}`] || instances[`ctu_${id}`] || { CV: 0, Q: false };
  const counterType = data.type || 'CTU';

  const handlePvChange = (e) => {
    const val = parseInt(e.target.value, 10);
    if (!isNaN(val)) {
      updateNodeData(id, { PV: val });
    }
  };

  const handleResetVarChange = (e) => {
    updateNodeData(id, { resetVar: e.target.value.toUpperCase() });
  };

  const handleLoadVarChange = (e) => {
    updateNodeData(id, { loadVar: e.target.value.toUpperCase() });
  };

  return (
    <div className={`plc-node function-block ${inst.Q ? 'powered' : ''}`}>
      <div className="fb-header">
        <span className="fb-type">{counterType}</span>
        <span className="fb-name">{data.name || `Counter_${id}`}</span>
      </div>
      
      <div className="fb-body">
        {/* CU Handle for CTU/CTUD */}
        {(counterType === 'CTU' || counterType === 'CTUD') && (
          <div className="fb-row">
            <div className="fb-pin-container">
              <Handle type="target" position={Position.Left} id="CU" className="handle handle-in" />
              <span className="fb-pin-label">CU</span>
            </div>
            {counterType === 'CTU' && (
              <div className="fb-pin-container right">
                <span className="fb-pin-label">Q</span>
                <Handle type="source" position={Position.Right} id="Q" className="handle handle-out" />
              </div>
            )}
          </div>
        )}

        {/* CD Handle for CTD/CTUD */}
        {(counterType === 'CTD' || counterType === 'CTUD') && (
          <div className="fb-row">
            <div className="fb-pin-container">
              <Handle type="target" position={Position.Left} id="CD" className="handle handle-in" />
              <span className="fb-pin-label">CD</span>
            </div>
            {counterType !== 'CTU' && (
              <div className="fb-pin-container right">
                <span className="fb-pin-label">Q</span>
                {/* For CTD/CTUD, Q might be shared or named QU/QD. We use Q for simplicity */}
                <Handle type="source" position={Position.Right} id="Q" className="handle handle-out" />
              </div>
            )}
          </div>
        )}
        
        {/* Reset Var for CTU/CTUD */}
        {(counterType === 'CTU' || counterType === 'CTUD') && (
          <div className="fb-row">
            <div className="fb-pin-container">
              <span className="fb-pin-label">R Var</span>
            </div>
            <div className="fb-pin-value-editable">
              <input 
                type="text" 
                className="nodrag nopan"
                value={data.resetVar || ''} 
                onChange={handleResetVarChange}
                placeholder="M0"
              />
            </div>
          </div>
        )}

        {/* Load Var for CTD/CTUD */}
        {(counterType === 'CTD' || counterType === 'CTUD') && (
          <div className="fb-row">
            <div className="fb-pin-container">
              <span className="fb-pin-label">LD Var</span>
            </div>
            <div className="fb-pin-value-editable">
              <input 
                type="text" 
                className="nodrag nopan"
                value={data.loadVar || ''} 
                onChange={handleLoadVarChange}
                placeholder="M1"
              />
            </div>
          </div>
        )}

        <div className="fb-row">
          <div className="fb-pin-container">
            <span className="fb-pin-label">PV</span>
          </div>
          <div className="fb-pin-value-editable">
            <input 
              type="number" 
              className="nodrag nopan"
              value={data.PV !== undefined ? data.PV : 3} 
              onChange={handlePvChange}
            />
          </div>
        </div>
        
        <div className="fb-row">
          <div className="fb-pin-container">
            <span className="fb-pin-label">CV</span>
          </div>
          <div className="fb-pin-value">
            {inst.CV}
          </div>
        </div>
      </div>
    </div>
  );
}

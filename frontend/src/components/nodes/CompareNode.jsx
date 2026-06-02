import { Handle, Position } from '@xyflow/react';
import { usePLCStore } from '../../store/usePLCStore';
import './nodes.css';

export default function CompareNode({ id, data }) {
  const updateNodeData = usePLCStore((state) => state.updateNodeData);

  const handleOperatorChange = (e) => {
    updateNodeData(id, { operator: e.target.value });
  };

  const handleVar1Change = (e) => {
    updateNodeData(id, { var1: e.target.value.toUpperCase() });
  };

  const handleVal2Change = (e) => {
    updateNodeData(id, { val2: parseInt(e.target.value, 10) });
  };

  const op = data.operator || '>=';

  return (
    <div className={`plc-node function-block ${data.powered ? 'powered' : ''}`}>
      <div className="fb-header">
        <span className="fb-type">CMP</span>
        <select 
          className="fb-op-select nodrag nopan" 
          value={op} 
          onChange={handleOperatorChange}
        >
          <option value="==">==</option>
          <option value="!=">!=</option>
          <option value="&gt;">&gt;</option>
          <option value="&lt;">&lt;</option>
          <option value="&gt;=">&gt;=</option>
          <option value="&lt;=">&lt;=</option>
        </select>
      </div>
      
      <div className="fb-body">
        <div className="fb-row">
          <div className="fb-pin-container">
            <Handle type="target" position={Position.Left} id="EN" className="handle handle-in" />
            <span className="fb-pin-label">EN</span>
          </div>
          <div className="fb-pin-container right">
            <span className="fb-pin-label">OUT</span>
            <Handle type="source" position={Position.Right} id="OUT" className="handle handle-out" />
          </div>
        </div>
        
        <div className="fb-row">
          <div className="fb-pin-container">
            <span className="fb-pin-label">IN1</span>
          </div>
          <div className="fb-pin-value-editable">
            <input 
              type="text" 
              className="nodrag nopan"
              value={data.var1 || ''} 
              onChange={handleVar1Change}
              placeholder="Var1"
            />
          </div>
        </div>

        <div className="fb-row">
          <div className="fb-pin-container">
            <span className="fb-pin-label">IN2</span>
          </div>
          <div className="fb-pin-value-editable">
            <input 
              type="number" 
              className="nodrag nopan"
              value={data.isConstant ? (data.val2 !== undefined ? data.val2 : 0) : ''} 
              onChange={handleVal2Change}
              placeholder="Val2"
              disabled={!data.isConstant}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

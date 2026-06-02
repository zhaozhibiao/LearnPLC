import { Play, Square, Settings, Cpu } from 'lucide-react';
import { usePLCStore } from '../store/usePLCStore';
import './ControlPanel.css';

export default function ControlPanel() {
  const { variables, isRunning, toggleRunState, toggleVariable } = usePLCStore();

  return (
    <div className="control-panel glass-panel">
      <div className="panel-header">
        <Cpu size={20} className="icon-accent" />
        <h2>PLC Control</h2>
      </div>

      <div className="status-indicator">
        <div className={`led ${isRunning ? 'led-run' : 'led-stop'}`}></div>
        <span>{isRunning ? 'RUNNING' : 'STOPPED'}</span>
      </div>

      <div className="action-buttons">
        <button 
          className={`btn ${isRunning ? 'btn-stop' : 'btn-run'}`}
          onClick={toggleRunState}
        >
          {isRunning ? <><Square size={16} /> STOP</> : <><Play size={16} /> RUN</>}
        </button>
      </div>

      <div className="io-panel">
        <div className="io-section">
          <h3>Inputs & Memory</h3>
          <div className="io-grid">
            {Object.keys(variables).filter(k => k.startsWith('X') || k.startsWith('M')).map((k) => (
              <div key={k} className="io-item" onClick={() => toggleVariable(k)}>
                <span className="io-label">{k}</span>
                <div className={`io-switch ${variables[k] ? 'on' : 'off'}`}>
                  {variables[k] ? 'ON' : 'OFF'}
                </div>
              </div>
            ))}
          </div>
        </div>

        <div className="io-section">
          <h3>Outputs</h3>
          <div className="io-grid">
            {Object.keys(variables).filter(k => k.startsWith('Y')).map((y) => (
              <div key={y} className="io-item output-item">
                <span className="io-label">{y}</span>
                <div className={`io-led ${variables[y] ? 'on' : 'off'}`}></div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

import { usePLCStore } from '../store/usePLCStore';
import { Activity, Trash2 } from 'lucide-react';
import './TimingDiagram.css';

export default function TimingDiagram() {
  const { history, clearHistory, variables } = usePLCStore();

  // Define which variables to chart based on the current task variables
  const varsToPlot = Object.keys(variables).sort();

  // SVG Drawing constants
  const svgWidth = 800; // SVG viewBox width
  const svgHeight = varsToPlot.length * 50 + 20; // 50px per track + padding
  const trackHeight = 30; // Height of the square wave
  const trackSpacing = 50; // Spacing between tracks
  const startX = 50; // Margin left for labels

  const renderWaveform = (varName, index) => {
    if (history.length === 0) return null;

    const maxItems = 100;
    const points = [];
    const stepX = (svgWidth - startX - 20) / maxItems;

    let currentY = index * trackSpacing + 30; // Base line (OFF state)

    history.forEach((entry, i) => {
      const isHigh = entry.variables[varName] === true;
      const x = startX + i * stepX;
      const y = isHigh ? currentY - trackHeight : currentY;

      // Draw horizontal line for current state
      points.push(`${x},${y}`);
      
      // If there's a next point, draw vertical line to its Y if state changes
      if (i < history.length - 1) {
        const nextHigh = history[i+1].variables[varName] === true;
        const nextY = nextHigh ? currentY - trackHeight : currentY;
        const nextX = startX + (i + 1) * stepX;
        
        points.push(`${nextX},${y}`); // horizontal to next point's X
        if (y !== nextY) {
          points.push(`${nextX},${nextY}`); // vertical transition
        }
      }
    });

    const isOutput = varName.startsWith('Y');
    const color = isOutput ? 'var(--power-on)' : 'var(--accent-color)';

    return (
      <g key={varName}>
        {/* Track Label */}
        <text x="10" y={currentY - 10} fill="var(--text-primary)" fontSize="12" fontWeight="bold">
          {varName}
        </text>
        {/* Base Line (dashed) */}
        <line x1={startX} y1={currentY} x2={svgWidth - 20} y2={currentY} stroke="var(--border-color)" strokeDasharray="4" />
        {/* Waveform Polyline */}
        <polyline
          points={points.join(' ')}
          fill="none"
          stroke={color}
          strokeWidth="2"
        />
      </g>
    );
  };

  return (
    <div className="timing-diagram glass-panel">
      <div className="timing-header">
        <div className="title">
          <Activity size={18} className="icon-accent" />
          <h3>时序图分析 (Timing Diagram)</h3>
        </div>
        <button className="btn-clear" onClick={clearHistory} title="清除历史记录">
          <Trash2 size={16} />
        </button>
      </div>
      <div className="svg-container">
        <svg viewBox={`0 0 ${svgWidth} ${svgHeight}`} width="100%" height="100%">
          {varsToPlot.map((v, idx) => renderWaveform(v, idx))}
        </svg>
      </div>
    </div>
  );
}

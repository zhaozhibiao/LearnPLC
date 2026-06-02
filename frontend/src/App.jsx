import React from 'react';
import LadderEditor from './components/LadderEditor';
import ControlPanel from './components/ControlPanel';
import TaskPanel from './components/TaskPanel';
import TimingDiagram from './components/TimingDiagram';
import './App.css';

function App() {
  return (
    <div className="app-container">
      <header className="app-header glass-panel">
        <div className="logo">
          <div className="logo-icon">⚡</div>
          <h1>OpenPLC Web Simulator</h1>
        </div>
        <div className="header-info">
          <span className="badge">Route 1: Pure Client-Side</span>
        </div>
      </header>
      
      <main className="app-main">
        <aside className="sidebar-left">
          <ControlPanel />
        </aside>
        
        <section className="center-column">
          <div className="editor-container">
            <LadderEditor />
          </div>
          <TimingDiagram />
        </section>

        <aside className="sidebar-right">
          <TaskPanel />
        </aside>
      </main>
    </div>
  );
}

export default App;

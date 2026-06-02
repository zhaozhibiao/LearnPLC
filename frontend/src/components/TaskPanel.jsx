import { useState } from 'react';
import { usePLCStore } from '../store/usePLCStore';
import { evaluatePLC } from '../engine/plcEngine';
import { TASK_LIBRARY } from '../store/taskLibrary';
import { CheckCircle, XCircle, Award, List } from 'lucide-react';
import './TaskPanel.css';

export default function TaskPanel() {
  const { currentTaskId, loadTask, variables, nodes, edges } = usePLCStore();
  const [results, setResults] = useState(null);
  const [isVerifying, setIsVerifying] = useState(false);

  const currentTask = TASK_LIBRARY.find(t => t.id === currentTaskId);

  const handleVerify = () => {
    setIsVerifying(true);
    setResults(null);
    
    setTimeout(() => {
      const testResults = [];
      let currentTestVars = { ...currentTask.initialVariables };
      let currentTestInsts = {};

      for (const tc of currentTask.testCases) {
        let passed = false;

        if (tc.sequence) {
          let edgeDetected = false;
          for (let step of tc.sequence) {
            currentTestVars = { ...currentTestVars, ...step };
            const { newVariables, newInstances } = evaluatePLC(currentTestVars, currentTestInsts, nodes, edges, 50);
            currentTestVars = newVariables;
            currentTestInsts = newInstances;
            
            if (tc.expectEdge && currentTestVars[tc.expectEdge]) {
              edgeDetected = true;
            }
          }
          
          if (tc.expectEdge) {
            passed = edgeDetected;
          } else if (tc.expected) {
            passed = Object.keys(tc.expected).every(k => currentTestVars[k] === tc.expected[k]);
          } else {
            passed = true;
          }
        } else {
          currentTestVars = { ...currentTestVars, ...tc.input };
          const steps = (tc.holdMs || 50) / 50;
          for (let i = 0; i < steps; i++) {
            const { newVariables, newInstances } = evaluatePLC(currentTestVars, currentTestInsts, nodes, edges, 50);
            currentTestVars = newVariables;
            currentTestInsts = newInstances;
          }
          if (tc.expected) {
            passed = Object.keys(tc.expected).every(k => currentTestVars[k] === tc.expected[k]);
          } else {
            passed = true;
          }
        }
        
        testResults.push({
          name: tc.name,
          passed,
          expected: tc.expected,
          actual: { ...currentTestVars }
        });
      }

      setResults(testResults);
      setIsVerifying(false);
    }, 800);
  };

  const allPassed = results && results.length > 0 && results.every(r => r.passed);

  return (
    <div className="task-panel glass-panel">
      <div className="panel-header">
        <List size={20} className="icon-accent" />
        <h2>教学单元选择</h2>
      </div>
      
      <select 
        className="task-selector"
        value={currentTaskId}
        onChange={(e) => {
          loadTask(e.target.value);
          setResults(null);
        }}
      >
        {TASK_LIBRARY.map(t => (
          <option key={t.id} value={t.id}>{t.title}</option>
        ))}
      </select>

      <div className="panel-header" style={{ marginTop: '10px' }}>
        <Award size={20} className="icon-accent" />
        <h2>题目要求</h2>
      </div>

      <div className="task-content">
        <p>{currentTask.description}</p>
      </div>

      <button 
        className={`btn btn-verify ${isVerifying ? 'verifying' : ''}`}
        onClick={handleVerify}
        disabled={isVerifying}
      >
        {isVerifying ? '验证中...' : '提交验证'}
      </button>

      {results && (
        <div className="results-container">
          {allPassed ? (
            <div className="success-banner">
              <CheckCircle size={20} />
              <span>验证通过！代码符合要求。</span>
            </div>
          ) : (
            <div className="error-banner">
              <XCircle size={20} />
              <span>验证失败，请检查逻辑。</span>
            </div>
          )}

          <ul className="test-list">
            {results.map((res, idx) => (
              <li key={idx} className={`test-item ${res.passed ? 'passed' : 'failed'}`}>
                {res.passed ? <CheckCircle size={14} /> : <XCircle size={14} />}
                <span>{res.name}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

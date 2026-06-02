/**
 * 纯前端 PLC 核心执行引擎 (完全符合工业标准的拓扑排序执行)
 * 
 * @param {Object} variables 当前的变量状态 (X0, X1, Y0...)
 * @param {Object} instances 实例数据，用于保存定时器时间、计数器值和边沿状态
 * @param {Array} nodes 节点数组
 * @param {Array} edges 边数组
 * @param {Number} deltaTime 距离上一次扫描经过的毫秒数
 * @returns {Object} { newVariables, newInstances, nodePowers }
 */
export function evaluatePLC(variables, instances, nodes, edges, deltaTime = 0) {
  const newVariables = { ...variables };
  const newInstances = JSON.parse(JSON.stringify(instances)); // Deep clone for safety
  const nodePowers = {}; // 记录每个节点的输出电源状态

  function evaluateNode(node, inPower, handlePowers = {}) {
    let outPower = false;
    
    if (node.type === 'power' || node.type === 'POWER') {
      outPower = true;
    }
    else if (node.type === 'contact') {
      const val = newVariables[node.data.variable] === true;
      if (node.data.type === 'NO') {
        outPower = inPower && val;
      } else if (node.data.type === 'NC') {
        outPower = inPower && !val;
      } else if (node.data.type === 'P') {
        const instKey = `edge_${node.id}`;
        const prev = newInstances[instKey] || false;
        outPower = inPower && (!prev && val);
        newInstances[instKey] = val; 
      } else if (node.data.type === 'N') {
        const instKey = `edge_${node.id}`;
        const prev = newInstances[instKey];
        const prevResolved = prev !== undefined ? prev : val;
        outPower = inPower && (prevResolved && !val);
        newInstances[instKey] = val;
      }
    }
    else if (node.type === 'coil' || node.type === 'COIL') {
      outPower = inPower;
      if (node.data.type === 'COIL' || !node.data.type) {
        newVariables[node.data.variable] = inPower;
      } else if (node.data.type === 'S') {
        if (inPower) newVariables[node.data.variable] = true;
      } else if (node.data.type === 'R') {
        if (inPower) newVariables[node.data.variable] = false;
      }
    }
    else if (node.type === 'line' || node.type === 'LINE') {
      outPower = inPower;
    }
    else if (node.type === 'timer_ton' || node.type === 'timer') {
      const instKey = `timer_${node.id}`;
      if (!newInstances[instKey]) newInstances[instKey] = { ET: 0, Q: false, prevIN: false };
      
      const timerType = node.data.type || 'TON';
      const PT = Number(node.data.PT) || 0;
      const IN = handlePowers['IN'] !== undefined ? handlePowers['IN'] : inPower;
      const inst = newInstances[instKey];

      if (timerType === 'TON') {
        if (IN) {
          inst.ET += deltaTime;
          if (inst.ET >= PT) inst.Q = true;
        } else {
          inst.ET = 0;
          inst.Q = false;
        }
      } else if (timerType === 'TOF') {
        if (IN) {
          inst.ET = 0;
          inst.Q = true;
        } else {
          if (inst.Q) {
            inst.ET += deltaTime;
            if (inst.ET >= PT) {
              inst.Q = false;
              inst.ET = PT;
            }
          }
        }
      } else if (timerType === 'TP') {
        if (IN && !inst.prevIN && !inst.Q) {
          inst.Q = true;
          inst.ET = 0;
        }
        if (inst.Q) {
          inst.ET += deltaTime;
          if (inst.ET >= PT) {
            inst.Q = false;
            inst.ET = PT;
          }
        } else {
          inst.ET = 0;
        }
      }
      
      inst.prevIN = IN;
      if (node.data.etVar) newVariables[node.data.etVar] = inst.ET;
      outPower = inst.Q;
    }
    else if (node.type === 'counter_ctu' || node.type === 'counter') {
      const instKey = `counter_${node.id}`;
      if (!newInstances[instKey]) newInstances[instKey] = { CV: 0, prevCU: false, prevCD: false, Q: false };
      
      const counterType = node.data.type || 'CTU';
      const PV = Number(node.data.PV) || 0;
      const inst = newInstances[instKey];

      const CU = handlePowers['CU'] !== undefined ? handlePowers['CU'] : inPower;
      const CD = handlePowers['CD'] || false;
      const R = newVariables[node.data.resetVar] === true;
      const LD = newVariables[node.data.loadVar] === true;

      if (counterType === 'CTU') {
        if (R) inst.CV = 0;
        else if (CU && !inst.prevCU) inst.CV += 1;
        inst.Q = inst.CV >= PV;
      } else if (counterType === 'CTD') {
        if (LD) inst.CV = PV;
        else if (CD && !inst.prevCD) inst.CV -= 1;
        inst.Q = inst.CV <= 0;
      } else if (counterType === 'CTUD') {
        if (R) inst.CV = 0;
        else if (LD) inst.CV = PV;
        else {
          if (CU && !inst.prevCU) inst.CV += 1;
          if (CD && !inst.prevCD) inst.CV -= 1;
        }
        inst.Q = inst.CV >= PV; 
      }

      inst.prevCU = CU;
      inst.prevCD = CD;
      if (node.data.cvVar) newVariables[node.data.cvVar] = inst.CV;
      outPower = inst.Q;
    }
    else if (node.type === 'cmp_ge' || node.type === 'cmp') {
      const op = node.data.operator || '>=';
      const val1 = newVariables[node.data.var1] || 0;
      const val2 = node.data.isConstant ? Number(node.data.val2) : (newVariables[node.data.var2] || 0);
      let res = false;
      if (op === '==') res = val1 == val2;
      else if (op === '!=') res = val1 != val2;
      else if (op === '>') res = val1 > val2;
      else if (op === '<') res = val1 < val2;
      else if (op === '>=') res = val1 >= val2;
      else if (op === '<=') res = val1 <= val2;
      outPower = inPower && res;
    }

    return outPower;
  }

  const nodesMap = {};
  const inDegree = {};
  const adjacencyList = {};
  
  // 初始化
  nodes.forEach(node => {
    nodesMap[node.id] = { ...node, inPower: false, handlePowers: {} };
    inDegree[node.id] = 0;
    adjacencyList[node.id] = [];
  });

  // 根据 edges 构建邻接表和计算入度
  edges.forEach(edge => {
    if (nodesMap[edge.source] && nodesMap[edge.target]) {
      adjacencyList[edge.source].push({ target: edge.target, handle: edge.targetHandle });
      inDegree[edge.target]++;
    }
  });

  // 拓扑排序队列：所有入度为 0 的节点（通常是 power 或者悬空节点）
  const queue = [];
  Object.keys(inDegree).forEach(id => {
    if (inDegree[id] === 0) queue.push(id);
  });

  const evaluatedNodes = new Set();

  while (queue.length > 0) {
    const id = queue.shift();
    const node = nodesMap[id];
    evaluatedNodes.add(id);
    
    const outPower = evaluateNode(node, node.inPower, node.handlePowers);
    nodePowers[id] = outPower;
    
    adjacencyList[id].forEach(link => {
      const nextNode = nodesMap[link.target];
      if (nextNode) {
        if (link.handle) {
          nextNode.handlePowers[link.handle] = nextNode.handlePowers[link.handle] || outPower;
        } else {
          // 梯形图的并联即逻辑 OR
          nextNode.inPower = nextNode.inPower || outPower;
        }
        inDegree[link.target]--;
        if (inDegree[link.target] === 0) {
          queue.push(link.target);
        }
      }
    });
  }

  // 处理图中可能存在的环路或永远无法到达的悬空节点
  // 在标准的 PLC 中，悬空节点相当于 inPower = false
  Object.keys(nodesMap).forEach(id => {
    if (!evaluatedNodes.has(id)) {
      const node = nodesMap[id];
      const outPower = evaluateNode(node, false, {});
      nodePowers[id] = outPower;
    }
  });

  return { newVariables, newInstances, nodePowers };
}

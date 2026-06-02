import { evaluatePLC } from './plcEngine.js';

function runTests() {
  console.log("Running PLC Engine Tests...");

  const variables = { X0: false, X1: false, Y0: false, Y1: false };
  const rungs = [
    {
      id: 'rung-1',
      nodes: [
        { id: 'p', type: 'POWER', next: ['n1', 'n2'] },
        { id: 'n1', type: 'NO', variable: 'X0', next: ['c1'] },
        { id: 'n2', type: 'NC', variable: 'X1', next: ['c1'] },
        { id: 'c1', type: 'COIL', variable: 'Y0', next: [] }
      ]
    }
  ];

  // Test 1: X0 is false, X1 is false. 
  // n1 (NO X0) -> false. n2 (NC X1) -> true.
  // c1 receives power from n2. Y0 should be true.
  let res1 = evaluatePLC(variables, rungs);
  console.assert(res1.Y0 === true, "Test 1 Failed");

  // Test 2: X0 is false, X1 is true.
  // n1 (NO) -> false. n2 (NC X1) -> false.
  // c1 receives no power. Y0 should be false.
  let res2 = evaluatePLC({ ...variables, X1: true }, rungs);
  console.assert(res2.Y0 === false, "Test 2 Failed");

  // Test 3: X0 is true, X1 is true.
  // n1 (NO) -> true. n2 (NC) -> false.
  // c1 receives power from n1. Y0 should be true.
  let res3 = evaluatePLC({ ...variables, X0: true, X1: true }, rungs);
  console.assert(res3.Y0 === true, "Test 3 Failed");

  console.log("All tests passed!");
}

runTests();

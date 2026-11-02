#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Create an identity/hash-bound diagnostic-only eight-entry IS620N TxPDO config."""
import argparse,hashlib,json,pathlib

if not __debug__:
    raise SystemExit('Refuse optimized Python: configuration assertions must remain enabled')

def prepare(root):
    folder=root/'config'
    machine=(folder/'is620n-machine.json').read_bytes()
    io=(folder/'is620n-io-map.json').read_bytes()
    data=json.loads(machine);axis=data['axes'][0]
    assert len(data['axes'])==1 and axis['name']=='Axis1' and axis['slavePosition']==0
    slave=data['slaves'][0]
    assert int(slave['vendorId'],0)==0x100000 and int(slave['productCode'],0)==0xc0108 and int(slave['revisionNumber'],0)==0x10001
    assert data['cycleTimeMicroseconds']==1000
    assert json.loads(io)==dict(schemaVersion=1,groups=[])
    suffixes=[(0x6040,16,'Controlword'),(0x607a,32,'Target_position'),
              (0x60b1,32,'Velocity_offset'),(0x6060,8,'Modes_of_operation'),
              (0x603f,16,'Error_code'),(0x6041,16,'Statusword'),
              (0x6064,32,'Position_actual_value'),(0x606c,32,'Velocity_actual_value'),
              (0x6061,8,'Modes_of_operation_display'),(0x6062,32,'Position_demand'),
              (0x60f4,32,'Following_error'),(0x6077,16,'Torque_actual')]
    entries=[dict(index=index,subindex=0,bitLength=bits,key=f'Axis1Drive.{name}.0x{index:04X}:0') for index,bits,name in suffixes]
    topology=dict(schemaVersion=1,readyForRuntime=True,blockers=[],cycleTimeNs=1000000,
        slaves=[dict(position=0,vendorId=0x100000,productCode=0xc0108,revisionNo=0x10001,name='Axis1Drive',
            dc=dict(enabled=True,assignActivate=0x300,sync0CycleNs=1000000,sync0ShiftNs=0,sync1CycleNs=0),
            moduleIdents=[],startupSdos=[],syncManagers=[dict(index=2,direction='Rx',watchdog='Enable'),dict(index=3,direction='Tx',watchdog='Disable')],
            pdos=[dict(direction='Rx',syncManager=2,index=0x1600,entries=entries[:4]),
                  dict(direction='Tx',syncManager=3,index=0x1a00,entries=entries[4:])])])
    topology['topologyFingerprint']=hashlib.sha256(json.dumps(topology,sort_keys=True,separators=(',',':')).encode()).hexdigest()
    topology['machineSha256']=hashlib.sha256(machine).hexdigest()
    topology['ioMapSha256']=hashlib.sha256(io).hexdigest()
    path=folder/'axis-trace-ethercat.json'
    with path.open('x') as file:json.dump(topology,file,indent=2)
    return path

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('root',type=pathlib.Path)
    print(prepare(parser.parse_args().root.resolve()))

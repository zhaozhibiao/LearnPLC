"""Evaluate MC completion at both requested targets using the captured project tolerance."""
import math
def roundtrip(samples,distance,tolerance,counts_per_unit):
    if not samples or not all(math.isfinite(v) and v>0 for v in [distance,tolerance,counts_per_unit]):raise ValueError('Invalid completion contract')
    if not any(s['values']['FORWARDMOVE.BUSY'] for s in samples) or not any(s['values']['REVERSEMOVE.BUSY'] for s in samples):raise ValueError('Both Busy phases required')
    endpoints=[]
    for step,offset in [(20,distance),(100,0)]:
        candidates=[s for s in samples if s['values']['STEP']==step]
        if not candidates:raise ValueError('Completion sequence missing')
        sample=candidates[0];axis=sample['axis'];target=sample['values']['STARTPOSITION']+offset
        error=axis['actualPosition']-target
        if axis['motionDone'] is not True or axis['motionId']<=0 or abs(axis['motionTarget']-target)>1/counts_per_unit or abs(error)>tolerance+1/counts_per_unit:
            raise ValueError('Done endpoint violates project completion tolerance')
        endpoints.append(dict(step=step,target=target,actualPosition=axis['actualPosition'],error=error,motionId=axis['motionId']))
    if endpoints[0]['motionId']==endpoints[1]['motionId']:raise ValueError('Move identity reused')
    positions=[s['axis']['actualPosition'] for s in samples]
    return dict(criteria='project-Done-endpoint-v1',positionTolerance=tolerance,positionSpan=max(positions)-min(positions),finalPosition=positions[-1],
        endpoints=endpoints,steps=sorted({s['values']['STEP'] for s in samples}))

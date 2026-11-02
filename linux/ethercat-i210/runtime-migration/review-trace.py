"""Validate complete formal-runtime per-cycle motion records; never controls hardware."""
import argparse,csv,hashlib,json,pathlib
def review(path,rpm):
    with path.open() as stream:
        metadata=[next(stream).strip() for _ in range(3)]
        if metadata[0]!='# format=ecmotion-cyclic-axis-trace-v2' or metadata[2]!='# dropped=0':raise ValueError('Trace schema differs or frames dropped')
        lines=stream.readlines()
        if not lines or lines[-1].strip()!='# complete=1':raise ValueError('Trace export is incomplete')
        if any(line.startswith('#') for line in lines[:-1]):raise ValueError('Unexpected trace marker')
        rows=list(csv.DictReader(lines[:-1]))
    if len(rows)<100:raise ValueError('Insufficient frames')
    if any(None in r or any(value is None for value in r.values()) for r in rows):raise ValueError('Truncated CSV row')
    if any(int(r['error_code']) or int(r['statusword'])&8 or int(r['state'])==6 or r['bus_ready']!='1' for r in rows):
        raise ValueError('Drive, host protection or bus fault in complete capture')
    enabled=[r for r in rows if int(r['statusword'])&0x6f==0x27 and r['homed']=='1' and r['mode']=='8']
    if not enabled:raise ValueError('No enabled, homed CSP frames')
    if any(int(b['cycle'])!=int(a['cycle'])+1 or int(b['scheduled_ns'])-int(a['scheduled_ns'])!=1000000 for a,b in zip(rows,rows[1:])):raise ValueError('Per-cycle frames are not contiguous')
    for r in enabled:
        if r['bus_ready']!='1' or r['motion_timing_ready']!='1' or r['delivery_valid']!='1' or int(r['error_code'])!=0:raise ValueError('Bus, timing or drive error in enabled CSP region')
        if any(r[key]!='0' for key in ['pre_send_budget_overrun','actual_send_budget_overrun','send_host_deadline_overrun']):raise ValueError('Delivery budget exceeded in enabled CSP region')
    if any(r['torque_valid']!='0' or r['torque_raw']!='' for r in rows):raise ValueError('Unmapped torque falsely recorded as measured')
    speeds=[int(r['velocity_raw'])/1048576 for r in enabled]
    requested=rpm/60
    if max(speeds)<requested*.97 or min(speeds)>-requested*.97:raise ValueError('Requested speed peak was not reached in both directions')
    longest=run=0
    for r in enabled:
        run=run+1 if abs(int(r['drive_error_raw']))/1048576>.005 else 0
        longest=max(longest,run)
    if longest>=20:raise ValueError('Drive-local error exceeded auxiliary protection dwell')
    return dict(status='PASS',rpm=rpm,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),frames=len(rows),enabledHomedFrames=len(enabled),dropped=0,
        peakPositiveRpm=max(speeds)*60,peakNegativeRpm=min(speeds)*60,driveFollowingPeakRev=max(abs(int(r['drive_error_raw'])) for r in enabled)/1048576,
        demandActualPeakRev=max(abs(int(r['demand_raw'])-int(r['actual_raw'])) for r in enabled)/1048576,
        hostRawPeakRev=max(abs(int(r['sent_target_raw'])-int(r['actual_raw'])) for r in enabled)/1048576,
        longestAuxiliaryExcessCycles=longest,axisMotionIds=sorted({int(r['motion_id']) for r in enabled if int(r['motion_id'])}),torqueMeasured=False)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('path',type=pathlib.Path);p.add_argument('--rpm',type=int,required=True,choices=[300,1000,3000]);p.add_argument('--output',type=pathlib.Path);args=p.parse_args()
    result=review(args.path,args.rpm);output=args.output or pathlib.Path.cwd()/(args.path.name+'.review.json');output.write_text(json.dumps(result,indent=2));print(json.dumps(result))

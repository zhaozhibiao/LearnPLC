"""Offline shadow model only. Not approved or wired as a real-time safety function."""
from collections import deque
class DemandGuard:
 def __init__(self):
  self.history=deque(maxlen=3);self.last=None;self.excess=0;self.fault=None
 def update(self,*,cycle,scheduled_ns,sent_target_raw,demand_raw,actual_raw,drive_error_raw,valid):
  if not valid or (self.last and (cycle!=self.last[0]+1 or scheduled_ns-self.last[1]!=1000000)):
   self.fault=self.fault or 'freshness'
  ready=len(self.history)==3
  if ready and demand_raw!=self.history[0]:self.fault=self.fault or 'delivery'
  error=max(abs(demand_raw-actual_raw),abs(drive_error_raw))
  self.excess=self.excess+1 if ready and error>1048576*.005 else 0
  if self.excess>=20:self.fault=self.fault or 'following'
  self.last=(cycle,scheduled_ns);self.history.append(sent_target_raw)
  return dict(fault=self.fault,ready=ready,error_raw=error,excess_cycles=self.excess)

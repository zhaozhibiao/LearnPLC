#!/usr/bin/python3
"""Fixed topology boot helper. Root-owned, no user configuration/imports."""
import json,os,pathlib
HK='0-1,3-5,7';MASK='bb'
def main():
 p=pathlib.Path('/sys/devices/system/cpu')
 if (p/'cpu2/topology/thread_siblings_list').read_text().strip()!='2,6':raise RuntimeError('CPU topology changed; refuse isolation')
 for option in ('isolcpus=domain,managed_irq,2,6','nohz_full=2,6','rcu_nocbs=2,6','irqaffinity='+HK):
  if option not in pathlib.Path('/proc/cmdline').read_text().split():raise RuntimeError('Boot option missing: '+option)
 (p/'cpu6/online').write_text('0')
 pathlib.Path('/sys/devices/virtual/workqueue/cpumask').write_text(MASK)
 failures=[]
 for affinity in pathlib.Path('/proc/irq').glob('[0-9]*/smp_affinity_list'):
  try:affinity.write_text(HK)
  except OSError as e:failures.append({'irq':affinity.parent.name,'errno':e.errno})
 report={'online':(p/'online').read_text().strip(),'isolated':(p/'isolated').read_text().strip(),'housekeeping':HK,'workqueueMask':pathlib.Path('/sys/devices/virtual/workqueue/cpumask').read_text().strip(),'irqWriteExceptions':failures}
 pathlib.Path('/run/st-workbench-cpu-isolation.json').write_text(json.dumps(report,indent=2));print(json.dumps(report))
if __name__=='__main__':main()

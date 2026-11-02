# Fixed physical-core isolation profile
Target verified: Debian PREEMPT_RT6.12.111, logical0..7, SMTsiblings2,6. Reserve physicalcore2: RTmotionCPU2, CPU6offline. Housekeeping0,1,3,4,5,7.

Installed boot parameters: isolcpus=domain,managed_irq,2,6 nohz_full=2,6 rcu_nocbs=2,6 irqaffinity=0-1,3-5,7.
System/user manager default CPUAffinity excludes2,6; user.slice AllowedCPUs enforces exclusion even for Mesa/desktop threads that reset their own affinity. Global unbound workqueue maskbb and movable IRQaffinities use housekeeping. Boot oneshot validates topology/boot flags and offlinesCPU6.

Motion must launch as a dedicated SYSTEM service (systemd-run root broker). Its nonRT threads inherit housekeeping, then RTmain explicitly sets CPU2. sudo within a user login remains constrained by user.slice and cannot bypass cpuset; do not loosen user.slice for this. PLC realtime scan workers use othercores as intended. This does not grant system service privileges to ordinary users; existing broker authorization is unchanged.

2026-10-30 installation: root-owned backup /var/backups/st-workbench-cpu-isolation/20261030-074908. Rollback: sudo python3 <backup>/rollback.py, then controlled reboot. Persistent dropins and newGRUB are restored/removed via backup manifest. CPU6 is brought online by rollback. Runtime cpuset remains until reboot. Config does not start or enable any axis.

No absolute absence of interrupts is promised. PerCPU timers, IPIs and firmware activity can remain. IRQ0 and2 reject affinity writes (nonmovable legacy IRQs); postbootdeviceIRQ CPU2counts were0. Physical isolation is not by itself a realtime worst-case latency qualification or proof the following-error issue is fixed.

Scripts are explicit fixed-topology commissioning tools, not generic deployment API. Review actual state and topology before reuse. install.py verifies STOPPED/EMPTY via existing coordinator credential owner and drive not-enabled, then writes backup+GRUB/systemd config. Boot helper installed root-owned. verify.py checks current ordinary-process affinities and PLCstate without exposing credentials. latency-probe.c does not accessbus; after user.slice restriction launch only via authorized system service.

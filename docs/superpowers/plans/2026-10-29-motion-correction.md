# Motion correction implementation plan

Goal: correct cycle attribution and unit contracts, enable bounded trace export without bus stop, then verify on isolated Linux candidate.
Architecture: preserve CiA402 state/safety envelope; independent scale contracts with compatible defaults. Compare feedback to last published target, retain next-target diagnostic. Freeze trace through RT/non-RT handshake, export outside RT, explicitly rearm.

- [x] Record exact source/device baseline.
- [x] Add failing tests for sent-target error, independent velocity scales and invalid scale rejection.
- [x] Implement adapter/config corrections, preserve thresholds and both error diagnostics.
- [x] Add trace freeze/rearm tests, implement non-RT export with safe handshake.
- [x] Save reproducible overlay and hashes in linux/ethercat-i210/motion-correction.
- [x] Full independent Linux build/CTest, review changes.
- [x] Disabled acceptance, controlled candidate switch, confirm trace export keeps bus OP.
- [x] Repeat bounded motion without drive gain/threshold changes; report raw driver error and limits.

Verification scope: isolated candidate only. Formal engineering Runtime migration is not completed because the previously active candidate source bundle is absent. Full-speed and independent physical calibration are not claimed.

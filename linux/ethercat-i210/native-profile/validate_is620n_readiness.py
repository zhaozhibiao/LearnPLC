"""Offline evidence validation; --write-header creates a disabled-startup candidate.

readyForRuntime permits attempting master startup, NOT proven motion acceptance.
No device access. Custom remapping/DC must still be accepted at real OP/WKC.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re

RX = [0x60400010, 0x607A0020, 0x60FF0020, 0x60600008,
      0x60B80010, 0x60E00010, 0x60E10010]
TX = [0x603F0010, 0x60410010, 0x60640020, 0x60770010,
      0x60610008, 0x60F40020, 0x60B90010, 0x60BA0020,
      0x60BC0020, 0x606C0020]


def read_values(text):
    result = {}
    sections = re.split(r'=== SDO (0x[0-9a-fA-F]+):(\d+) \([^\n]+\) ===', text)
    for index in range(1, len(sections), 3):
        address, subindex, body = sections[index:index + 3]
        value = re.match(r'\s*(0x[0-9a-fA-F]+)\b', body)
        if value and 'READ_UNAVAILABLE' not in body.split('===')[0]:
            result[(int(address, 16), int(subindex))] = int(value.group(1), 16)
    return result


def evaluate(text, evidence=None, machine=None):
    values, blockers, maps = read_values(text), [], {}
    identity = all(values.get((0x1018, sub)) == expected for sub, expected in
                   [(1, 0x00100000), (2, 0x000C0108), (3, 0x00010001)])
    if not identity:
        blockers.append('Exact 0x1018 vendor/product/revision not confirmed.')
    if not re.search(r'\bSlaves:\s+1\b', text):
        blockers.append('Probe must show exactly one slave.')
    for index, expected in [(0x1703, RX), (0x1B04, TX)]:
        matched = values.get((index, 0)) == len(expected) and all(
            values.get((index, sub)) == value for sub, value in enumerate(expected, 1))
        maps[f'0x{index:04X}'] = matched
        if not matched:
            blockers.append(f'Fixed reference PDO 0x{index:04X} does not prove required object widths.')
    if not all(values.get((0x6091, sub)) == 1 for sub in [1, 2]):
        blockers.append('Electronic gear 0x6091:1/:2 must both read 1.')
    if values.get((0x6502, 0), 0) & 0xA0 != 0xA0:
        blockers.append('0x6502 must support CSP and homing.')
    if any(not 0 <= values.get((index, 0), -1) <= 10 for index in [0x1600, 0x1A00]):
        blockers.append('Custom PDO 0x1600/0x1A00 counts were not readable.')
    for index in [0x1C32, 0x1C33]:
        if values.get((index, 1)) != 2 or not values.get((index, 4), 0) & 4:
            blockers.append(f'0x{index:04X} must report active/supported DC Sync0.')
    if not 0 < values.get((0x1C32, 5), 0) <= 1000000:
        blockers.append('Reported minimum does not support the 1 ms Sync0 cycle.')
    evidence = evidence or {}
    if (not evidence.get('source', {}).get('url')
            or evidence.get('customPdoReadWrite') is not True
            or evidence.get('maximumCustomPdoEntries') != 10
            or evidence.get('velocityActualUnit') != 'encoder-counts-per-second'
            or evidence.get('motorModel') != 'ISMH1-10B30CB-U230Z'
            or evidence.get('encoderCountsPerMotorRevolution') != 1048576
            or evidence.get('dcAssignActivateCandidate') != 0x0300
            or evidence.get('dcSync1CycleNanoseconds') != 0):
        blockers.append('Explicit manufacturer/mechanics and standard Sync0 candidate evidence is incomplete.')
    try:
        axis, = machine['axes']
        mechanics = axis['mechanics']
        valid_machine = (axis['slavePosition'] == 0 and machine['cycleTimeMicroseconds'] == 1000
                         and axis['positionUnit'] == 'rev'
                         and mechanics['motorModel'] == evidence.get('motorModel')
                         and mechanics['encoderCountsPerMotorRevolution'] == 1048576
                         and mechanics['motorTurnsPerOutputTurn'] == 1
                         and mechanics['engineeringUnitsPerOutputTurn'] == 1
                         and axis['homing']['method'] == 35
                         and axis['homing']['allowCurrentPositionZero'] is True)
    except (KeyError, ValueError, TypeError):
        valid_machine = False
    if not valid_machine:
        blockers.append('Single-axis 20-bit/rev mechanics and authorized homing profile required.')
    return {'readyForRuntime': not blockers, 'startupPurpose': 'disabled commissioning launch candidate',
            'motionAcceptanceProven': False, 'identityMatches': identity,
            'fixedPdoMapsMatch': maps, 'blockers': blockers,
            'remainingRuntimeChecks': ['Exact online identity', 'Custom remapping accepted; all slaves OP and WKC complete',
                                       'CSP velocity offset 0x60B1 accepted in custom RxPDO 0x1600',
                                       'Standard 0x0300 Sync0 candidate accepted at 1 ms; Sync1 disabled',
                                       'Homing completion and commanded motion feedback safety checks']}


def write_header(path, result, evidence_digest):
    original = (Path(__file__).parent / 'src/EcMotion.Igh/generated/ethercat_config.hpp').read_text()
    begin, end = original.index('#pragma once'), original.index('inline constexpr bool ready_for_runtime')
    lines = ['// Generated by validate_is620n_readiness.py from recorded evidence.',
             '// Commissioning launch candidate; remapping/DC acceptance is not yet proven.',
             '// Runtime still gates exact identity, OP/WKC, commissioning and homing.',
             f'// Evidence SHA256: {evidence_digest}', original[begin:end].rstrip(),
             f'inline constexpr bool ready_for_runtime = {str(result["readyForRuntime"]).lower()};',
             'inline constexpr std::uint32_t cycle_time_ns = 1000000U;',
             f'inline constexpr std::size_t blocker_count = {len(result["blockers"])}U;']
    descriptors = [(0x6040, 16, 'Controlword'), (0x607A, 32, 'Target_position'),
                   (0x60B1, 32, 'Velocity_offset'), (0x6060, 8, 'Modes_of_operation'),
                   (0x603F, 16, 'Error_code'), (0x6041, 16, 'Statusword'),
                   (0x6064, 32, 'Position_actual_value'), (0x606C, 32, 'Velocity_actual_value'),
                   (0x6061, 8, 'Modes_of_operation_display')]
    fingerprint = hashlib.sha256(json.dumps([descriptors, 0x1600, 0x1A00, 0x0300, 1000000, 0,
                                           0x00100000, 0x000C0108, 0x00010001]).encode()).hexdigest()
    lines += [f'inline constexpr const char* topology_fingerprint = "{fingerprint}";',
              'inline constexpr std::array<Entry, 9> entries{{']
    lines += [f'    {{0x{index:04X}, 0, {bits}, "Axis1Drive.{name}.0x{index:04X}:0"}},'
              for index, bits, name in descriptors]
    lines += ['}};', 'inline constexpr std::array<Pdo, 2> pdos{{',
              '    {Direction::Rx, 2, 0x1600, 0U, 4U},', '    {Direction::Tx, 3, 0x1A00, 4U, 5U},', '}};',
              'inline constexpr std::array<std::uint32_t, 0> module_idents{};',
              'inline constexpr std::array<Slave, 1> slaves{{',
              '    {0, 0x00100000U, 0x000C0108U, 0x00010001U, "Axis1Drive", {true, 0x0300, 1000000U, 0, 0U}, 0U, 2U, 0U, 0U},',
              '}};', '} // namespace ecmotion::generated', '']
    Path(path).write_text('\n'.join(lines), encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('probes', nargs='+', type=Path)
    parser.add_argument('--evidence', type=Path)
    parser.add_argument('--machine', type=Path, default=Path(__file__).parent / 'config/is620n-machine.json')
    parser.add_argument('--write-header', type=Path)
    args = parser.parse_args()
    text = '\n'.join(probe.read_text(encoding='utf-8-sig') for probe in args.probes)
    evidence_text = args.evidence.read_text(encoding='utf-8-sig') if args.evidence else '{}'
    result = evaluate(text, json.loads(evidence_text), json.loads(args.machine.read_text()))
    digest = hashlib.sha256((text + evidence_text + args.machine.read_text()).encode()).hexdigest()
    result['evidenceSha256'] = digest
    if args.write_header:
        write_header(args.write_header, result, digest)
    print(json.dumps(result, indent=2, ensure_ascii=False))
    raise SystemExit(0 if result['readyForRuntime'] else 2)

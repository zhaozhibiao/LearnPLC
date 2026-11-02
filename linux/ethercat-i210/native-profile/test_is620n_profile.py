"""Offline commissioning contract; never accesses EtherCAT hardware."""
import json
from pathlib import Path
import unittest
import copy
from validate_is620n_readiness import evaluate, RX, TX

ROOT = Path(__file__).resolve().parent


class ProfileTests(unittest.TestCase):
    def test_single_axis_mechanics_and_limits(self):
        machine = json.loads((ROOT / 'config/is620n-machine.json').read_text())
        self.assertEqual(len(machine['slaves']), 1)
        self.assertEqual(len(machine['axes']), 1)
        axis = machine['axes'][0]
        self.assertEqual(axis['slavePosition'], 0)
        self.assertEqual(axis['positionUnit'], 'rev')
        mechanics = axis['mechanics']
        scale = (mechanics['encoderCountsPerMotorRevolution'] *
                 mechanics['motorTurnsPerOutputTurn'] / mechanics['engineeringUnitsPerOutputTurn'])
        self.assertEqual(scale, 1048576)
        self.assertEqual(mechanics['motorModel'], 'ISMH1-10B30CB-U230Z')
        self.assertEqual((axis['softLimitNegative'], axis['softLimitPositive']), (-1, 1))
        self.assertEqual(axis['maximumVelocity'], 1.0)
        self.assertEqual(axis['maximumAcceleration'], 2.0)
        self.assertEqual(axis['maximumDeceleration'], 2.0)
        self.assertEqual(axis['motionSafety']['maximumActualVelocity'], 1.5)
        self.assertEqual(axis['motionSafety']['maximumFollowingError'], 0.005)
        self.assertEqual(axis['motionSafety']['followingErrorTimeoutMilliseconds'], 20)
        self.assertEqual(axis['homing']['method'], 35)
        self.assertIs(axis['homing']['allowCurrentPositionZero'], True)

    def test_no_invented_io(self):
        io = json.loads((ROOT / 'config/is620n-io-map.json').read_text())
        self.assertEqual(io, {'schemaVersion': 1, 'groups': []})

    def test_minimal_candidate_has_exact_identity_and_no_torque_outputs(self):
        header = (ROOT / 'src/EcMotion.Igh/generated/is620n_ethercat_config.hpp').read_text()
        self.assertIn('0x000C0108U, 0x00010001U', header)
        self.assertIn('std::array<Slave, 1>', header)
        self.assertNotIn('MainIoRack', header)
        self.assertNotIn('0x1701,', header)
        self.assertNotIn('0x1B01,', header)
        self.assertIn('Direction::Rx, 2, 0x1600, 0U, 4U', header)
        self.assertIn('Direction::Tx, 3, 0x1A00, 4U, 5U', header)
        self.assertNotIn('{0x60E0,', header)
        self.assertNotIn('{0x60E1,', header)

    def test_matching_maps_cannot_bypass_manufacturer_evidence(self):
        values = [(0x1018, 1, 0x00100000), (0x1018, 2, 0x000C0108),
                  (0x1018, 3, 0x00010001), (0x6091, 1, 1), (0x6091, 2, 1)]
        for address, entries in [(0x1703, RX), (0x1B04, TX)]:
            values.append((address, 0, len(entries)))
            values.extend((address, sub, entry) for sub, entry in enumerate(entries, 1))
        probe = '\n'.join(f'=== SDO 0x{address:04x}:{sub} (uint32) ===\n0x{value:08x} {value}'
                          for address, sub, value in values)
        result = evaluate(probe)
        self.assertTrue(result['identityMatches'])
        self.assertTrue(all(result['fixedPdoMapsMatch'].values()))
        self.assertFalse(result['readyForRuntime'])
        self.assertTrue(any('manufacturer' in item for item in result['blockers']))

    def test_probe_errors_do_not_confirm_mapping(self):
        result = evaluate('=== SDO 0x1703:0 (uint8) ===\n'
                          'SDO transfer aborted with code 0x06020000\nREAD_UNAVAILABLE')
        self.assertFalse(result['identityMatches'])
        self.assertFalse(result['fixedPdoMapsMatch']['0x1703'])

    def test_candidate_readiness_requires_gear_modes_dc_and_motor_evidence(self):
        values = [(0x1018, 1, 0x00100000), (0x1018, 2, 0x000C0108),
                  (0x1018, 3, 0x00010001), (0x6091, 1, 1), (0x6091, 2, 1),
                  (0x6502, 0, 0x3AD), (0x1600, 0, 3), (0x1A00, 0, 7),
                  (0x1C32, 1, 2), (0x1C32, 4, 4), (0x1C32, 5, 125000),
                  (0x1C33, 1, 2), (0x1C33, 4, 4)]
        for address, entries in [(0x1703, RX), (0x1B04, TX)]:
            values.append((address, 0, len(entries)))
            values.extend((address, sub, entry) for sub, entry in enumerate(entries, 1))
        def probe(source):
            return 'Slaves: 1\n' + '\n'.join(
                f'=== SDO 0x{address:04x}:{sub} (uint32) ===\n0x{value:08x} {value}'
                for address, sub, value in source)
        evidence = json.loads((ROOT / 'config/is620n-manufacturer-evidence.json').read_text())
        machine = json.loads((ROOT / 'config/is620n-machine.json').read_text())
        result = evaluate(probe(values), evidence, machine)
        self.assertTrue(result['readyForRuntime'])
        self.assertFalse(result['motionAcceptanceProven'])
        for changed_key, bad_value in [((0x6091, 1), 2), ((0x6502, 0), 0),
                                        ((0x1C32, 4), 0), ((0x1C32, 5), 2000000)]:
            changed = [(address, sub, bad_value if (address, sub) == changed_key else value)
                       for address, sub, value in values]
            self.assertFalse(evaluate(probe(changed), evidence, machine)['readyForRuntime'])
        bad_motor = copy.deepcopy(machine)
        bad_motor['axes'][0]['mechanics']['encoderCountsPerMotorRevolution'] = 8388608
        self.assertFalse(evaluate(probe(values), evidence, bad_motor)['readyForRuntime'])


if __name__ == '__main__':
    unittest.main()

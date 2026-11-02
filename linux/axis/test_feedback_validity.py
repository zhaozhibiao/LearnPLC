import datetime
import unittest

import axis_worker as w
from test_axis_worker import Fake, manifest, packet


class FeedbackValidityTests(unittest.TestCase):
    def worker(self):
        self.clock = 1.
        fake = Fake(physical=True)
        fake.axis.update(homed=True, actualPosition=12.5)
        worker = w.Worker(w.Manifest.from_object(manifest()), fake, 'EcMotion',
                          now=lambda: self.clock)
        worker.probe()
        return worker, fake

    def assert_invalid(self, worker):
        image = worker.feedback()
        self.assertEqual(len(image), 5632)
        self.assertEqual(w.FEEDBACK.format, '<9Q2d')
        self.assertEqual(w.FEEDBACK.size, 88)
        for index in range(64):
            feedback = w.FEEDBACK.unpack_from(image, index * w.FEEDBACK.size)
            self.assertEqual(feedback[5:8], (0, 0, 0), index)
            self.assertFalse(feedback[2], index)
        self.assertFalse(worker.motion_ready)
        for field in ('ControllerReady', 'PhysicalBasicAxis', 'MotionReady'):
            self.assertFalse(worker.capabilities()[field], field)

    def test_shutdown_invalidates_idle_and_unconfigured_slots(self):
        worker, fake = self.worker()
        # No lease or command is needed for a cached monitoring sample.
        worker.slots[63].sample = dict(worker.slots[0].sample)
        worker.slots[63].done = True
        worker.shutdown()
        self.assert_invalid(worker)
        self.assertFalse(any(kind == 'AcquireControlRequest' for kind, _ in fake.calls))

    def test_watchdog_invalidates_completed_command_and_stays_invalid_after_poll(self):
        worker, fake = self.worker()
        worker.scan(packet())
        worker.poll()
        self.assertTrue(worker.slots[0].done)
        self.clock += 2.
        worker.tick()
        worker.poll()
        self.assert_invalid(worker)

    def test_unknown_payload_and_stalled_actual_cycle_invalidate_previous_sample(self):
        for failure in ('unknown_state', 'invalid_number', 'stalled_cycle', 'stale_timestamp'):
            with self.subTest(failure=failure):
                worker, fake = self.worker()
                if failure == 'unknown_state':
                    fake.axis['state'] = 'Unknown'
                elif failure == 'invalid_number':
                    fake.axis['actualPosition'] = float('nan')
                elif failure == 'stalled_cycle':
                    fake.cycle -= 1
                    self.clock += 2.
                else:
                    request = fake.request
                    def stale(kind, payload, expected=None):
                        result = request(kind, payload, expected)
                        if kind == 'ReadSnapshotRequest':
                            result['timestamp'] = (datetime.datetime.now(datetime.timezone.utc)
                                                   - datetime.timedelta(seconds=10)).isoformat()
                        return result
                    fake.request = stale
                worker.poll()
                self.assertEqual(worker.failed, 2004)
                self.assert_invalid(worker)

    def test_fresh_command_errors_leave_actual_data_readable(self):
        for state, kind, flags, position, error in (
                ('ErrorStop', 1, 0, 0., 2005),
                ('Standstill', 5, 1, 0., 2006),
                ('Disabled', 5, 0, 101., 2007)):
            with self.subTest(state=state, error=error):
                worker, fake = self.worker()
                fake.axis['state'] = state
                worker.poll()
                worker.scan(packet(kind=kind, flags=flags, position=position))
                feedback = w.FEEDBACK.unpack_from(worker.feedback())
                self.assertEqual(feedback[3], error)
                self.assertEqual(feedback[5:8], (int(state == 'Standstill'), 1, w.STATES[state]))
                self.assertEqual(feedback[9:], (12.5, 0.))
                self.assertEqual(worker.failed, 0)
                self.assertFalse(feedback[2])


if __name__ == '__main__':
    unittest.main()

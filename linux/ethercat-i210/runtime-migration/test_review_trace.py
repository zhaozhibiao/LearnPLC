import csv,importlib.util,pathlib,tempfile,unittest
spec=importlib.util.spec_from_file_location('trace_review',pathlib.Path(__file__).with_name('review-trace.py'));module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
class Tests(unittest.TestCase):
    def fixture(self,path,footer=True,fault=False):
        fields=['cycle','scheduled_ns','statusword','homed','mode','bus_ready','motion_timing_ready','delivery_valid','error_code','pre_send_budget_overrun','actual_send_budget_overrun','send_host_deadline_overrun','torque_valid','torque_raw','velocity_raw','drive_error_raw','demand_raw','actual_raw','sent_target_raw','motion_id','state']
        with path.open('w',newline='') as stream:
            stream.write('# format=ecmotion-cyclic-axis-trace-v2\n# capacity=120000\n# dropped=0\n');writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader()
            for i in range(100):
                row={key:0 for key in fields};row.update(cycle=i+1,scheduled_ns=i*1000000,statusword=0x27,homed=1,mode=8,bus_ready=1,motion_timing_ready=1,delivery_valid=1,torque_raw='',velocity_raw=5*1048576*(1 if i<50 else -1),motion_id=1 if i<50 else 2,state=3)
                if fault and i==99:row.update(statusword=8,error_code=3592,homed=0,state=6)
                writer.writerow(row)
            if footer:stream.write('# complete=1\n')
    def test_complete_native_csv_is_readable(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'wave.csv';self.fixture(path)
            self.assertEqual(module.review(path,300)['frames'],100)
    def test_missing_completion_marker_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'wave.csv';self.fixture(path,footer=False)
            with self.assertRaises(ValueError):module.review(path,300)
    def test_drive_fault_after_speed_peaks_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'wave.csv';self.fixture(path,fault=True)
            with self.assertRaises(ValueError):module.review(path,300)
if __name__=='__main__':unittest.main()

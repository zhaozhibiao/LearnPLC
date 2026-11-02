import importlib.util,json,pathlib,unittest
spec=importlib.util.spec_from_file_location('acceptance_results',pathlib.Path(__file__).with_name('acceptance-results.py'));module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
class Tests(unittest.TestCase):
    def fixture(self):
        return [dict(values=dict(STEP=10,STARTPOSITION=0,**{'FORWARDMOVE.BUSY':True,'REVERSEMOVE.BUSY':False}),axis=dict(actualPosition=22.50031,motionTarget=22.5,motionDone=False,motionId=1)),
            dict(values=dict(STEP=20,STARTPOSITION=0,**{'FORWARDMOVE.BUSY':False,'REVERSEMOVE.BUSY':False}),axis=dict(actualPosition=22.500064,motionTarget=22.5,motionDone=True,motionId=1)),
            dict(values=dict(STEP=30,STARTPOSITION=0,**{'FORWARDMOVE.BUSY':False,'REVERSEMOVE.BUSY':True}),axis=dict(actualPosition=-.000231,motionTarget=0,motionDone=False,motionId=2)),
            dict(values=dict(STEP=100,STARTPOSITION=0,**{'FORWARDMOVE.BUSY':False,'REVERSEMOVE.BUSY':False}),axis=dict(actualPosition=-.000069,motionTarget=0,motionDone=True,motionId=2))]
    def test_endpoint_criterion_uses_project_completion_tolerance(self):
        result=module.roundtrip(self.fixture(),22.5,.0001,1048576)
        self.assertGreater(result['positionSpan']-22.5,.0005)
        self.assertLess(max(abs(e['error']) for e in result['endpoints']),.0001)
    def test_wrong_endpoint_or_reused_motion_id_rejected(self):
        for change in [dict(actualPosition=.0002),dict(motionTarget=1),dict(motionId=1),dict(motionDone=False)]:
            samples=self.fixture();samples[-1]['axis'].update(change)
            with self.assertRaises(ValueError):module.roundtrip(samples,22.5,.0001,1048576)
if __name__=='__main__':unittest.main()

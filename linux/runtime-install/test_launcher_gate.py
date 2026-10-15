import hashlib
import importlib.util
import json
import pathlib
import tempfile
import unittest
spec=importlib.util.spec_from_file_location('gate',pathlib.Path(__file__).with_name('launcher-gate.py'))
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
class GateTests(unittest.TestCase):
    def test_native_loader_suffix_variants_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root=pathlib.Path(folder);(root/'source/build').mkdir(parents=True)
            candidate=root/'source/build/libplc_other.so.old'
            candidate.write_bytes(b'unapproved loader match')
            with self.assertRaises(ValueError): module.validate(root)
    def test_empty_reserved_target_and_rollback_allowed(self):
        with tempfile.TemporaryDirectory() as folder:
            root=pathlib.Path(folder);(root/'source/build').mkdir(parents=True);(root/'deployment').mkdir()
            target=root/'source/build/libplc_stworkbench.so'
            active={'runtime_commit':module.REVISION,'build_hash':None,'library_path':str(target),'library_sha256':None}
            (root/'deployment/active.json').write_text(json.dumps(active),encoding='utf-8')
            (root/'deployment/transaction.json').write_text(json.dumps({'state':'rolled-back','old_active':active}),encoding='utf-8')
            module.validate(root)
            active['library_path']=str(root/'outside.so')
            (root/'deployment/active.json').write_text(json.dumps(active),encoding='utf-8')
            (root/'deployment/transaction.json').write_text(json.dumps({'state':'rolled-back','old_active':active}),encoding='utf-8')
            with self.assertRaises(ValueError): module.validate(root)
    def test_empty_fresh_install_allowed(self):
        with tempfile.TemporaryDirectory() as folder:
            root=pathlib.Path(folder);(root/'source/build').mkdir(parents=True)
            module.validate(root)
    def test_unapproved_library_and_pending_transaction_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            root=pathlib.Path(folder);(root/'source/build').mkdir(parents=True)
            library=root/'source/build/libplc_stworkbench.so';library.write_bytes(b'approved bytes')
            with self.assertRaises(ValueError): module.validate(root)
            (root/'deployment').mkdir()
            active={'runtime_commit':module.REVISION,'library_path':str(library),'library_sha256':hashlib.sha256(library.read_bytes()).hexdigest()}
            (root/'deployment/active.json').write_text(json.dumps(active),encoding='utf-8')
            module.validate(root)
            (root/'deployment/transaction.json').write_text(json.dumps({'state':'swapped','new_active':active}),encoding='utf-8')
            with self.assertRaises(ValueError): module.validate(root)
            (root/'deployment/transaction.json').write_text(json.dumps({'state':'committed','new_active':active}),encoding='utf-8')
            module.validate(root)
            library.write_bytes(b'changed bytes')
            with self.assertRaises(ValueError): module.validate(root)
    def test_rollback_marker_must_match_active(self):
        with tempfile.TemporaryDirectory() as folder:
            root=pathlib.Path(folder);(root/'source/build').mkdir(parents=True);(root/'deployment').mkdir()
            active={'runtime_commit':module.REVISION,'library_path':None,'library_sha256':None}
            (root/'deployment/active.json').write_text(json.dumps(active),encoding='utf-8')
            (root/'deployment/transaction.json').write_text(json.dumps({'state':'rolled-back','old_active':{}}),encoding='utf-8')
            with self.assertRaises(ValueError): module.validate(root)
if __name__=='__main__': unittest.main()

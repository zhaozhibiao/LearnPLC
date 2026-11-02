import hashlib,json,pathlib,tarfile,tempfile,unittest
from install_overlay import apply
PACKAGE=pathlib.Path(__file__).parent
ARCHIVE=PACKAGE.parents[2]/'artifacts/cyclic-trace-20261023/source-snapshot.tar.gz'
class Tests(unittest.TestCase):
 def prepare(self,root):
  manifest=json.loads((PACKAGE/'source-manifest.json').read_text())
  with tarfile.open(ARCHIVE) as t:
   for name,info in manifest.items():
    if info['baseSha256'] is not None:
     f=root/name;f.parent.mkdir(parents=True,exist_ok=True);f.write_bytes(t.extractfile(name).read())
  return manifest
 def test_exact_overlay_and_idempotence(self):
  with tempfile.TemporaryDirectory() as d:
   root=pathlib.Path(d);m=self.prepare(root)
   self.assertEqual(apply(root),len(m));self.assertEqual(apply(root),0)
   for n,i in m.items():self.assertEqual(hashlib.sha256((root/n).read_bytes()).hexdigest(),i['patchedSha256'])
 def test_last_file_mismatch_prevents_all_writes(self):
  with tempfile.TemporaryDirectory() as d:
   root=pathlib.Path(d);m=self.prepare(root);f=root/list(m)[-1];f.parent.mkdir(parents=True,exist_ok=True);f.write_bytes(b'unrelated')
   before={n:(root/n).read_bytes() if (root/n).exists() else None for n in m}
   with self.assertRaises(ValueError):apply(root)
   self.assertEqual(before,{n:(root/n).read_bytes() if (root/n).exists() else None for n in m})
if __name__=='__main__':unittest.main()

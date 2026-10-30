"""Apply the source-only correction to the exact recorded diagnostic snapshot."""
import argparse
import hashlib
import json
from pathlib import Path


def apply(root: Path):
    package = Path(__file__).resolve().parent
    manifest = json.loads((package / 'source-manifest.json').read_text())
    pending = []
    root = root.resolve()
    for name, identity in manifest.items():
        destination = (root / name).resolve()
        if root not in destination.parents:
            raise ValueError('Source destination escapes root')
        source = (package / 'overlay' / name).read_bytes()
        if hashlib.sha256(source).hexdigest() != identity['patchedSha256']:
            raise ValueError('Overlay hash differs: ' + name)
        existing = hashlib.sha256(destination.read_bytes()).hexdigest() if destination.exists() else None
        if existing == identity['patchedSha256']:
            continue
        if existing != identity['baseSha256']:
            raise ValueError('Source baseline differs: ' + name)
        pending.append((destination, source))
    for destination, source in pending:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(source)
    return len(pending)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    print('Source files updated:', apply(parser.parse_args().root))

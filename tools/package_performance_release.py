"""Package an already built Release DLL and verify saved regression results."""
from pathlib import Path
import difflib
import hashlib
import json
import shutil
import zipfile

root = Path(__file__).resolve().parents[1]
analysis = root / 'analysis_outputs'
baseline = analysis / 'performance_baseline_20260918'
before = json.loads((analysis / 'performance-baseline.json').read_text())
after = json.loads((analysis / 'performance-current.json').read_text())
assert before == after, 'Baseline regression mismatch; refusing to package'

destination = root / 'artifacts/performance-release'
destination.mkdir(parents=True, exist_ok=True)
(destination / 'rollback').mkdir(exist_ok=True)
for name in ['DomaStatsBranch.dll', 'DomaStatsBranch.pdb']:
    shutil.copy2(root / 'bin/Release/net48' / name, destination / name)
shutil.copy2(root / 'config/doma_stats/xg_model.json', destination / 'xg_model.json')
shutil.copy2(baseline / 'DomaStatsBranch.dll', destination / 'rollback/DomaStatsBranch.dll')
shutil.copy2(root / 'PERFORMANCE_CHANGES.md', destination / 'PERFORMANCE_CHANGES.md')

files = [root / 'DomaStatsBranch.csproj']
for folder in ['Codebase', 'Codebase.Configs', 'OPLStats_Stats', 'OPLStats_Stats.Configs']:
    files.extend(sorted((root / folder).rglob('*.cs')))
patch = []
changed = []
for path in files:
    relative = path.relative_to(root).as_posix()
    old = (baseline / relative).read_text(encoding='utf-8-sig') if (baseline / relative).exists() else ''
    new = path.read_text(encoding='utf-8-sig')
    if old == new:
        continue
    changed.append(relative)
    patch.extend(difflib.unified_diff(old.splitlines(True), new.splitlines(True), fromfile='a/' + relative, tofile='b/' + relative))
(analysis / 'performance-changes.patch').write_text(''.join(patch), encoding='utf-8')
manifest = {
    'build': 'Release net48',
    'baselineCheckpointsMatched': len(after),
    'installed': False,
    'changedProductionFiles': changed,
    'sha256': {p.relative_to(destination).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(destination.rglob('*')) if p.is_file() and p.name != 'manifest.json'},
    'benchmarkBefore': json.loads((analysis / 'benchmark-baseline.json').read_text()),
    'benchmarkAfter': json.loads((analysis / 'benchmark-current.json').read_text()),
}
(destination / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
with zipfile.ZipFile(root / 'artifacts/DomaStatsBranch-performance-release.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(destination.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(destination))
print(f'PASS: {len(after)} exact baseline checkpoints; {len(changed)} changed production files; packaged {destination}')
print('Release DLL SHA256: ' + manifest['sha256']['DomaStatsBranch.dll'])

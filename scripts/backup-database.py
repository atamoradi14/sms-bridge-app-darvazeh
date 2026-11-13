"""Take and verify an online SQLite backup on the worker and retain a local copy."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
KUBE = ['kubectl', '--context', 'ravand', '-n', 'darvazeh']
claim = json.loads(subprocess.check_output(KUBE + ['get', 'pvc', 'fieldsense-api-data', '-o', 'json']))
volume = json.loads(subprocess.check_output(['kubectl', '--context', 'ravand', 'get', 'pv', claim['spec']['volumeName'], '-o', 'json']))
storage = (volume['spec'].get('local') or volume['spec'].get('hostPath'))['path']
assert storage.startswith('/var/lib/rancher/k3s/storage/'), 'Unexpected storage path'
assert 'darvazeh' in storage, 'Storage does not belong to Darvazeh'
stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
remote = '/srv/darvazeh/backups/fieldsense-' + stamp + '.db'
script = 'source=' + repr(storage + '/fieldsense.db') + '\ndestination=' + repr(remote) + '\n' + '''
import sqlite3,hashlib,json,os
from pathlib import Path
from contextlib import closing
target=Path(destination)
target.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
with closing(sqlite3.connect('file:'+source+'?mode=ro',uri=True)) as src, closing(sqlite3.connect(destination)) as dst:
    src.backup(dst)
    assert dst.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
    counts={table:dst.execute('SELECT COUNT(*) FROM '+table).fetchone()[0] for table in ['Users','Devices','Messages','OutboundJobs']}
target.chmod(0o600)
print(json.dumps({'file':destination,'sha256':hashlib.sha256(target.read_bytes()).hexdigest(),'counts':counts}))
'''
result = subprocess.run(['ssh', '-o', 'BatchMode=yes', 'ravand-worker-01', 'python3', '-'], input=script.encode(), capture_output=True, check=True)
evidence = json.loads(result.stdout)
local = ROOT / '.local/backups' / ('fieldsense-' + stamp + '.db')
local.parent.mkdir(parents=True, exist_ok=True)
subprocess.run(['scp', '-o', 'BatchMode=yes', '-q', 'ravand-worker-01:' + remote, str(local)], check=True)
assert hashlib.sha256(local.read_bytes()).hexdigest() == evidence['sha256'], 'Backup transfer checksum mismatch'
local.with_suffix('.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
print('Verified worker + off-node SQLite backup:', local)
print('SHA-256:', evidence['sha256'])
print('Preserved row counts:', json.dumps(evidence['counts']))

#!/usr/bin/env python3
"""Add only Darvazeh's DNS records and routes to the existing worker gateway."""
import argparse
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
HOSTS = ('darvazeh.daryajan.com', 'api1.daryajan.com')
WORKER_IP = '2.29.120.138'
KUBE = ['kubectl', '--context', 'ravand', '-n', 'hamafarin-apps']
START = '# BEGIN DARVAZEH PUBLIC ENDPOINTS'
END = '# END DARVAZEH PUBLIC ENDPOINTS'


def command(args, data=None):
    result = subprocess.run(args, input=data, capture_output=True)
    if result.returncode:
        raise RuntimeError(f'{args[0]} operation failed; private command output withheld')
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--gateway-repo', type=Path, default=ROOT.parent / 'hamafarin-cluster')
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('deployment_env', ROOT / 'scripts/deployment-env.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    values = module.load(ROOT / '.env')
    token = values.get('CLOUDFLARE_API_TOKEN')
    if not token:
        raise RuntimeError('Add CLOUDFLARE_API_TOKEN to root .env')

    def cloudflare(path, method='GET', data=None):
        request = urllib.request.Request('https://api.cloudflare.com/client/v4/' + path,
            method=method, data=json.dumps(data).encode() if data is not None else None,
            headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=40) as response:
            result = json.load(response)
        if not result.get('success'):
            raise RuntimeError('Cloudflare operation rejected; response withheld')
        return result['result']

    zones = cloudflare('zones?name=daryajan.com')
    if len(zones) != 1:
        raise RuntimeError('Expected one accessible daryajan.com zone')
    zone = zones[0]['id']
    records = {host: cloudflare(f'zones/{zone}/dns_records?name={host}') for host in HOSTS}
    for host, items in records.items():
        if items and not (len(items) == 1 and items[0]['type'] == 'A' and items[0]['content'] == WORKER_IP):
            raise RuntimeError(f'{host} has an unexpected existing record; refusing to replace it')

    before = json.loads(command(KUBE + ['get', 'configmap', 'caddy-config', '-o', 'json']))
    original = before['data']['Caddyfile']
    snippet = (ROOT / 'deploy/caddy/darvazeh.Caddyfile').read_text(encoding='utf-8')
    snippet = snippet[snippet.index(START):].strip() + '\n'
    if START in original:
        prefix, remainder = original.split(START, 1)
        _, suffix = remainder.split(END, 1)
        updated = prefix + snippet.rstrip() + suffix
    else:
        if any(host in original for host in HOSTS):
            raise RuntimeError('Target host already appears outside the managed route block')
        updated = original.rstrip() + '\n\n' + snippet
    source_path = args.gateway_repo / 'manifests/apps.json'
    source = json.loads(source_path.read_text(encoding='utf-8'))
    source_config = next(item for item in source['items']
        if item['kind'] == 'ConfigMap' and item['metadata']['name'] == 'caddy-config')
    caddy_path = args.gateway_repo / 'manifests/Caddyfile'
    if source_config['data']['Caddyfile'] != original or caddy_path.read_text(encoding='utf-8') != original:
        raise RuntimeError('Gateway desired state differs from live configuration; reconcile before publishing')
    command(KUBE + ['exec', '-i', 'deployment/caddy', '--', 'caddy', 'validate', '--config', '-', '--adapter', 'caddyfile'], updated.encode())
    print('Combined Caddy configuration validated; existing routes preserved')
    if not args.apply:
        print('Plan: add two proxied worker DNS records, persist gateway routes, and reload Caddy')
        return

    backup = ROOT / '.local' / ('public-endpoints-' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
    backup.mkdir(parents=True, exist_ok=False)
    (backup / 'caddy-config.before.json').write_text(json.dumps(before), encoding='utf-8')
    (backup / 'dns.before.json').write_text(json.dumps(records), encoding='utf-8')
    (backup / 'apps.before.json').write_bytes(source_path.read_bytes())
    (backup / 'Caddyfile.before').write_bytes(caddy_path.read_bytes())
    changes = []
    for host in HOSTS:
        data = {'type': 'A', 'name': host, 'content': WORKER_IP, 'proxied': True, 'ttl': 1}
        if not records[host]:
            record = cloudflare(f'zones/{zone}/dns_records', 'POST', data)
        elif not records[host][0].get('proxied'):
            record = cloudflare(f'zones/{zone}/dns_records/{records[host][0]["id"]}', 'PATCH', {'proxied': True})
        else:
            record = records[host][0]
        changes.append({'name': host, 'id': record['id'], 'content': record['content'], 'proxied': record['proxied']})
        (backup / 'dns.after.json').write_text(json.dumps(changes), encoding='utf-8')
        print('DNS ready:', host)

    # Guard against concurrent changes after validation; patch with resourceVersion.
    patch = {'metadata': {'resourceVersion': before['metadata']['resourceVersion']}, 'data': {'Caddyfile': updated}}
    patch_path = backup / 'caddy.patch.json'
    patch_path.write_text(json.dumps(patch), encoding='utf-8')
    command(KUBE + ['patch', 'configmap', 'caddy-config', '--type=merge', '--patch-file', str(patch_path)])
    # The ConfigMap is mounted via subPath, so the old pod's file will not update.
    # Reload stdin and persist the ConfigMap for the next pod start.
    try:
        command(KUBE + ['exec', '-i', 'deployment/caddy', '--', 'caddy', 'reload', '--config', '-', '--adapter', 'caddyfile'], updated.encode())
    except RuntimeError:
        rollback = {'data': {'Caddyfile': original}}
        patch_path.write_text(json.dumps(rollback), encoding='utf-8')
        command(KUBE + ['patch', 'configmap', 'caddy-config', '--type=merge', '--patch-file', str(patch_path)])
        raise
    source_config['data']['Caddyfile'] = updated
    source_path.write_text(json.dumps(source, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    caddy_path.write_text(updated, encoding='utf-8')
    print('Gateway reloaded in place; desired-state files updated in', args.gateway_repo)
    print('Private recovery snapshots:', backup)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Avoid printing request objects, headers, response bodies or credentials.
        if isinstance(error, RuntimeError):
            print(str(error), file=__import__('sys').stderr)
        else:
            print('Public endpoint operation failed: ' + type(error).__name__, file=__import__('sys').stderr)
        raise SystemExit(1)

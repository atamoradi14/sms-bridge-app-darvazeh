#!/usr/bin/env python3
"""Load deployment configuration without sourcing .env as executable code."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SECRET_KEYS = ('FieldSense__AdminPassword', 'FieldSense__JwtKey', 'FieldSense__AcsConnectionString')
OPTIONAL_SECRET_KEYS = ('Billing__StripeSecretKey', 'Billing__StripeWebhookSecret', 'Billing__PaymentRelayApiKey', 'Google__ClientSecret')
OPTIONAL_CONFIG = {
    'Google__Enabled': 'false',
    'Google__ClientId': '',
    'Google__RedirectUri': 'https://api1.daryajan.com/api/auth/google/callback',
    'Google__PortalOrigin': 'https://darvazeh.daryajan.com',
    'Billing__StripeEnabled': 'false',
    'Billing__PortalOrigin': 'https://darvazeh.daryajan.com',
    'Billing__LegalName': 'Niliarya Limited',
    'Billing__TradingName': 'ND ORBIT',
    # Toman checkout: the Iran-hosted Ezpay relay (/broker routes); the receiving wallet is connected on the admin page.
    'Billing__EzpayEnabled': 'false',
    'Billing__PaymentRelayEndpoint': '',
}
CONFIG_KEYS = ('FieldSense__AdminEmail', 'FieldSense__AcsSenderAddress', 'FieldSense__AcsMock',
               'FieldSense__CorsOrigin', 'FieldSense__PublicApiBase')


def load(path):
    values = {}
    for number, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        line = re.sub(r'^export\s+', '', line)
        if '=' not in line:
            raise ValueError(f'Invalid .env entry at line {number}')
        key, value = line.split('=', 1)
        key, value = key.strip(), value.strip()
        if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', key) or key in values:
            raise ValueError(f'Invalid or duplicate .env key at line {number}')
        if value.startswith(('"', "'")):
            quote = value[0]
            end = value.rfind(quote)
            if end == 0 or (value[end + 1:].strip() and not value[end + 1:].lstrip().startswith('#')):
                raise ValueError(f'Invalid quoted value for {key}')
            value = value[1:end]
        else:
            value = re.split(r'\s+#', value, maxsplit=1)[0].rstrip()
        values[key] = value
    if not values.get('DOCKERHUB_USERNAME') and values.get('DOCKERHUB_USER'):
        values['DOCKERHUB_USERNAME'] = values['DOCKERHUB_USER']
    return values


def validate(values, build_only=False):
    required = ['DOCKERHUB_USERNAME', 'DOCKERHUB_TOKEN']
    if not build_only:
        # The API supports an unset public base and derives it from forwarded
        # headers. A hostname is only needed when configuring the public gateway.
        required += list(SECRET_KEYS) + [key for key in CONFIG_KEYS if key != 'FieldSense__PublicApiBase']
    errors = [f'Missing {key}' for key in required if not values.get(key)]
    if values.get('DOCKERHUB_USERNAME') and not re.fullmatch(r'[a-z0-9][a-z0-9_-]*', values['DOCKERHUB_USERNAME']):
        errors.append('Invalid DOCKERHUB_USERNAME')
    if build_only:
        return errors
    for key in SECRET_KEYS[:2]:
        value = values.get(key, '')
        if value and (value.lower().startswith(('dev-', 'replace-')) or 'changeme' in value.lower()):
            errors.append(f'{key} is a development placeholder')
    if values.get('FieldSense__JwtKey') and len(values['FieldSense__JwtKey'].encode()) < 32:
        errors.append('FieldSense__JwtKey must contain at least 32 UTF-8 bytes')
    if values.get('FieldSense__AcsMock', '').lower() != 'false':
        errors.append('Set FieldSense__AcsMock=false for production email login')
    connection = values.get('FieldSense__AcsConnectionString', '')
    if connection and not re.fullmatch(r'endpoint=https://[^;\s]+;accesskey=[^;\s]+;?', connection, re.I):
        errors.append('Invalid FieldSense__AcsConnectionString format')
    for key in ('FieldSense__AdminEmail', 'FieldSense__AcsSenderAddress'):
        if values.get(key) and not re.fullmatch(r'[^\s@]+@[^\s@]+\.[^\s@]+', values[key]):
            errors.append(f'Invalid {key}')
    origin = values.get('FieldSense__PublicApiBase', '').rstrip('/')
    if origin and not re.fullmatch(r'https://[a-zA-Z0-9.-]+(?::[0-9]+)?', origin):
        errors.append('FieldSense__PublicApiBase must be a public HTTPS origin')
    if origin and origin not in [s.strip().rstrip('/') for s in values.get('FieldSense__CorsOrigin', '').split(',')]:
        errors.append('FieldSense__CorsOrigin must include FieldSense__PublicApiBase')
    enabled = values.get('Billing__StripeEnabled', 'false').lower()
    if enabled not in ('true', 'false'):
        errors.append('Billing__StripeEnabled must be true or false')
    if enabled == 'true':
        if not re.fullmatch(r'(sk|rk)_(test|live)_[A-Za-z0-9]+', values.get('Billing__StripeSecretKey', '')):
            errors.append('Set a valid Billing__StripeSecretKey before enabling USD checkout')
        if not values.get('Billing__StripeWebhookSecret', '').startswith('whsec_'):
            errors.append('Set Billing__StripeWebhookSecret before enabling USD checkout')
    ezpay_enabled = values.get('Billing__EzpayEnabled', 'false').lower()
    if ezpay_enabled not in ('true', 'false'):
        errors.append('Billing__EzpayEnabled must be true or false')
    if ezpay_enabled == 'true':
        if not re.fullmatch(r'https://[A-Za-z0-9.-]+(?::[0-9]+)?', values.get('Billing__PaymentRelayEndpoint', '').rstrip('/')):
            errors.append('Set Billing__PaymentRelayEndpoint to the HTTPS Iran payment relay origin before enabling toman checkout')
        if len(values.get('Billing__PaymentRelayApiKey', '')) < 16:
            errors.append('Set Billing__PaymentRelayApiKey (the relay x-api-key) before enabling toman checkout')
    if not re.fullmatch(r'https://[A-Za-z0-9.-]+(?::[0-9]+)?', values.get('Billing__PortalOrigin', OPTIONAL_CONFIG['Billing__PortalOrigin']).rstrip('/')):
        errors.append('Billing__PortalOrigin must be a public HTTPS origin')
    google_enabled = values.get('Google__Enabled', 'false').lower()
    if google_enabled not in ('true', 'false'):
        errors.append('Google__Enabled must be true or false')
    if google_enabled == 'true':
        if not re.fullmatch(r'[A-Za-z0-9._-]+\.apps\.googleusercontent\.com', values.get('Google__ClientId', '')):
            errors.append('Set a valid Google__ClientId before enabling Google login')
        if not values.get('Google__ClientSecret') or values.get('Google__ClientSecret', '').lower().startswith(('replace', 'your_')):
            errors.append('Set Google__ClientSecret before enabling Google login')
    redirect = values.get('Google__RedirectUri', OPTIONAL_CONFIG['Google__RedirectUri'])
    portal = values.get('Google__PortalOrigin', OPTIONAL_CONFIG['Google__PortalOrigin']).rstrip('/')
    if not re.fullmatch(r'https://[A-Za-z0-9.-]+(?::[0-9]+)?/api/auth/google/callback', redirect):
        errors.append('Google__RedirectUri must be the public HTTPS Google callback URL')
    if not re.fullmatch(r'https://[A-Za-z0-9.-]+(?::[0-9]+)?', portal):
        errors.append('Google__PortalOrigin must be a public HTTPS origin')
    if google_enabled == 'true' and portal not in [v.strip().rstrip('/') for v in values.get('FieldSense__CorsOrigin', '').split(',')]:
        errors.append('FieldSense__CorsOrigin must include Google__PortalOrigin')
    return errors


def render(values, output):
    namespace = 'darvazeh'
    objects = {
        '20-secret.yaml': {'apiVersion': 'v1', 'kind': 'Secret',
            'metadata': {'name': 'fieldsense-secrets', 'namespace': namespace},
            'type': 'Opaque', 'stringData': {**{key: values[key] for key in SECRET_KEYS},
                **{key: values.get(key, '') for key in OPTIONAL_SECRET_KEYS}}},
        '21-registry-secret.yaml': {'apiVersion': 'v1', 'kind': 'Secret',
            'metadata': {'name': 'darvazeh-registry', 'namespace': namespace},
            'type': 'kubernetes.io/dockerconfigjson', 'stringData': {'.dockerconfigjson': json.dumps({
                'auths': {'https://index.docker.io/v1/': {
                    'username': values['DOCKERHUB_USERNAME'], 'password': values['DOCKERHUB_TOKEN'],
                    'auth': base64.b64encode((values['DOCKERHUB_USERNAME'] + ':' + values['DOCKERHUB_TOKEN']).encode()).decode()}}})}},
        '10-configmap.yaml': {'apiVersion': 'v1', 'kind': 'List', 'items': [
            {'apiVersion': 'v1', 'kind': 'ConfigMap', 'metadata': {'name': 'fieldsense-config', 'namespace': namespace},
             'data': {**{key: values.get(key, '') for key in CONFIG_KEYS},
                      **{key: values.get(key, default) for key, default in OPTIONAL_CONFIG.items()},
                      'ConnectionStrings__Default': 'Data Source=/data/fieldsense.db',
                      'ASPNETCORE_URLS': 'http://0.0.0.0:8080'}},
            {'apiVersion': 'v1', 'kind': 'ConfigMap', 'metadata': {'name': 'fieldsense-web-config', 'namespace': namespace},
             'data': {
                 'config.js': 'window.__FIELDSENSE_API_BASE__ = ' + (
                     json.dumps(values['FieldSense__PublicApiBase'].rstrip('/'))
                     if values.get('FieldSense__PublicApiBase') else 'window.location.origin') + ';\n',
                 'mobile-config.json': json.dumps({
                     'apiBase': values.get('FieldSense__PublicApiBase', '').rstrip('/'),
                     'brand': 'دروازه',
                 }, ensure_ascii=False),
             }}]},
    }
    for name, document in objects.items():
        path = output / name
        path.write_text(json.dumps(document), encoding='utf-8')
        path.chmod(0o600)
    config_hash = hashlib.sha256(json.dumps(values, sort_keys=True).encode()).hexdigest()
    for name in ('40-api-deployment.yaml', '50-web-deployment.yaml'):
        path = output / name
        if path.exists():
            path.write_text(path.read_text(encoding='utf-8').replace('DEPLOYMENT_CONFIG_HASH', config_hash), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['values', 'check', 'render', 'run'])
    parser.add_argument('--env-file', type=Path, default=ROOT / '.env')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--build-only', action='store_true')
    args, command = parser.parse_known_args()
    try:
        values = load(args.env_file)
        if args.mode == 'values':
            print(json.dumps(values))  # Consumed in memory by the PowerShell loader.
            return 0
        if args.mode == 'run':
            command = command[1:] if command[:1] == ['--'] else command
            return subprocess.call(command, env={**os.environ, **values, 'DARVAZEH_ENV_LOADED': '1'})
        errors = validate(values, args.build_only)
        if errors:
            print('\n'.join(errors), file=sys.stderr)
            return 1
        if args.mode == 'render':
            if not args.output or not args.output.is_dir():
                raise ValueError('Output directory must exist')
            render(values, args.output)
        print('Deployment .env validation passed (secret values hidden)')
        return 0
    except (OSError, ValueError):
        print('Could not read deployment .env or generate private manifests; check the file and its syntax', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())

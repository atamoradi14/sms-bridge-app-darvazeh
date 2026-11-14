"""VPN product regression tests: real API + SQLite against a stateful local 3x-ui fixture and the Stripe fixture.
Never contacts a real panel, sends no email and makes no payment."""
from contextlib import closing
from datetime import datetime, timedelta, timezone
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('verify_billing', ROOT / 'scripts/verify-billing.py')
billing = importlib.util.module_from_spec(spec); spec.loader.exec_module(billing)

TOKEN = 'xui-fixture-token-0123456789'
BASE_PATH = '/secretPanelPath'
panel = {'clients': {}, 'online': set(), 'ips': {}, 'down': False, 'calls': [], 'next_id': 1}
lock = threading.Lock()


class XuiFixture(BaseHTTPRequestHandler):
    """Implements the 3x-ui v3.11 client endpoints Darvazeh uses, including read/write model differences."""
    def log_message(self, *args): pass

    def reply(self, obj=None, success=True, msg=''):
        body = json.dumps({'success': success, 'msg': msg, 'obj': obj}).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(body)

    def handle_any(self, method):
        if panel['down']:
            self.send_response(503); self.end_headers(); return
        if self.headers.get('Authorization') != 'Bearer ' + TOKEN:
            self.send_response(302); self.send_header('Location', '/login'); self.end_headers(); return
        path = urllib.parse.urlsplit(self.path).path
        assert path.startswith(BASE_PATH + '/panel/api/'), path
        route = path[len(BASE_PATH + '/panel/api/'):]
        length = int(self.headers.get('Content-Length') or 0)
        body = json.loads(self.rfile.read(length) or b'null') if length else None
        with lock:
            panel['calls'].append((method, route))
            parts = [urllib.parse.unquote(p) for p in route.split('/')]
            c = panel['clients']
            if route == 'inbounds/list':
                return self.reply([{'id': i, 'remark': r, 'protocol': 'vless', 'port': p, 'enable': True}
                                   for i, r, p in [(1, 'XHTTP TLS', 10080), (2, 'REALITY vision', 8443), (3, 'REALITY xhttp', 9443)]])
            if route == 'clients/onlines':
                return self.reply(sorted(panel['online']))
            if parts[:2] == ['clients', 'get']:
                x = c.get(parts[2])
                if not x: return self.reply(None, False, 'record not found')
                record = {k: v for k, v in x['client'].items() if k != 'id'}
                record.update({'id': x['db_id'], 'uuid': x['client']['id'], 'allowedIPs': '', 'createdAt': 1, 'updatedAt': 2})
                return self.reply({'client': record, 'inboundIds': x['inbounds']})
            if route == 'clients/add':
                cl = body['client']; assert cl['email'] not in c
                assert set(body['inboundIds']) <= {1, 2, 3}
                assert cl['totalGB'] > 0, 'never create an unlimited client'
                c[cl['email']] = {'client': dict(cl), 'inbounds': list(body['inboundIds']), 'up': 0, 'down': 0, 'db_id': panel['next_id']}
                panel['next_id'] += 1
                return self.reply(None)
            if len(parts) == 3 and parts[0] == 'clients' and parts[2] == 'attach':
                c[parts[1]]['inbounds'] = sorted(set(c[parts[1]]['inbounds']) | set(body['inboundIds'])); return self.reply(None)
            if parts[:2] == ['clients', 'update']:
                x = c.get(parts[2])
                if not x: return self.reply(None, False, 'record not found')
                # Write model: VLESS UUID string in id, allowedIPs as a list.
                assert isinstance(body['id'], str) and len(body['id']) == 36, body.get('id')
                assert isinstance(body['allowedIPs'], list)
                assert body['totalGB'] > 0, 'never write an unlimited quota'
                x['client'] = {k: v for k, v in body.items() if k not in ('uuid', 'createdAt', 'updatedAt')}
                self.enforce(x)
                return self.reply(None)
            if parts[:2] == ['clients', 'del']:
                c.pop(parts[2], None); return self.reply(None)
            if parts[:2] == ['clients', 'resetTraffic']:
                x = c[parts[2]]; x['up'] = x['down'] = 0; x['client']['enable'] = True; return self.reply(None)
            if parts[:2] == ['clients', 'traffic']:
                x = c.get(parts[2])
                if not x: return self.reply(None, False, 'record not found')
                return self.reply({'up': x['up'], 'down': x['down'], 'enable': x['client']['enable'], 'total': x['client']['totalGB'],
                                   'expiryTime': x['client']['expiryTime'], 'lastOnline': 0, 'email': parts[2]})
            if parts[:2] == ['clients', 'ips']:
                return self.reply([f'{ip} (2026-11-14 10:00:00)' for ip in panel['ips'].get(parts[2], [])] or 'No IP Record')
        raise AssertionError('unexpected route ' + route)

    @staticmethod
    def enforce(x):
        if x['up'] + x['down'] >= x['client']['totalGB']: x['client']['enable'] = False

    def do_GET(self): self.handle_any('GET')
    def do_POST(self): self.handle_any('POST')


def utcnow(): return datetime.now(timezone.utc).replace(tzinfo=None)


def use(email, down, up=0):
    with lock:
        x = panel['clients'][email]; x['down'] = down; x['up'] = up; XuiFixture.enforce(x)


def main():
    stripe = ThreadingHTTPServer(('127.0.0.1', 0), billing.StripeFixture)
    xui = ThreadingHTTPServer(('127.0.0.1', 0), XuiFixture)
    for s in (stripe, xui): threading.Thread(target=s.serve_forever, daemon=True).start()
    sub_base = f'http://127.0.0.1:{xui.server_port}/subPath/'
    with tempfile.TemporaryDirectory(prefix='darvazeh-vpn-') as temporary:
        temporary = Path(temporary); database = temporary / 'vpn.db'
        with socket.socket() as sock: sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': base,
               'FieldSense__AcsMock': 'true', 'FieldSense__AcsConnectionString': '', 'FieldSense__AdminEmail': 'admin@ndorbit.co.nz',
               'FieldSense__AdminPassword': 'vpn-admin-fixture', 'FieldSense__JwtKey': 'disposable-vpn-test-key-' + 'x' * 40,
               'ConnectionStrings__Default': 'Data Source=' + str(database),
               'Billing__StripeEnabled': 'true', 'Billing__StripeSecretKey': 'sk_test_fixture', 'Billing__StripeWebhookSecret': 'whsec_fixture',
               'Billing__StripeApiBase': f'http://127.0.0.1:{stripe.server_port}', 'Billing__PortalOrigin': 'https://darvazeh.daryajan.com',
               'Vpn__Servers__0__Code': 'tehran', 'Vpn__Servers__0__Name': 'Tehran', 'Vpn__Servers__0__NameFa': 'تهران',
               'Vpn__Servers__0__PanelBaseUrl': f'http://127.0.0.1:{xui.server_port}{BASE_PATH}/',
               'Vpn__Servers__0__SubscriptionBaseUrl': sub_base, 'Vpn__Servers__0__ApiToken': TOKEN, 'Vpn__Servers__0__InboundIds': '1,2,3'}
        log = (temporary / 'server.log').open('wb'); process = None

        def call(path, body=None, token=None, method=None, raw=None, headers=None):
            h = {'Content-Type': 'application/json', **(headers or {})}
            if token: h['Authorization'] = 'Bearer ' + token
            data = raw if raw is not None else json.dumps(body).encode() if body is not None else None
            req = urllib.request.Request(base + path, headers=h, data=data, method=method or ('POST' if data is not None else 'GET'))
            try:
                with urllib.request.urlopen(req, timeout=60) as r: return r.status, json.load(r)
            except urllib.error.HTTPError as e: return e.code, json.loads(e.read() or '{}')

        def start():
            nonlocal process
            process = subprocess.Popen(['dotnet', str(ROOT / 'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],
                                       cwd=ROOT / 'server/FieldSense.Api', env=env, stdout=log, stderr=log)
            for _ in range(120):
                if process.poll() is not None: raise AssertionError('API startup failed')
                try:
                    if call('/health')[0] == 200: return
                except OSError: pass
                time.sleep(.15)
            raise AssertionError('API startup timed out')

        def login(email):
            assert call('/api/auth/request-otp', {'email': email})[0] == 200
            status, data = call('/api/auth/verify-otp', {'email': email, 'code': '000000', 'adminPassword': 'vpn-admin-fixture'})
            assert status == 200, data
            return data['token'], data['user']['id']

        def webhook(identity, type='checkout.session.completed'):
            event = {'id': 'evt_' + uuid.uuid4().hex, 'livemode': False, 'type': type, 'data': {'object': {'id': identity}}}
            raw = json.dumps(event, separators=(',', ':')).encode(); stamp = int(time.time())
            digest = hmac.new(b'whsec_fixture', str(stamp).encode() + b'.' + raw, hashlib.sha256).hexdigest()
            return call('/api/billing/stripe/webhook', raw=raw, headers={'Stripe-Signature': f't={stamp},v1={digest}'})

        def buy(token, plan_id):
            status, data = call('/api/billing/checkout', {'planId': plan_id, 'currency': 'USD', 'requestKey': str(uuid.uuid4())}, token)
            assert status == 200, (status, data)
            identity = next(k for k, v in billing.sessions.items() if v['metadata']['order_id'] == data['id'])
            billing.sessions[identity]['payment_status'] = 'paid'; billing.sessions[identity]['status'] = 'complete'
            assert webhook(identity)[0] == 200
            return data, identity

        def home(token, refresh=True):
            if refresh: time.sleep(3.1)  # The member refresh is throttled to one panel read every 3 seconds.
            return call('/api/vpn' + ('?refresh=true' if refresh else ''), token=token)[1]
        def client(email): return panel['clients'][email]['client']
        def ms(dt): return int(dt.replace(tzinfo=timezone.utc).timestamp() * 1000)

        try:
            start()
            admin, _ = login('admin@ndorbit.co.nz'); user, user_id = login('vpn.buyer@gmail.com'); other, _ = login('second@example.com')
            h = home(user)
            assert h['account'] is None and h['trialAvailable'] and h['policy']['trialBytes'] == 10 * 1024 * 1024
            assert h['policy']['monthlyBytes'] == 5 * 1024 ** 3 and h['policy']['ipLimit'] == 1 and h['policy']['trialDays'] == 1
            assert h['plan']['priceUsdCents'] == 400 and h['plan']['priceToman'] == 1000000 and h['servers'][0]['code'] == 'tehran'
            plan_id = h['plan']['id']
            assert all(p['code'] != 'vpn-iran' for p in call('/api/billing/catalog', token=user)[1]['plans'])
            assert all(p['code'] != 'vpn-iran' for p in call('/api/admin/plans', token=admin)[1])
            assert call('/api/billing/checkout', {'planId': plan_id, 'currency': 'USD', 'requestKey': str(uuid.uuid4())}, user)[1]['code'] == 'vpn_request_required'
            print('PASS: VPN catalog, 10 MB trial / 5 GB / USD 4 / 1,000,000 toman defaults, hidden from SMS plans, pay-after-request guard')

            status, acc = call('/api/vpn/request', {'serverCode': 'tehran'}, user); assert status == 200, acc
            email = call('/api/admin/vpn/accounts', token=admin)[1][0]['clientEmail']
            c = client(email)
            assert acc['status'] == 'trial' and acc['subscriptionUrl'] == sub_base + c['subId']
            assert c['totalGB'] == 10 * 1024 * 1024 and c['limitIp'] == 1 and c['enable']
            assert abs(c['expiryTime'] / 1000 - (time.time() + 86400)) < 120, 'trial lasts one day'
            assert datetime.fromisoformat(acc['periodEnd'].rstrip('Z')) > utcnow() + timedelta(hours=23)
            assert c['flow'] == 'xtls-rprx-vision' and panel['clients'][email]['inbounds'] == [1, 2, 3]
            again = call('/api/vpn/request', {'serverCode': 'tehran'}, user)[1]; assert again['id'] == acc['id'] and len(panel['clients']) == 1
            use(email, 6 * 1024 * 1024, 5 * 1024 * 1024)
            a = home(user)['account']; assert a['status'] == 'trial_used' and a['usedDown'] == 6 * 1024 * 1024 and a['remainingBytes'] == 0
            print('PASS: one-click 1-day trial provisioning on 3 inbounds, one-user limit, idempotent request, usage and trial depletion')

            sub_id = c['subId']
            order, session = buy(user, plan_id)
            a = home(user)['account']; c = client(email)
            assert a['status'] == 'active' and a['limitBytes'] == 5 * 1024 ** 3 and a['usedTotal'] == 0
            assert c['subId'] == sub_id and c['enable'] and c['totalGB'] == 5 * 1024 ** 3
            paid_until = datetime.fromisoformat(a['paidUntil'].rstrip('Z'))
            assert abs((paid_until - utcnow()).total_seconds() - 30 * 86400) < 120 and abs(c['expiryTime'] - ms(paid_until)) < 1000
            assert call('/api/user/me', token=user)[1]['subscription']['planCode'] == 'trial'
            orders = home(user)['orders']; assert orders[0]['status'] == 'paid_active'
            use(email, 100 * 1024 * 1024)
            buy(user, plan_id)
            a2 = home(user)['account']
            assert abs((datetime.fromisoformat(a2['paidUntil'].rstrip('Z')) - paid_until).total_seconds() - 30 * 86400) < 2
            assert a2['usedDown'] == 100 * 1024 * 1024 and a2['periodEnd'] == a['periodEnd']
            print('PASS: USD 4 payment activates the same config (no re-request), early renewal queues 30 more days without resetting usage, SMS plan untouched')

            assert call('/api/vpn/suspension', {'suspended': True}, user)[1]['status'] == 'suspended' and not client(email)['enable']
            assert call('/api/vpn/suspension', {'suspended': False}, user)[1]['status'] == 'active' and client(email)['enable']
            vid = a['id']
            assert call(f'/api/admin/vpn/accounts/{vid}/suspension', {'suspended': True, 'reason': 'abuse'}, user)[0] == 403
            assert call(f'/api/admin/vpn/accounts/{vid}/suspension', {'suspended': True, 'reason': 'abuse'}, admin)[1]['suspendReason'] == 'abuse'
            assert call('/api/vpn/suspension', {'suspended': False}, user)[1]['code'] == 'vpn_admin_suspended' and not client(email)['enable']
            assert call(f'/api/admin/vpn/accounts/{vid}/suspension', {'suspended': False}, admin)[1]['status'] == 'active'
            old_uuid = client(email)['id']
            r = call('/api/vpn/rotate', {}, user)[1]
            assert client(email)['subId'] != sub_id and client(email)['id'] != old_uuid and r['subscriptionUrl'].endswith(client(email)['subId'])
            print('PASS: member pause/resume, admin suspension overrides member, link rotation revokes old UUID and subscription')

            panel['online'].add(email); panel['ips'][email] = ['5.6.7.8', '9.9.9.9']
            s = call(f'/api/admin/vpn/accounts/{vid}/sync', {}, admin)[1]
            assert s['online'] and s['activeIps'] == 2 and s['ipLimit'] == 1
            ov = call('/api/admin/vpn', token=admin)[1]; assert ov['stats']['online'] == 1 and ov['stats']['overIpLimit'] == 1 and ov['stats']['revenueUsdCents'] == 800
            policy = {'enabled': True, 'trialBytes': 20 * 1024 * 1024, 'trialDays': 0, 'monthlyBytes': 6 * 1024 ** 3, 'periodDays': 30, 'ipLimit': 2, 'priceUsdCents': 500, 'priceToman': 450000}
            assert call('/api/admin/vpn/policy', policy, user, 'PUT')[0] == 403
            assert call('/api/admin/vpn/policy', {**policy, 'monthlyBytes': 0}, admin, 'PUT')[0] == 400
            assert call('/api/admin/vpn/policy', policy, admin, 'PUT')[0] == 200
            assert home(user)['plan']['priceUsdCents'] == 500 and home(user)['plan']['priceToman'] == 450000
            call(f'/api/admin/vpn/accounts/{vid}/sync', {}, admin); assert client(email)['limitIp'] == 2
            assert call(f'/api/admin/vpn/accounts/{vid}/limit', {'monthlyBytes': 0, 'currentBytes': None}, admin)[0] == 400
            l = call(f'/api/admin/vpn/accounts/{vid}/limit', {'monthlyBytes': 8 * 1024 ** 3, 'currentBytes': 7 * 1024 ** 3}, admin)[1]
            assert l['limitBytes'] == 7 * 1024 ** 3 and client(email)['totalGB'] == 7 * 1024 ** 3
            assert call(f'/api/admin/vpn/accounts/{vid}/reset-traffic', {}, admin)[1]['usedTotal'] == 0 and panel['clients'][email]['down'] == 0
            e = call(f'/api/admin/vpn/accounts/{vid}/extend', {'days': -10}, admin)[1]
            assert abs((datetime.fromisoformat(e['paidUntil'].rstrip('Z')) - datetime.fromisoformat(a2['paidUntil'].rstrip('Z'))).total_seconds() + 10 * 86400) < 2
            print('PASS: online users and over-limit detection, admin pricing/data/IP policy, per-member data override, traffic reset, period adjustment')

            # Monthly roll-over: end the data period in the past, restart; the worker starts a new period with a fresh quota.
            use(email, 3 * 1024 ** 3)
            home(user)
            with closing(sqlite3.connect(database)) as db, db:
                db.execute("UPDATE VpnClients SET PeriodEnd=? WHERE Id=?", ((utcnow() - timedelta(minutes=1)).isoformat(sep=' '), vid.upper()))
            process.terminate(); process.wait(timeout=10); start()
            for _ in range(60):
                a3 = call('/api/vpn', token=user)[1]['account']
                if not a3['pending'] and a3['limitBytes'] == 8 * 1024 ** 3: break
                time.sleep(.5)
            assert a3['usedTotal'] == 0 and a3['limitBytes'] == 8 * 1024 ** 3 and a3['lifetimeDown'] >= 3 * 1024 ** 3, a3
            assert datetime.fromisoformat(a3['periodEnd'].rstrip('Z')) > utcnow() + timedelta(days=29)
            print('PASS: restart-safe state and automatic monthly period roll-over with fresh quota and lifetime totals')

            # Refund removes the order's days.
            before = datetime.fromisoformat(a3['paidUntil'].rstrip('Z'))
            payment = billing.sessions[session]['payment_intent']; charge = 'ch_' + uuid.uuid4().hex
            billing.charges[charge] = {'id': charge, 'payment_intent': payment, 'amount': 400, 'amount_refunded': 400, 'currency': 'usd', 'livemode': False}
            assert webhook(charge, 'charge.refunded')[0] == 200
            after = datetime.fromisoformat(home(user)['account']['paidUntil'].rstrip('Z'))
            assert abs((before - after).total_seconds() - 30 * 86400) < 2
            print('PASS: refunds shorten paid VPN time')

            # Panel outage: the request is recorded and retried, never lost.
            panel['down'] = True
            status, pending = call('/api/vpn/request', {'serverCode': 'tehran'}, other)
            assert status == 200 and pending['pending'] and pending['lastError']
            panel['down'] = False
            synced = call(f"/api/admin/vpn/accounts/{pending['id']}/sync", {}, admin)[1]
            assert not synced['pending'] and synced['status'] == 'trial' and len(panel['clients']) == 2
            assert client(next(e for e in panel['clients'] if e != email))['totalGB'] == 20 * 1024 * 1024
            print('PASS: panel outage is stored and retried; new trial uses the updated 20 MB admin default')

            # Revoke; the same email identity (gmail dot/plus variant) gets no second trial and is never created unlimited.
            assert call(f'/api/admin/vpn/accounts/{vid}', token=admin, method='DELETE')[1]['revoked'] and email not in panel['clients']
            assert home(user)['account'] is None and not home(user)['trialAvailable']
            again = call('/api/vpn/request', {'serverCode': 'tehran'}, user)[1]
            new_email = next(e for e in panel['clients'] if e not in (email,) and client(e)['comment'].endswith(user_id.replace('-', '').lower()))
            assert again['status'] == 'awaiting_payment' and not client(new_email)['enable'] and client(new_email)['totalGB'] == 1
            assert call('/api/admin/vpn/accounts?includeRevoked=true', token=admin)[1][-1]['account']['status'] in ('revoked', 'trial', 'awaiting_payment')
            assert call('/api/admin/vpn/servers', token=admin)[1][0]['reachable']
            env_bad = call('/api/admin/vpn', token=user)[0]; assert env_bad == 403
            with closing(sqlite3.connect(database)) as db: assert db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
            print('PASS: revoke deletes the panel client, no repeat trial, disabled clients never get unlimited quota, admin-only routes, DB integrity')
        finally:
            if process and process.poll() is None: process.terminate(); process.wait(timeout=10)
            log.close()
            for s in (stripe, xui): s.shutdown(); s.server_close()


if __name__ == '__main__': main()

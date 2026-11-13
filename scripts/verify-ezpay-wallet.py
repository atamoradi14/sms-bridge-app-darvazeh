"""Real HTTP/SQLite regression test for toman payments through the Iran Ezpay relay into Darvazeh's default wallet.

A local fixture copies the relay's /broker contract (online-payment/persian-payement-project/broker.js): x-api-key auth,
resultCode 0 on success, HTTP 422 {error, resultCode} on Ezpay refusals, Ezpay code 21 on /link reported as requestUid null.
No real Ezpay call is made. Run after `dotnet build server/FieldSense.Api`.
"""
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
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
RELAY_KEY = 'relay-fixture-key-' + 'r' * 24
PHONE_NEW, PHONE_LINKED, PHONE_DEFAULT = '09120000001', '09120000002', '09120000003'
CONSENT_CODE = 4321
state = {'linked': {PHONE_LINKED}, 'verified': set(), 'consents': {}, 'links': {}, 'calls': []}
lock = threading.Lock()


class Relay(BaseHTTPRequestHandler):
    def log_message(self, *args): pass

    def respond(self, body, status=200):
        self.send_response(status); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps(body).encode())

    def refuse(self, code): self.respond({'error': 'Ezpay rejected the broker request.', 'resultCode': code}, 422)

    def body(self):
        length = int(self.headers.get('Content-Length') or 0)
        return json.loads(self.rfile.read(length)) if length else {}

    def route(self, method):
        if self.headers.get('x-api-key') != RELAY_KEY: return self.respond({'error': 'Unauthorized'}, 401)
        url = urllib.parse.urlparse(self.path); path, query = url.path, urllib.parse.parse_qs(url.query)
        with lock: state['calls'].append((method, path))
        if method == 'POST' and path == '/broker/link':
            phone = self.body()['phoneNumber']
            with lock:
                if phone in state['linked']: return self.respond({'resultCode': 0, 'requestUid': None, 'alreadyLinked': True})
                uid = 'req-' + uuid.uuid4().hex[:10]; state['consents'][uid] = phone
            return self.respond({'resultCode': 0, 'requestUid': uid})
        if m := re.fullmatch(r'/broker/link/([\w-]+)/resend', path):
            return self.respond({'resultCode': 0}) if m[1] in state['consents'] else self.refuse(5)
        if m := re.fullmatch(r'/broker/link/([\w-]+)', path):
            if self.body().get('verifyCode') != CONSENT_CODE: return self.refuse(5)
            with lock: state['linked'].add(state['consents'].pop(m[1]))
            return self.respond({'resultCode': 0})
        if m := re.fullmatch(r'/broker/wallet/(09\d{9})/status', path):
            if m[1] not in state['linked']: return self.respond({'resultCode': 0, 'access': 'NO_ACCESS', 'group': None, 'identityVerified': None})
            verified = m[1] in state['verified']
            return self.respond({'resultCode': 0, 'access': 'ACTIVE', 'group': 'LEVEL2' if verified else 'DEFAULT', 'identityVerified': verified})
        if m := re.fullmatch(r'/broker/wallet/(09\d{9})/balances', path):
            return self.respond({'resultCode': 0, 'balances': [{'accountType': 'CASH', 'balance': 1500000, 'availableBalance': 1200000}]})
        if m := re.fullmatch(r'/broker/wallet/(09\d{9})/history', path):
            return self.respond({'resultCode': 0, 'totalCount': 1, 'pageItems': [{'amount': 5000000, 'description': 'payment', 'fromOrTo': None,
                                 'transactionNumber': 't-1', 'utcTimestamp': 1763000000000, 'balance': 1500000}]})
        if method == 'POST' and path == '/broker/payments':
            b = self.body()
            assert re.fullmatch(r'[a-zA-Z0-9_-]{1,100}', b['factorNumber']) and b['subject'].strip()
            if b['phoneNumber'] not in state['linked']: return self.refuse(103)
            uid = 'u' + uuid.uuid4().hex[:12]
            with lock: state['links'][uid] = {**b, 'status': 'PENDING'}
            return self.respond({'resultCode': 0, 'uniqueId': uid, 'paymentLink': 'https://wallet.ezpay.ir/pay/' + uid})
        if m := re.fullmatch(r'/broker/payments/([\w-]+)', path):
            link = state['links'].get(m[1])
            ok = link and query['phoneNumber'] == [link['phoneNumber']] and query['factorNumber'] == [link['factorNumber']] and query['amount'] == [str(link['amount'])]
            if not ok: return self.respond({'error': 'Ezpay payment link identity or amount mismatch.'}, 502)
            paid = {'transactionid': 'tx-' + m[1], 'reference': 'ref'} if link['status'] == 'SUCCESS' else {}
            return self.respond({'resultCode': 0, 'status': link['status'], 'requestid': m[1], 'amount': link['amount'], **paid})
        self.respond({'error': 'not found'}, 404)

    def do_GET(self): self.route('GET')
    def do_POST(self): self.route('POST')
    def do_PUT(self): self.route('PUT')


def main():
    relay = ThreadingHTTPServer(('127.0.0.1', 0), Relay)
    threading.Thread(target=relay.serve_forever, daemon=True).start()
    with tempfile.TemporaryDirectory(prefix='darvazeh-ezpay-') as temporary:
        temporary = Path(temporary); database = temporary / 'billing.db'
        with socket.socket() as sock: sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': base,
               'FieldSense__AcsMock': 'true', 'FieldSense__AcsConnectionString': '', 'FieldSense__AdminEmail': 'admin@ndorbit.co.nz',
               'FieldSense__AdminPassword': 'billing-admin-fixture', 'FieldSense__JwtKey': 'disposable-billing-test-key-' + 'x' * 40,
               'ConnectionStrings__Default': 'Data Source=' + str(database), 'Billing__StripeEnabled': 'false',
               'Billing__PortalOrigin': 'https://darvazeh.daryajan.com', 'Billing__EzpayEnabled': 'true',
               'Billing__PaymentRelayEndpoint': f'http://127.0.0.1:{relay.server_port}', 'Billing__PaymentRelayApiKey': RELAY_KEY}
        log = (temporary / 'server.log').open('wb'); process = None

        def call(path, body=None, token=None, method=None):
            headers = {'Content-Type': 'application/json'}
            if token: headers['Authorization'] = 'Bearer ' + token
            req = urllib.request.Request(base + path, headers=headers, data=json.dumps(body).encode() if body is not None else None,
                                         method=method or ('POST' if body is not None else 'GET'))
            try:
                with urllib.request.urlopen(req, timeout=30) as r: return r.status, json.loads(r.read() or b'{}')
            except urllib.error.HTTPError as e: return e.code, json.loads(e.read() or b'{}')

        def login(email):
            assert call('/api/auth/request-otp', {'email': email})[0] == 200
            status, data = call('/api/auth/verify-otp', {'email': email, 'code': '000000', 'adminPassword': 'billing-admin-fixture'})
            assert status == 200, (status, data)
            return data['token'], data['user']['id']

        def ok(result, expected=200):
            assert result[0] == expected, result
            return result[1]

        def order_status(token, order):
            return next(o for o in call('/api/billing/orders', token=token)[1] if o['id'] == order)['status']

        def pay(uid):
            with lock: state['links'][uid]['status'] = 'SUCCESS'

        def start(environment):
            started = subprocess.Popen(['dotnet', str(ROOT / 'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],
                                       cwd=ROOT / 'server/FieldSense.Api', env=environment, stdout=log, stderr=log)
            for _ in range(150):
                if started.poll() is not None: raise AssertionError('API startup failed')
                try:
                    if call('/health')[0] == 200: return started
                except OSError: pass
                time.sleep(.15)
            raise AssertionError('API did not become healthy')

        def stop(running):
            running.terminate(); running.wait(10)

        try:
            process = start(env)
            admin, admin_id = login('admin@ndorbit.co.nz'); user, _ = login('buyer@example.com'); other, _ = login('other@example.com')
            plan = ok(call('/api/admin/plans', {'code': 'toman-silver', 'name': 'Silver', 'durationDays': 30, 'messageLimit': 200, 'emailForwardLimit': 10,
                                                'sendEnabled': True, 'receiveEnabled': True, 'priceUsdCents': None, 'priceToman': 500000, 'isPurchasable': True}, admin))

            # Only the owner manages the wallet; without a wallet, toman checkout is not offered.
            assert call('/api/admin/wallet', token=user)[0] == 403
            view = ok(call('/api/admin/wallet', token=admin))
            assert view['connection']['status'] == 'Disconnected' and view['relayConfigured'], view
            assert not ok(call('/api/billing/catalog', token=user))['tomanAvailable']
            assert call('/api/billing/checkout', {'planId': plan['id'], 'currency': 'IRT', 'requestKey': str(uuid.uuid4())}, user)[0] == 503

            # Connect a new wallet: Ezpay sends a consent code; a wrong code is refused, the right one connects.
            assert call('/api/admin/wallet/connection', {'phoneNumber': '۰۹۱۲۰۰۰۰۰۰x'}, admin)[0] == 400
            view = ok(call('/api/admin/wallet/connection', {'phoneNumber': '+989120000001'}, admin))
            challenge = view['connection']['challengeId']
            assert view['connection']['status'] == 'PendingConsent' and view['connection']['pendingPhoneNumber'] == PHONE_NEW and challenge, view
            assert call('/api/admin/wallet/connection', {'phoneNumber': PHONE_NEW}, admin)[0] == 429  # one code per minute
            refused = call(f'/api/admin/wallet/consent/{challenge}', {'verifyCode': '1111'}, admin, 'PUT')
            assert refused[0] == 409 and refused[1]['resultCode'] == 5, refused
            view = ok(call(f'/api/admin/wallet/consent/{challenge}', {'verifyCode': str(CONSENT_CODE)}, admin, 'PUT'))
            assert view['connection']['status'] == 'Connected' and view['connection']['phoneNumber'] == PHONE_NEW and not view['purchaseReady'], view

            # Merchant services need Ezpay identity verification first.
            assert ok(call('/api/admin/wallet/merchant', {'enabled': True}, admin, 'PUT'), 409)['code'] == 'wallet_not_verified'
            with lock: state['verified'].add(PHONE_NEW)
            view = ok(call('/api/admin/wallet/merchant', {'enabled': True}, admin, 'PUT'))
            assert view['purchaseReady'] and view['providerStatus']['identityVerified'], view
            assert ok(call('/api/admin/wallet/balances', token=admin))[0]['availableBalance'] == '1200000'
            assert ok(call('/api/admin/wallet/history', token=admin))['total'] == 1

            # Link creation: a payment link into the default wallet for the plan's toman price in rial.
            assert ok(call('/api/billing/catalog', token=user))['tomanAvailable']
            key = str(uuid.uuid4())
            order = ok(call('/api/billing/checkout', {'planId': plan['id'], 'currency': 'IRT', 'requestKey': key}, user))
            assert order['status'] == 'checkout_open' and order['checkoutUrl'].startswith('https://wallet.ezpay.ir/pay/'), order
            uid = order['checkoutUrl'].rsplit('/', 1)[1]; link = state['links'][uid]
            assert (link['phoneNumber'], link['amount'], link['factorNumber']) == (PHONE_NEW, 5_000_000, 'darvazeh-' + uuid.UUID(order['id']).hex), link
            print('link created:', order['checkoutUrl'])
            count = len(state['links'])
            assert ok(call('/api/billing/checkout', {'planId': plan['id'], 'currency': 'IRT', 'requestKey': key}, user))['id'] == order['id']
            assert len(state['links']) == count  # same request key: no second link
            assert call('/api/billing/checkout', {'planId': plan['id'], 'currency': 'EUR', 'requestKey': str(uuid.uuid4())}, user)[0] == 400

            # Pending stays open, another user cannot reconcile it, paid activates the period once.
            assert ok(call(f'/api/billing/orders/{order["id"]}/reconcile', {}, user))['status'] == 'checkout_open'
            assert call(f'/api/billing/orders/{order["id"]}/reconcile', {}, other)[0] == 404
            pay(uid)
            assert ok(call(f'/api/billing/orders/{order["id"]}/reconcile', {}, user))['status'].startswith('paid_')
            assert ok(call('/api/user/me', token=user))['subscription']['active']
            with closing(sqlite3.connect(database)) as db:
                assert db.execute('SELECT Provider, Currency, AmountMinor, ReceivingPhone, ProviderPaymentId FROM PaymentOrders WHERE Id=?',
                                  (order['id'].upper(),)).fetchone() == ('ezpay', 'IRT', 500000, PHONE_NEW, 'ezpay:tx-' + uid)

            # An open order on this wallet, then the wallet changes: its status is still checked against the wallet it was created for.
            late = ok(call('/api/billing/checkout', {'planId': plan['id'], 'currency': 'IRT', 'requestKey': str(uuid.uuid4())}, other))
            late_uid = late['checkoutUrl'].rsplit('/', 1)[1]

            # A wallet Ezpay already gives access to (code 21): phone ownership must be proved with an SMS code.
            time.sleep(61)  # one Ezpay code per minute
            refused = call('/api/admin/wallet/connection', {'phoneNumber': PHONE_LINKED}, admin)
            assert refused[0] == 409 and refused[1]['code'] == 'wallet_phone_proof_required', refused
            assert ok(call('/api/admin/wallet/phone-proof/request', {'phoneNumber': PHONE_LINKED}, admin), 409)['code'] == 'wallet_proof_no_device'
            with closing(sqlite3.connect(database)) as db, db:
                db.execute("INSERT INTO Devices (Id, UserId, Name, DeviceToken, IsOnline, CreatedAt, LastSeenAt) VALUES (?, ?, 'Admin phone', ?, 1, ?, ?)",
                           (str(uuid.uuid4()).upper(), admin_id.upper(), uuid.uuid4().hex, '2026-11-13 00:00:00', '2026-11-13 00:00:00'))
            ok(call('/api/admin/wallet/phone-proof/request', {'phoneNumber': PHONE_LINKED}, admin))
            with closing(sqlite3.connect(database)) as db:
                to, body = db.execute("SELECT ToNumber, Body FROM OutboundJobs ORDER BY CreatedAt DESC LIMIT 1").fetchone()
            code = re.search(r'(\d{6})', body)[1]
            assert to == PHONE_LINKED
            assert ok(call('/api/admin/wallet/phone-proof/confirm', {'phoneNumber': PHONE_LINKED, 'code': '000000' if code != '000000' else '111111'}, admin), 400)['code'] == 'wallet_proof_invalid'
            view = ok(call('/api/admin/wallet/phone-proof/confirm', {'phoneNumber': PHONE_LINKED, 'code': code}, admin))
            assert view['connection']['phoneNumber'] == PHONE_LINKED and not view['merchantEnabled'], view  # a new number clears merchant approval
            assert not ok(call('/api/billing/catalog', token=user))['tomanAvailable']

            # Both numbers are now proven: either can be selected again without a code.
            assert {w['phoneNumber'] for w in ok(call('/api/admin/wallet/reusable', token=admin))} == {PHONE_NEW, PHONE_LINKED}
            view = ok(call('/api/admin/wallet/reuse', {'phoneNumber': PHONE_NEW}, admin))
            assert view['connection']['phoneNumber'] == PHONE_NEW and not view['merchantEnabled'], view
            assert call('/api/admin/wallet/reuse', {'phoneNumber': '09129999999'}, admin)[0] == 403

            # The background reconciler confirms the older order on its original wallet without the buyer returning.
            pay(late_uid)
            for _ in range(90):
                if order_status(other, late['id']).startswith('paid_'): break
                time.sleep(1)
            else: raise AssertionError('Background reconciliation did not confirm the payment')
            txs = ok(call('/api/admin/wallet/transactions', token=admin))
            assert txs['total'] == 2 and {t['receivingPhone'] for t in txs['items']} == {PHONE_NEW}, txs

            ok(call('/api/admin/wallet', token=admin, method='DELETE'), 204)
            assert ok(call('/api/admin/wallet', token=admin))['connection']['status'] == 'Disconnected'
            assert all(m in ('GET', 'POST', 'PUT') for m, _ in state['calls'])

            # Billing__DefaultWalletPhone connects the configured wallet once on a fresh install (no SMS proof; it is operator config),
            # with merchant services on because Ezpay reports it verified. A later admin disconnect is never overridden.
            stop(process); process = None
            with lock: state['linked'].add(PHONE_DEFAULT); state['verified'].add(PHONE_DEFAULT)
            boot_env = {**env, 'ConnectionStrings__Default': 'Data Source=' + str(temporary / 'bootstrap.db'), 'Billing__DefaultWalletPhone': PHONE_DEFAULT}
            process = start(boot_env)
            admin, _ = login('admin@ndorbit.co.nz'); buyer, _ = login('buyer@example.com')
            for _ in range(90):
                view = ok(call('/api/admin/wallet', token=admin))
                if view['connection']['status'] == 'Connected': break
                time.sleep(1)
            else: raise AssertionError('Default wallet was not connected')
            assert view['connection']['phoneNumber'] == PHONE_DEFAULT and view['merchantEnabled'] and view['purchaseReady'], view
            assert ok(call('/api/admin/wallet/reusable', token=admin))[0]['phoneNumber'] == PHONE_DEFAULT
            ok(call('/api/admin/wallet', token=admin, method='DELETE'), 204)
            stop(process); process = start(boot_env)
            time.sleep(3)
            assert ok(call('/api/admin/wallet', token=admin))['connection']['status'] == 'Disconnected'
            print('Ezpay default wallet and toman payment tests passed')
        finally:
            if process: stop(process)
            log.close(); relay.shutdown()


if __name__ == '__main__':
    main()

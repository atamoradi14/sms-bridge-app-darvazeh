"""Real HTTP/SQLite regression test for promo codes on SMS and VPN checkout.

Reuses the Ezpay relay fixture from verify-ezpay-wallet.py (no real Ezpay call). Covers admin create/edit/validation,
product scope, percent and fixed discounts, the payment minimum, total and per-member limits (including open checkouts
holding a place), start/expiry, deactivation, free (100%) orders that activate without a provider, request-key safety and
the rate limit. Run after `dotnet build server/FieldSense.Api`.
"""
from contextlib import closing
from datetime import datetime, timedelta, timezone
import importlib.util
from http.server import ThreadingHTTPServer
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
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ezpay_fixture', ROOT / 'scripts/verify-ezpay-wallet.py')
fixture = importlib.util.module_from_spec(spec); spec.loader.exec_module(fixture)
IR = {'CF-IPCountry': 'IR'}


def iso(delta_days):
    return (datetime.now(timezone.utc) + timedelta(days=delta_days)).strftime('%Y-%m-%dT%H:%M:%SZ')


def main():
    relay = ThreadingHTTPServer(('127.0.0.1', 0), fixture.Relay)
    threading.Thread(target=relay.serve_forever, daemon=True).start()
    with tempfile.TemporaryDirectory(prefix='darvazeh-promo-') as temporary:
        temporary = Path(temporary); database = temporary / 'billing.db'
        with socket.socket() as sock: sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': base,
               'FieldSense__AcsMock': 'true', 'FieldSense__AcsConnectionString': '', 'FieldSense__AdminEmail': 'admin@ndorbit.co.nz',
               'FieldSense__AdminPassword': 'billing-admin-fixture', 'FieldSense__JwtKey': 'disposable-billing-test-key-' + 'x' * 40,
               'ConnectionStrings__Default': 'Data Source=' + str(database), 'Billing__StripeEnabled': 'false',
               'Billing__PortalOrigin': 'https://darvazeh.daryajan.com', 'Billing__EzpayEnabled': 'true',
               'Billing__PaymentRelayEndpoint': f'http://127.0.0.1:{relay.server_port}', 'Billing__PaymentRelayApiKey': fixture.RELAY_KEY,
               'Billing__DefaultWalletPhone': fixture.PHONE_LINKED}
        log = (temporary / 'server.log').open('wb'); process = None

        def call(path, body=None, token=None, method=None, extra=None):
            headers = {'Content-Type': 'application/json', **(extra or {})}
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
            return data['token']

        def ok(result, expected=200):
            assert result[0] == expected, result
            return result[1]

        def refused(result, code, status=400):
            assert result[0] == status and result[1].get('code') == code, (code, result)

        def promo(admin, **fields):
            body = {'code': 'X', 'description': '', 'product': 'all', 'discountType': 'percent', 'discountPercent': 10, 'amountOffToman': None,
                    'amountOffUsdCents': None, 'maxUses': None, 'maxUsesPerUser': 1, 'startsAt': None, 'expiresAt': None, 'isActive': True, **fields}
            return call('/api/admin/promos', body, admin)

        def checkout(token, plan, currency, code=None, key=None, extra=IR):
            return call('/api/billing/checkout', {'planId': plan, 'currency': currency, 'requestKey': key or str(uuid.uuid4()), 'promoCode': code}, token, extra=extra)

        try:
            process = subprocess.Popen(['dotnet', str(ROOT / 'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],
                                       cwd=ROOT / 'server/FieldSense.Api', env=env, stdout=log, stderr=log)
            for _ in range(150):
                if process.poll() is not None: raise AssertionError('API startup failed')
                try:
                    if call('/health')[0] == 200: break
                except OSError: pass
                time.sleep(.15)
            admin = login('admin@ndorbit.co.nz')
            buyer, second, third, fourth, fifth = (login(f'm{i}@example.com') for i in range(1, 6))
            plan = ok(call('/api/admin/plans', {'code': 'promo-silver', 'name': 'Silver', 'durationDays': 30, 'messageLimit': 200, 'emailForwardLimit': 10,
                                                'sendEnabled': True, 'receiveEnabled': True, 'priceUsdCents': 300, 'priceToman': 500000, 'isPurchasable': True}, admin))
            pid = plan['id']
            # Default wallet bootstraps from config in the background; wait until toman checkout is offered.
            for _ in range(80):
                if ok(call('/api/billing/catalog', token=buyer))['tomanAvailable']: break
                with fixture.lock: fixture.state['verified'].add(fixture.PHONE_LINKED)
                time.sleep(.25)
            if not ok(call('/api/billing/catalog', token=buyer))['tomanAvailable']:
                ok(call('/api/admin/wallet/merchant', {'enabled': True}, admin, 'PUT'))
            assert ok(call('/api/billing/catalog', token=buyer))['tomanAvailable']

            # Admin only, and validated.
            assert call('/api/admin/promos', token=buyer)[0] == 403
            refused(promo(admin, code='a'), 'invalid_promo')
            refused(promo(admin, code='BAD CODE'), 'invalid_promo')
            refused(promo(admin, code='ZERO', discountPercent=0), 'invalid_promo')
            refused(promo(admin, code='FIX', discountType='fixed'), 'invalid_promo')
            refused(promo(admin, code='DATES', startsAt=iso(2), expiresAt=iso(1)), 'invalid_promo')
            save20 = ok(promo(admin, code='save20', product='sms', discountPercent=20, maxUses=2, maxUsesPerUser=1, description='Launch'))
            assert save20['code'] == 'SAVE20'
            refused(promo(admin, code='Save20'), 'promo_exists', 409)

            # Quote: server-side prices, case-insensitive entry, product scope, unknown code.
            quote = ok(call('/api/billing/promo', {'code': ' save20 ', 'product': 'sms'}, buyer))
            line = next(p for p in quote['plans'] if p['planId'] == pid)
            assert line['toman'] == {'original': 500000, 'discount': 100000, 'final': 400000} and line['usd']['final'] == 240, quote
            refused(call('/api/billing/promo', {'code': 'SAVE20', 'product': 'vpn'}, buyer), 'promo_wrong_product')
            refused(call('/api/billing/promo', {'code': 'NOPE1', 'product': 'sms'}, buyer), 'promo_invalid')

            # Discounted toman checkout: the Ezpay link is for the discounted amount (rial), the order keeps the breakdown.
            key = str(uuid.uuid4())
            order = ok(checkout(buyer, pid, 'IRT', 'save20', key))
            uid = order['alternateUrl'].rsplit('/', 1)[1]
            assert fixture.state['links'][uid]['amount'] == 4_000_000, fixture.state['links'][uid]
            mine = next(o for o in ok(call('/api/billing/orders', token=buyer)) if o['id'] == order['id'])
            assert (mine['amountMinor'], mine['originalAmountMinor'], mine['discountMinor'], mine['promoCode']) == (400000, 500000, 100000, 'SAVE20'), mine
            # Same request key with a different code is refused; same key and code returns the same order.
            assert checkout(buyer, pid, 'IRT', None, key)[0] == 400
            assert ok(checkout(buyer, pid, 'IRT', 'SAVE20', key))['id'] == order['id']

            # Limit 2: the buyer's open checkout and a second member's hold both places; a third member is refused.
            ok(checkout(second, pid, 'IRT', 'SAVE20'))
            refused(checkout(third, pid, 'IRT', 'SAVE20'), 'promo_used_up')
            refused(call('/api/billing/promo', {'code': 'SAVE20', 'product': 'sms'}, third), 'promo_used_up')
            # Paying uses it; the same member cannot use it again.
            with fixture.lock: fixture.state['links'][uid]['status'] = 'SUCCESS'
            assert ok(call(f'/api/billing/orders/{order["id"]}/reconcile', {}, buyer))['status'].startswith('paid_')
            refused(checkout(buyer, pid, 'IRT', 'SAVE20'), 'promo_user_limit')
            listed = next(p for p in ok(call('/api/admin/promos', token=admin)) if p['code'] == 'SAVE20')
            assert listed['used'] == 1 and listed['holding'] == 1 and listed['discountToman'] == 100000 and listed['status'] == 'active', listed

            # Fixed toman discount: not for USD; the minimum toman charge is kept.
            ok(promo(admin, code='TOMAN100K', discountType='fixed', discountPercent=0, amountOffToman=100000, maxUsesPerUser=None))
            line = next(p for p in ok(call('/api/billing/promo', {'code': 'TOMAN100K', 'product': 'sms'}, third))['plans'] if p['planId'] == pid)
            assert line['usd'] is None and line['toman']['final'] == 400000, line
            refused(checkout(third, pid, 'USD', 'TOMAN100K'), 'promo_wrong_currency')
            ok(promo(admin, code='ALMOST', discountType='fixed', discountPercent=0, amountOffToman=499500))
            floor = ok(checkout(third, pid, 'IRT', 'ALMOST'))
            assert fixture.state['links'][floor['alternateUrl'].rsplit('/', 1)[1]]['amount'] == 10_000  # 1,000 toman minimum

            # Dates and deactivation.
            ok(promo(admin, code='OLD', expiresAt=iso(-1), startsAt=iso(-3)))
            ok(promo(admin, code='SOON', startsAt=iso(2)))
            refused(call('/api/billing/promo', {'code': 'OLD', 'product': 'sms'}, fourth), 'promo_expired')
            refused(call('/api/billing/promo', {'code': 'SOON', 'product': 'sms'}, fourth), 'promo_not_started')
            off = ok(promo(admin, code='PAUSED'))
            body = {k: off[k] for k in ('code', 'description', 'product', 'discountType', 'discountPercent', 'amountOffToman', 'amountOffUsdCents', 'maxUses', 'maxUsesPerUser', 'startsAt', 'expiresAt')}
            ok(call(f'/api/admin/promos/{off["id"]}', {**body, 'isActive': False}, admin, 'PUT'))
            refused(call('/api/billing/promo', {'code': 'PAUSED', 'product': 'sms'}, fourth), 'promo_invalid')
            # A used code cannot be renamed; an unused one can.
            s20 = {k: save20[k] for k in body}
            refused(call(f'/api/admin/promos/{save20["id"]}', {**s20, 'code': 'SAVE25', 'isActive': True}, admin, 'PUT'), 'promo_in_use')
            ok(call(f'/api/admin/promos/{off["id"]}', {**body, 'code': 'PAUSED2', 'isActive': False}, admin, 'PUT'))

            # 100% off: activates without any provider (USD checkout is disabled here) and without an Iranian IP for toman.
            ok(promo(admin, code='FREE100', discountPercent=100, maxUses=5))
            free = ok(checkout(fourth, pid, 'USD', 'free100', extra={}))
            assert free['free'] and free['status'].startswith('paid_') and free['checkoutUrl'] is None, free
            me = ok(call('/api/user/me', token=fourth))
            assert me['subscription']['active'] and me['subscription']['messageLimit'] == 200, me['subscription']
            free2 = ok(checkout(fifth, pid, 'IRT', 'FREE100', extra={'CF-IPCountry': 'DE'}))
            assert free2['free'], free2
            refused(checkout(fifth, pid, 'IRT', None, extra={'CF-IPCountry': 'DE'}), 'toman_requires_iran_ip', 403)  # paid toman still needs Iran
            with closing(sqlite3.connect(database)) as db:
                row = db.execute("SELECT Provider, AmountMinor, OriginalAmountMinor, DiscountMinor, PromoCode FROM PaymentOrders WHERE Id=?",
                                 (free['id'].upper(),)).fetchone() or db.execute("SELECT Provider, AmountMinor, OriginalAmountMinor, DiscountMinor, PromoCode FROM PaymentOrders WHERE lower(Id)=?", (free['id'].lower(),)).fetchone()
            assert row == ('promo', 0, 300, 300, 'FREE100'), row

            # VPN scope: a VPN-only code quotes the VPN plan and is refused for SMS.
            ok(promo(admin, code='VPNHALF', product='vpn', discountPercent=50))
            vq = ok(call('/api/billing/promo', {'code': 'VPNHALF', 'product': 'vpn'}, fifth))
            assert len(vq['plans']) == 1 and vq['plans'][0]['toman']['final'] == vq['plans'][0]['toman']['original'] // 2, vq
            refused(call('/api/billing/promo', {'code': 'VPNHALF', 'product': 'sms'}, fifth), 'promo_wrong_product')

            # Guessing is rate limited per member.
            guesser = login('guesser@example.com')
            codes = [call('/api/billing/promo', {'code': f'GUESS{i}', 'product': 'sms'}, guesser)[0] for i in range(22)]
            assert codes[:20] == [400] * 20 and codes[-1] == 429, codes
            print('Promo code tests passed')
        finally:
            if process and process.poll() is None:
                process.terminate(); process.wait(10)
            relay.shutdown(); log.close()
            if os.environ.get('SHOW_LOG'): print((temporary / 'server.log').read_text(errors='replace')[-4000:])


if __name__ == '__main__':
    main()

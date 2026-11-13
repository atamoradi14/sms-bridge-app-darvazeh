"""Real HTTP/SQLite billing regression tests against a local Stripe protocol fixture. No payments."""
import concurrent.futures
from contextlib import closing
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
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
sessions, idempotency, intents, charges = {}, {}, {}, {}
forms = []
lock = threading.Lock()


class StripeFixture(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def respond(self, body, status=200):
        self.send_response(status); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps(body).encode())
    def do_POST(self):
        assert self.headers['Authorization'] == 'Bearer sk_test_fixture'
        form = urllib.parse.parse_qs(self.rfile.read(int(self.headers['Content-Length'])).decode())
        assert self.path == '/v1/checkout/sessions'
        assert form['mode'] == ['payment'] and form['line_items[0][price_data][currency]'] == ['usd']
        assert not any(key.startswith('payment_method_types') for key in form)
        assert 'recurring' not in str(form) and form['adaptive_pricing[enabled]'] == ['false']
        with lock:
            key = self.headers['Idempotency-Key']
            if key in idempotency: return self.respond(sessions[idempotency[key]])
            identity = 'cs_test_' + uuid.uuid4().hex
            amount = int(form['line_items[0][price_data][unit_amount]'][0])
            payment = 'pi_' + uuid.uuid4().hex
            metadata = {'order_id': form['metadata[order_id]'][0], 'user_id': form['metadata[user_id]'][0]}
            sessions[identity] = {'id':identity,'url':'https://checkout.stripe.com/c/pay/'+identity,
                'mode':'payment','livemode':False,'status':'open','payment_status':'unpaid','currency':'usd',
                'amount_total':amount,'client_reference_id':form['client_reference_id'][0], 'metadata':metadata,
                'payment_intent':payment,'expires_at':int(time.time())+3600,
                'line_items':{'data':[{'quantity':1,'amount_total':amount}]}}
            intents[payment] = {'id':payment,'metadata':metadata,'currency':'usd','amount':amount}
            idempotency[key] = identity; forms.append(form)
            self.respond(sessions[identity])
    def do_GET(self):
        path = urllib.parse.urlsplit(self.path).path
        identity = path.rsplit('/',1)[-1]
        store = sessions if '/checkout/sessions/' in path else charges if '/charges/' in path else intents
        self.respond(store[identity])


def main():
    stripe = ThreadingHTTPServer(('127.0.0.1',0),StripeFixture)
    threading.Thread(target=stripe.serve_forever,daemon=True).start()
    with tempfile.TemporaryDirectory(prefix='darvazeh-billing-') as temporary:
        temporary=Path(temporary);database=temporary/'billing.db'
        with socket.socket() as sock: sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
        base=f'http://127.0.0.1:{port}'
        env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':base,
            'FieldSense__AcsMock':'true','FieldSense__AcsConnectionString':'','FieldSense__AdminEmail':'admin@ndorbit.co.nz',
            'FieldSense__AdminPassword':'billing-admin-fixture','FieldSense__JwtKey':'disposable-billing-test-key-'+'x'*40,
            'ConnectionStrings__Default':'Data Source='+str(database),
            'Billing__StripeEnabled':'true','Billing__StripeSecretKey':'sk_test_fixture',
            'Billing__StripeWebhookSecret':'whsec_fixture','Billing__StripeApiBase':f'http://127.0.0.1:{stripe.server_port}',
            'Billing__PortalOrigin':'https://darvazeh.daryajan.com'}
        log=(temporary/'server.log').open('wb');process=None
        def call(path,body=None,token=None,method=None,headers=None,raw=None):
            all_headers={'Content-Type':'application/json',**(headers or {})}
            if token:all_headers['Authorization']='Bearer '+token
            req=urllib.request.Request(base+path,headers=all_headers,data=raw if raw is not None else json.dumps(body).encode() if body is not None else None,method=method or ('POST' if body is not None or raw is not None else 'GET'))
            try:
                with urllib.request.urlopen(req,timeout=30) as r:return r.status,json.load(r)
            except urllib.error.HTTPError as e:return e.code,json.loads(e.read() or '{}')
        def start():
            nonlocal process
            process=subprocess.Popen(['dotnet',str(ROOT/'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],cwd=ROOT/'server/FieldSense.Api',env=env,stdout=log,stderr=log)
            for _ in range(100):
                if process.poll() is not None:raise AssertionError('Billing test API startup failed')
                try:
                    if call('/health')[0]==200:return
                except OSError:pass
                time.sleep(.15)
            raise AssertionError('Billing test startup timed out')
        def login(email):
            assert call('/api/auth/request-otp',{'email':email})[0]==200
            status,data=call('/api/auth/verify-otp',{'email':email,'code':'000000','adminPassword':'billing-admin-fixture'})
            assert status==200
            return data['token'],data['user']['id']
        def webhook(identity,type='checkout.session.completed',event_id=None,signature=True,stamp=None,live=False):
            event={'id':event_id or 'evt_'+uuid.uuid4().hex,'livemode':live,'type':type,'data':{'object':{'id':identity}}}
            raw=json.dumps(event,separators=(',',':')).encode();stamp=stamp or int(time.time())
            digest=hmac.new(b'whsec_fixture',str(stamp).encode()+b'.'+raw,hashlib.sha256).hexdigest()
            headers={'Stripe-Signature':f't={stamp},v1={digest}' if signature else 'invalid'}
            return call('/api/billing/stripe/webhook',raw=raw,headers=headers)
        def checkout(token,plan,key=None):
            key=key or str(uuid.uuid4());status,data=call('/api/billing/checkout',{'planId':plan,'currency':'USD','requestKey':key},token)
            assert status==200,(status,data)
            identity=next(k for k,v in sessions.items() if v['metadata']['order_id']==data['id'])
            return data,identity,key
        def paid(identity):sessions[identity]['payment_status']='paid';sessions[identity]['status']='complete'
        def me(token):return call('/api/user/me',token=token)[1]['subscription']
        try:
            start();admin,admin_id=login('admin@ndorbit.co.nz');user,user_id=login('buyer@example.com');other,_=login('other@example.com')
            plan_data={'code':'billing-silver','name':'Silver','durationDays':30,'messageLimit':200,'emailForwardLimit':10,'sendEnabled':True,'receiveEnabled':True,'priceUsdCents':1234,'priceToman':500000,'isPurchasable':True}
            status,plan=call('/api/admin/plans',plan_data,admin);assert status==200
            assert call('/api/admin/plans/'+plan['id'],plan_data,user,'PUT')[0]==403
            invalid={**plan_data,'priceUsdCents':1};assert call('/api/admin/plans/'+plan['id'],invalid,admin,'PUT')[0]==400
            assert call('/api/billing/checkout',{'planId':plan['id'],'currency':'IRT','requestKey':str(uuid.uuid4())},user)[0]==503
            assert call('/api/billing/catalog',token=user)[1]['stripeAvailable']
            order,session,key=checkout(user,plan['id'])
            again,_,_=checkout(user,plan['id'],key);assert again['id']==order['id'] and len(sessions)==1
            assert call('/api/billing/orders/'+order['id']+'/reconcile',{},other)[0]==404
            assert forms[0]['line_items[0][price_data][unit_amount]']==['1234']
            assert forms[0]['success_url'][0].startswith('https://darvazeh.daryajan.com/?payment=success')
            assert me(user)['planCode']=='trial'
            print('PASS: authoritative USD cents, independent toman price, fixed one-period checkout, authorization and creation idempotency')
            assert webhook(session,signature=False)[0]==400
            assert webhook(session,stamp=int(time.time())-1000)[0]==400
            assert webhook(session,live=True)[0]==400
            assert webhook(session)[0]==200 and me(user)['planCode']=='trial'
            assert call('/api/billing/orders/'+order['id']+'/reconcile',{},user)[0]==200 and me(user)['planCode']=='trial'
            # Snapshot is immutable even if the administrator changes the published plan later.
            assert call('/api/admin/plans/'+plan['id'],{**plan_data,'priceUsdCents':9999,'messageLimit':999,'emailForwardLimit':199},admin,'PUT')[0]==200
            paid(session);sessions[session]['amount_total']=1
            assert webhook(session)[0]==400 and me(user)['planCode']=='trial'
            sessions[session]['amount_total']=1234
            assert webhook(session)[0]==200
            assert me(user)['planCode']=='billing-silver' and me(user)['messageLimit']==200
            assert me(user)['usedMessages']==0 and me(user)['emailForwardLimit']==10
            with closing(sqlite3.connect(database)) as db:current=db.execute('SELECT Id FROM Subscriptions WHERE IsCurrent=1 AND UserId=?',(user_id.upper(),)).fetchone()[0]
            assert webhook(session)[0]==200
            assert call('/api/billing/orders/'+order['id']+'/reconcile',{},user)[0]==200
            with closing(sqlite3.connect(database)) as db:assert db.execute('SELECT Id FROM Subscriptions WHERE IsCurrent=1 AND UserId=?',(user_id.upper(),)).fetchone()[0]==current
            print('PASS: signature/timestamp/mode validation, no redirect-only access, amount verification, immutable plan snapshot, exactly-once fulfillment')
            second,session2,_=checkout(user,plan['id']);paid(session2)
            with concurrent.futures.ThreadPoolExecutor(2) as pool:assert all(x[0]==200 for x in pool.map(lambda _:webhook(session2),[1,2]))
            assert call('/api/billing/catalog',token=user)[1]['queuedPeriods']==1
            assert me(user)['messageLimit']==200
            assert call('/api/admin/users/'+user_id+'/features',{'sendEnabled':False,'receiveEnabled':True},admin,'PUT')[0]==200
            with closing(sqlite3.connect(database)) as db,db:db.execute('UPDATE Subscriptions SET UsedMessages=MessageLimit WHERE UserId=? AND IsCurrent=1',(user_id.upper(),))
            sub=me(user);assert sub['messageLimit']==999 and sub['usedMessages']==0 and not sub['canSend'] and sub['canReceive']
            assert call('/api/billing/catalog',token=user)[1]['queuedPeriods']==0
            assert call('/api/admin/users/'+user_id+'/features',{'sendEnabled':None,'receiveEnabled':None},admin,'PUT')[0]==200
            assert me(user)['canSend']
            print('PASS: paid-period queue, concurrent notifications, quota-boundary activation and administrator restrictions survive purchases')
            # Refund the queued order before fulfillment; it must never become active.
            third,session3,_=checkout(user,plan['id']);paid(session3);assert webhook(session3)[0]==200
            payment=sessions[session3]['payment_intent'];charge='ch_'+uuid.uuid4().hex
            charges[charge]={'id':charge,'payment_intent':payment,'amount':9999,'amount_refunded':9999,'currency':'usd','livemode':False}
            assert webhook(charge,'charge.refunded')[0]==200
            assert call('/api/billing/catalog',token=user)[1]['queuedPeriods']==0
            assert webhook(session3)[0]==200
            orders=call('/api/billing/orders',token=user)[1]
            assert next(o for o in orders if o['id']==third['id'])['status']=='refunded'
            # Full refund of the active period expires its entitlement without deleting history.
            payment=sessions[session2]['payment_intent'];charge2='ch_'+uuid.uuid4().hex
            charges[charge2]={'id':charge2,'payment_intent':payment,'amount':9999,'amount_refunded':1000,'currency':'usd','livemode':False}
            assert webhook(charge2,'charge.refunded')[0]==200 and me(user)['active']
            charges[charge2]['amount_refunded']=9999;assert webhook(charge2,'charge.refunded')[0]==200
            assert not me(user)['active']
            print('PASS: partial/full refunds, refunded queued periods, refund-before-repeat payment notifications and access expiry')
            # Recover a payment even if the webhook was temporarily unavailable.
            fourth,session4,_=checkout(user,plan['id']);paid(session4)
            assert call('/api/billing/orders/'+fourth['id']+'/reconcile',{},user)[0]==200
            assert me(user)['active']
            assert call('/api/admin/payments',token=admin)[0]==200 and call('/api/admin/payments',token=user)[0]==403
            process.terminate();process.wait(timeout=10);start()
            assert me(user)['active'] and len(call('/api/billing/orders',token=user)[1])==4
            with closing(sqlite3.connect(database)) as db:assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
            print('PASS: missing-webhook reconciliation, scoped payment history, restart-safe ledger and database integrity')
        finally:
            if process and process.poll() is None:process.terminate();process.wait(timeout=10)
            log.close();stripe.shutdown();stripe.server_close()


if __name__=='__main__':main()

"""Exercise the real API against private disposable SQLite databases; no email/SMS sent."""
import concurrent.futures
from contextlib import closing
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]


def legacy_database(path):
    with closing(sqlite3.connect(path)) as db, db:
        db.executescript('''
        CREATE TABLE Users(Id TEXT PRIMARY KEY, Email TEXT NOT NULL, Role INTEGER NOT NULL, CanRelaySms INTEGER NOT NULL,
          ForwardEmailEnabled INTEGER NOT NULL, ForwardEmailAddress TEXT, CreatedAt TEXT NOT NULL, LastLoginAt TEXT);
        CREATE TABLE Devices(Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, Name TEXT NOT NULL, DeviceToken TEXT NOT NULL,
          PhoneNumber TEXT, IsOnline INTEGER NOT NULL, LastSeenAt TEXT, CreatedAt TEXT NOT NULL);
        CREATE TABLE Messages(Id TEXT PRIMARY KEY,UserId TEXT NOT NULL,DeviceId TEXT,Direction TEXT NOT NULL,
          PeerNumber TEXT NOT NULL,Body TEXT NOT NULL,Status TEXT NOT NULL,EmailForwarded INTEGER NOT NULL,CreatedAt TEXT NOT NULL);
        CREATE TABLE OutboundJobs(Id TEXT PRIMARY KEY,UserId TEXT NOT NULL,DeviceId TEXT NOT NULL,SmsMessageId TEXT NOT NULL,
          ToNumber TEXT NOT NULL,Body TEXT NOT NULL,Status TEXT NOT NULL,Error TEXT,CreatedAt TEXT NOT NULL,CompletedAt TEXT);
        CREATE TABLE OtpChallenges(Id TEXT PRIMARY KEY,Email TEXT NOT NULL,CodeHash TEXT NOT NULL,ExpiresAt TEXT NOT NULL,
          CreatedAt TEXT NOT NULL,Consumed INTEGER NOT NULL);
        ''')
        user = str(uuid.uuid4()).upper()
        now = datetime.now(timezone.utc).replace(tzinfo=None).isoformat(' ')
        db.execute('INSERT INTO Users VALUES(?,?,0,0,0,NULL,?,NULL)', (user, 'legacy@example.com', now))
        db.execute('INSERT INTO Messages VALUES(?,?,NULL,?,?,?,?,0,?)',
            (str(uuid.uuid4()).upper(), user, 'inbound', '+1000', 'preserved historical message', 'received', now))


def main():
    with tempfile.TemporaryDirectory(prefix='darvazeh-membership-') as directory:
        directory = Path(directory)
        database = directory / 'test.db'
        legacy_database(database)
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        env = {**os.environ, 'FieldSense__AcsMock': 'true', 'FieldSense__AcsConnectionString': '',
            'FieldSense__AdminEmail': 'admin@ndorbit.co.nz', 'FieldSense__AdminPassword': 'test-admin-only',
            'FieldSense__JwtKey': 'private-disposable-test-key-' + 'x' * 40,
            'ConnectionStrings__Default': 'Data Source=' + str(database), 'ASPNETCORE_URLS': base}
        log = (directory / 'server.log').open('wb')
        process = None

        def request(path, body=None, token=None, device=None, method=None):
            headers = {'Content-Type': 'application/json'}
            if token: headers['Authorization'] = 'Bearer ' + token
            if device: headers['X-Device-Token'] = device
            req = urllib.request.Request(base + path, headers=headers,
                data=json.dumps(body).encode() if body is not None else None,
                method=method or ('POST' if body is not None else 'GET'))
            try:
                with urllib.request.urlopen(req, timeout=30) as response:
                    return response.status, json.load(response)
            except urllib.error.HTTPError as error:
                return error.code, json.loads(error.read() or '{}')

        def start():
            nonlocal process
            process = subprocess.Popen(['dotnet', str(ROOT / 'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],
                cwd=ROOT / 'server/FieldSense.Api', env=env, stdout=log, stderr=log)
            for _ in range(100):
                if process.poll() is not None: raise AssertionError('Test API startup failed')
                try:
                    if request('/health')[0] == 200: return
                except OSError: pass
                time.sleep(.15)
            raise AssertionError('Test API did not become healthy')

        def login(email, phone=False):
            assert request('/api/auth/request-otp', {'email': email})[0] == 200
            body = {'email': email, 'code': '000000'}
            if email == 'admin@ndorbit.co.nz': body['adminPassword'] = 'test-admin-only'
            if phone: body.update(client='phone', installationId=email, deviceName='Test phone')
            status, response = request('/api/auth/verify-otp', body)
            assert status == 200, ('login', status)
            return response

        try:
            start()
            admin = login('admin@ndorbit.co.nz')['token']
            legacy = login('legacy@example.com')
            assert legacy['user']['subscription']['usedMessages'] == 1
            assert request('/api/user/messages', token=legacy['token'])[1][0]['body'] == 'preserved historical message'
            print('PASS: additive legacy upgrade preserves messages and backfills usage')
            account = login('member@example.com', True)
            token = account['token']; device = account['device']['deviceToken']; user_id = account['user']['id']
            subscription = account['user']['subscription']
            assert subscription['messageLimit'] == 20 and subscription['canSend'] and subscription['canReceive']
            assert 2.99 < (datetime.fromisoformat(subscription['expiresAt']) - datetime.fromisoformat(subscription['startsAt'])).total_seconds()/86400 < 3.01
            assert request('/api/admin/users', token=token)[0] == 403
            print('PASS: three-day/20-message trial, both features enabled, administrator boundary')
            assert request('/api/admin/users/'+user_id+'/features', {'sendEnabled':False,'receiveEnabled':True},admin,method='PUT')[0] == 200
            # Phone sign-in must not re-enable an administrator-disabled feature.
            account = login('member@example.com', True); device=account['device']['deviceToken']
            assert not request('/api/user/me',token=token)[1]['subscription']['canSend']
            assert request('/api/user/sms/send',{'toNumber':'+1000','body':'blocked'},token)[0] == 403
            assert request('/api/device/inbound-sms',{'from':'+1000','body':'accepted','clientMessageId':'one'},device=device)[0] == 200
            assert request('/api/device/inbound-sms',{'from':'+1000','body':'accepted','clientMessageId':'one'},device=device)[0] == 200
            assert request('/api/user/me',token=token)[1]['subscription']['usedMessages'] == 1
            assert request('/api/admin/users/'+user_id+'/features', {'sendEnabled':True,'receiveEnabled':False},admin,method='PUT')[0] == 200
            assert request('/api/device/inbound-sms',{'from':'+1000','body':'blocked','clientMessageId':'two'},device=device)[0] == 403
            assert request('/api/user/sms/send',{'toNumber':'+1000','body':'queued','clientMessageId':'out-one'},token)[0] == 200
            assert request('/api/user/sms/send',{'toNumber':'+1000','body':'queued','clientMessageId':'out-one'},token)[0] == 200
            assert request('/api/user/me',token=token)[1]['subscription']['usedMessages'] == 2
            assert request('/api/admin/users/'+user_id+'/features', {'sendEnabled':True,'receiveEnabled':True},admin,method='PUT')[0] == 200
            print('PASS: independent feature controls, no login bypass, inbound/outbound retry deduplication')
            for index in range(17):
                assert request('/api/device/inbound-sms',{'from':'+1000','body':'test','clientMessageId':'quota-'+str(index)},device=device)[0] == 200
            with concurrent.futures.ThreadPoolExecutor(2) as pool:
                statuses = list(pool.map(lambda n: request('/api/device/inbound-sms', {'from':'+1000','body':'race','clientMessageId':'race-'+str(n)}, device=device)[0], [1,2]))
            assert sorted(statuses) == [200,403], statuses
            assert request('/api/user/me',token=token)[1]['subscription']['usedMessages'] == 20
            assert request('/api/user/sms/send',{'toNumber':'+1000','body':'over quota'},token)[0] == 403
            pending=request('/api/device/outbound/pending',device=device)[1]
            assert len(pending)==1, 'The final reserved outgoing message must still dispatch at quota'
            assert request('/api/device/outbound/'+pending[0]['id']+'/result',{'status':'sent'},device=device)[0]==200
            assert request('/api/device/outbound/'+pending[0]['id']+'/result',{'status':'failed'},device=device)[0]==200
            assert next(m for m in request('/api/user/messages',token=token)[1] if m['direction']=='outbound')['status']=='sent'
            print('PASS: combined quota, concurrent final-unit reservation, queued dispatch and result retry safety')
            status,plan=request('/api/admin/plans',{'code':'test-plan','name':'Test membership','durationDays':7,'messageLimit':10,'sendEnabled':True,'receiveEnabled':False},admin)
            assert status==200
            assert request('/api/admin/users/'+user_id+'/subscription',{'planId':plan['id']},admin,method='PUT')[0]==200
            assert request('/api/user/me',token=token)[1]['subscription']['remainingMessages']==10
            assert len(request('/api/user/messages',token=token)[1])==20
            with closing(sqlite3.connect(database)) as db, db:
                db.execute("UPDATE Subscriptions SET ExpiresAt='2000-01-01 00:00:00' WHERE UserId=? AND IsCurrent=1",(user_id.upper(),))
            assert request('/api/user/sms/send',{'toNumber':'+1000','body':'expired'},token)[0]==403
            assert request('/api/admin/activities',token=admin)[0]==200
            print('PASS: plan creation/renewal/history, expiry enforcement, persistent activity reporting')
            # The extra administrator credential must not consume the code before retry.
            request('/api/auth/request-otp',{'email':'admin@ndorbit.co.nz'})
            assert request('/api/auth/verify-otp',{'email':'admin@ndorbit.co.nz','code':'000000'})[0]==401
            assert request('/api/auth/verify-otp',{'email':'admin@ndorbit.co.nz','code':'000000','adminPassword':'test-admin-only'})[0]==200
            request('/api/auth/request-otp',{'email':'attempts@example.com'})
            for _ in range(5): assert request('/api/auth/verify-otp',{'email':'attempts@example.com','code':'bad'})[0]==401
            assert request('/api/auth/verify-otp',{'email':'attempts@example.com','code':'000000'})[0]==401
            request('/api/auth/request-otp',{'email':'admin@ndorbit.co.nz'})
            wrong_password=request('/api/auth/verify-otp',{'email':'admin@ndorbit.co.nz','code':'000000','adminPassword':'incorrect'})
            wrong_code=request('/api/auth/verify-otp',{'email':'admin@ndorbit.co.nz','code':'bad','adminPassword':'test-admin-only'})
            assert wrong_password==wrong_code, 'No administrator password oracle'
            assert request('/api/auth/verify-otp',{'email':'admin@ndorbit.co.nz','code':'000000','adminPassword':'test-admin-only'})[0]==200
            print('PASS: administrator second step, one-time consumption, shared attempt budget and uniform credential errors')
            process.terminate();process.wait(timeout=10);start()
            assert request('/api/user/me',token=token)[1]['subscription']['planCode']=='test-plan'
            with closing(sqlite3.connect(database)) as db, db:
                assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
                assert db.execute('SELECT COUNT(*) FROM UsageEntries WHERE UserId=?',(user_id.upper(),)).fetchone()[0]==20
            print('PASS: restart-safe migration, membership/usage persistence and SQLite integrity')
        finally:
            if process and process.poll() is None: process.terminate();process.wait(timeout=10)
            log.close()


if __name__ == '__main__': main()

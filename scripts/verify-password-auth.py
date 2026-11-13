"""Exercise signup, password sign-in/reset, lockout and existing OTP accounts. No real emails."""
from contextlib import closing
import json,os,socket,sqlite3,subprocess,tempfile,time,urllib.request,urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def main():
 with tempfile.TemporaryDirectory(prefix='darvazeh-password-') as temporary:
  folder=Path(temporary);database=folder/'app.db'
  with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
  base=f'http://127.0.0.1:{port}'
  env={**os.environ,'ASPNETCORE_URLS':base,'FieldSense__AcsMock':'true','FieldSense__AcsConnectionString':'','FieldSense__AdminEmail':'admin@ndorbit.co.nz','FieldSense__AdminPassword':'test-owner-extra-password','FieldSense__JwtKey':'disposable-password-test-'+'x'*40,'ConnectionStrings__Default':'Data Source='+str(database),'Billing__StripeEnabled':'false'}
  log=(folder/'api.log').open('wb');process=None
  def call(path,body=None,token=None,method=None):
   if body is not None and path in ('/api/auth/password/signup','/api/auth/password/reset'):body={'confirmPassword':body.get('password'),**body}
   h={'Content-Type':'application/json'}
   if token:h['Authorization']='Bearer '+token
   request=urllib.request.Request(base+path,data=json.dumps(body).encode() if body is not None else None,headers=h,method=method or ('POST' if body is not None else 'GET'))
   try:
    with urllib.request.urlopen(request,timeout=30) as response:return response.status,json.load(response)
   except urllib.error.HTTPError as error:return error.code,json.loads(error.read() or '{}')
  def start():
   nonlocal process
   process=subprocess.Popen(['dotnet',str(ROOT/'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],cwd=ROOT/'server/FieldSense.Api',env=env,stdout=log,stderr=log)
   for _ in range(100):
    if process.poll() is not None:raise AssertionError((folder/'api.log').read_text()[-3000:])
    try:
     if call('/health')[0]==200:return
    except OSError:pass
    time.sleep(.1)
   raise AssertionError('startup timeout')
  def code(address):assert call('/api/auth/request-otp',{'email':address})[0]==200
  pwd='n7!Zq2#Rt'
  try:
   start();address='password-user@example.com';payload={'email':address,'password':pwd,'code':'000000'}
   for weak in ['short','password','Password1!','12345678','abcdabcd','password-user123!','n7!Zq2#R']:
    assert call('/api/auth/password/reset',{**payload,'password':weak})[0]==400
   assert call('/api/auth/password/reset',{**payload,'confirmPassword':'different'})[1]['code']=='password_mismatch'
   assert call('/api/auth/password/reset',{**payload,'confirmPassword':None})[1]['code']=='password_mismatch'
   status,created=call('/api/auth/password/signup',payload);assert status==200,(status,created)
   old=created['token'];uid=created['user']['id']
   assert created['user']['subscription']['messageLimit']==0
   me=call('/api/user/me',token=old)[1];assert me['emailVerified'] is False and not me['subscription']['active']
   assert call('/api/user/devices/pair',{'name':'blocked'},old)[1]['code']=='email_verification_required'
   assert call('/api/billing/checkout',{'planId':'00000000-0000-0000-0000-000000000000','currency':'USD','requestKey':'test'},old)[1]['code']=='email_verification_required'
   assert call('/api/user/verify-email',{'code':'000000'},old)[0]==401
   with closing(sqlite3.connect(database)) as db,db:
    db.execute("UPDATE Users SET CreatedAt=datetime('now','-5 days') WHERE Id=?",(uid.upper(),))
   assert call('/api/user/verify-email/request',{},old)[0]==200
   assert call('/api/user/verify-email',{'code':'000000'},old)[0]==200
   verified=call('/api/user/me',token=old)[1];assert verified['emailVerified'] and verified['subscription']['messageLimit']==20 and verified['subscription']['active']
   assert call('/api/user/verify-email',{'code':'123456'},old)[0]==200
   assert call('/api/user/me',token=old)[1]['subscription']['startsAt']==verified['subscription']['startsAt']
   assert created['user']['username'] is None
   assert call('/api/auth/password/signup',payload)[0]==409
   for identity in [address,address.upper()]:
    status,result=call('/api/auth/password/signin',{'identity':identity,'password':pwd});assert status==200,(status,result)
    assert result['user']['id']==uid
   assert 'passwordHash' not in call('/api/user/me',token=old)[1]
   with closing(sqlite3.connect(database)) as db:
    stored=db.execute('SELECT PasswordHash FROM Users WHERE Id=?',(uid.upper(),)).fetchone()[0];assert stored.startswith('$2') and pwd not in stored
   print('PASS: immediate signup, gated activation, single trial, username/email sign-in and BCrypt',flush=True)
   unknown=call('/api/auth/password/signin',{'identity':'no-such-user','password':'invalid'})
   for _ in range(5):
    denied=call('/api/auth/password/signin',{'identity':address,'password':'wrong'});assert denied==unknown
   assert call('/api/auth/password/signin',{'identity':address,'password':pwd})==unknown
   code(address);newpwd='replacement long passphrase 456'
   assert call('/api/auth/password/reset',{'email':address,'password':newpwd,'confirmPassword':'different','code':'000000'})[1]['code']=='password_mismatch'
   assert call('/api/auth/password/reset',{'email':address,'password':newpwd,'code':'wrong'})[0]==401
   status,reset=call('/api/auth/password/reset',{'email':address,'username':'new.username','password':newpwd,'code':'000000'});assert status==200,(status,reset)
   assert call('/api/user/me',token=old)[0]==401
   assert call('/api/user/me',token=reset['token'])[0]==200
   assert call('/api/auth/password/signin',{'identity':'new.username','password':pwd})[0]==401
   assert call('/api/auth/password/signin',{'identity':'new.username','password':newpwd})[0]==200
   assert call('/api/auth/password/reset',{'email':address,'password':pwd,'code':'000000'})[0]==401
   print('PASS: generic errors, five-attempt lockout, verified reset, single-use codes, old-session revocation',flush=True)
   process.terminate();process.wait(10);start()
   # A logged-in password holder can change it without email delivery; stolen
   # sessions cannot replace an existing password without knowing it.
   profile=reset['token'];changedpwd='profile long passphrase 789'
   assert call('/api/user/password',{'password':changedpwd,'confirmPassword':changedpwd},profile)[0]==401
   assert call('/api/user/password',{'password':changedpwd,'confirmPassword':'different','currentPassword':newpwd},profile)[0]==400
   status,changed=call('/api/user/password',{'password':changedpwd,'confirmPassword':changedpwd,'currentPassword':newpwd},profile);assert status==200,(status,changed)
   assert call('/api/user/me',token=profile)[0]==401
   newpwd=changedpwd
   # Pre-registration by somebody else must not retain credentials/settings after
   # the mailbox owner proves ownership through an independent sign-in.
   pre='preclaimed@example.com'
   status,untrusted=call('/api/auth/password/signup',{'email':pre,'password':pwd});assert status==200
   pending=untrusted['token']
   assert call('/api/user/settings',{'forwardEmailEnabled':True,'forwardEmailAddress':'untrusted@example.com'},pending,method='PUT')[0]==200
   code(pre);status,owner=call('/api/auth/verify-otp',{'email':pre,'code':'000000'});assert status==200,(status,owner)
   assert call('/api/user/me',token=pending)[0]==401
   owned=call('/api/user/me',token=owner['token'])[1]
   assert owned['emailVerified'] and not owned['hasPassword'] and not owned['forwardEmailEnabled'] and owned['forwardEmailAddress']==pre
   assert call('/api/auth/password/signin',{'identity':pre,'password':pwd})[0]==401
   import base64,hmac
   head,payloadpart,_=owner['token'].split('.')
   claims=json.loads(base64.urlsafe_b64decode(payloadpart+'='*(-len(payloadpart)%4)));claims['auth_time']=int(time.time())-901
   altered=base64.urlsafe_b64encode(json.dumps(claims).encode()).rstrip(b'=').decode();unsigned=head+'.'+altered
   stale=unsigned+'.'+base64.urlsafe_b64encode(hmac.digest(env['FieldSense__JwtKey'].encode(),unsigned.encode(),'sha256')).rstrip(b'=').decode()
   assert call('/api/user/password',{'password':pwd,'confirmPassword':pwd},stale)[1]['code']=='recent_signin_required'
   status,setup=call('/api/user/password',{'password':pwd,'confirmPassword':pwd},owner['token']);assert status==200,(status,setup)
   code(pre);assert call('/api/auth/verify-otp',{'email':pre,'code':'000000'})[0]==200
   assert call('/api/user/me',token=setup['token'])[1]['hasPassword']
   assert call('/api/auth/password/signin',{'identity':pre,'password':pwd})[0]==200
   print('PASS: profile change requires current password, recent verified first setup, preclaim takeover prevention and session revocation',flush=True)
   # Gmail dot/plus aliases share one free trial. Verification itself still works.
   for address,eligible in [('trial.alias@gmail.com',True),('trialalias+second@googlemail.com',False)]:
    status,signup=call('/api/auth/password/signup',{'email':address,'password':pwd});assert status==200,(status,signup)
    trialtoken=signup['token'];assert call('/api/user/verify-email/request',{},trialtoken)[0]==200
    assert call('/api/user/verify-email',{'code':'000000'},trialtoken)[0]==200
    me=call('/api/user/me',token=trialtoken)[1];assert me['emailVerified'] and me['subscription']['active']==eligible
    assert me['subscription']['messageLimit']==(20 if eligible else 0)
   print('PASS: Gmail aliases cannot obtain multiple free trials',flush=True)
   process.terminate();process.wait(10);start()
   legacy='existing-otp@example.com';code(legacy);status,otp=call('/api/auth/verify-otp',{'email':legacy,'code':'000000'});assert status==200
   assert call('/api/auth/password/signin',{'identity':legacy,'password':pwd})[0]==401
   code(legacy);status,configured=call('/api/auth/password/reset',{'email':legacy,'username':'existing.user','password':pwd,'code':'000000'});assert status==200
   assert configured['user']['id']==otp['user']['id']
   admin='admin@ndorbit.co.nz';code(admin)
   assert call('/api/auth/password/reset',{'email':admin,'password':pwd,'code':'000000'})[0]==401
   status,ar=call('/api/auth/password/reset',{'email':admin,'password':pwd,'code':'000000','adminPassword':'test-owner-extra-password'});assert status==200,(status,ar)
   assert call('/api/auth/password/signin',{'identity':admin,'password':pwd})[0]==200
   assert call('/api/admin/security',{'adminRequireOtp':True,'password':pwd},ar['token'],method='PUT')[0]==200
   code(admin)
   assert call('/api/auth/password/signin',{'identity':admin,'password':pwd,'adminPassword':'test-owner-extra-password'})[1]['code']=='admin_otp_required'
   assert call('/api/auth/password/signin',{'identity':admin,'password':pwd,'adminPassword':'test-owner-extra-password','code':'000000'})[0]==200
   print('PASS: existing OTP-account password setup preserves ID/history; optional administrator OTP and legacy OTP extra credential remain enforced',flush=True)
   process.terminate();process.wait(10);start()
   assert call('/api/auth/password/signin',{'identity':'new.username','password':newpwd})[0]==200
   with closing(sqlite3.connect(database)) as db:assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
   print('PASS: password migration and sign-in survive restart; database integrity',flush=True)
  finally:
   if process and process.poll() is None:process.terminate();process.wait(10)
   log.close()
if __name__=='__main__':main()

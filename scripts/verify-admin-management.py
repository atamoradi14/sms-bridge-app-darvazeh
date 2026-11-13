"""Real API, disposable database, mock email; never contacts production or sends SMS."""
import json,os,socket,sqlite3,subprocess,tempfile,time,urllib.request,urllib.error,uuid
from contextlib import closing
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def main():
 with tempfile.TemporaryDirectory(prefix='darvazeh-admin-') as tmp:
  folder=Path(tmp);dbfile=folder/'app.db'
  with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
  base=f'http://127.0.0.1:{port}';password='fixture owner password 456';memberpass='fixture member password 789'
  env={**os.environ,'ASPNETCORE_URLS':base,'FieldSense__AcsMock':'true','FieldSense__AcsConnectionString':'','FieldSense__AdminEmail':'admin@ndorbit.co.nz','FieldSense__AdminPassword':password,'FieldSense__JwtKey':'disposable-admin-tests-'+'x'*48,'ConnectionStrings__Default':'Data Source='+str(dbfile),'Billing__StripeEnabled':'false','Google__Enabled':'false'}
  log=(folder/'server.log').open('wb');process=None
  def call(path,data=None,token=None,method=None,device=None):
   headers={'Content-Type':'application/json'}
   if token:headers['Authorization']='Bearer '+token
   if device:headers['X-Device-Token']=device
   req=urllib.request.Request(base+path,data=json.dumps(data).encode() if data is not None else None,headers=headers,method=method or ('POST' if data is not None else 'GET'))
   try:
    with urllib.request.urlopen(req,timeout=30) as r:return r.status,json.load(r)
   except urllib.error.HTTPError as e:return e.code,json.loads(e.read() or '{}')
  def start():
   nonlocal process
   process=subprocess.Popen(['dotnet',str(ROOT/'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],cwd=ROOT/'server/FieldSense.Api',env=env,stdout=log,stderr=log)
   for _ in range(150):
    if process.poll() is not None:raise AssertionError((folder/'server.log').read_text()[-2500:])
    try:
     if call('/health')[0]==200:return
    except OSError:pass
    time.sleep(.1)
   raise AssertionError('Startup timeout')
  def signin(email,pw=memberpass,code=None):return call('/api/auth/password/signin',{'identity':email,'password':pw,'code':code})
  try:
   start();status,owner=signin('admin@ndorbit.co.nz',password);assert status==200,(status,owner)
   admin=owner['token'];ownerid=owner['user']['id']
   with closing(sqlite3.connect(dbfile)) as db:assert db.execute('SELECT COUNT(*) FROM OtpChallenges').fetchone()[0]==0
   assert call('/api/admin/security',token=admin)[1]['adminRequireOtp'] is False
   assert signin('admin@ndorbit.co.nz','wrong')[0]==401
   assert call('/api/admin/security',{'adminRequireOtp':True,'password':'wrong'},admin,'PUT')[0]==401
   assert call('/api/admin/security',{'adminRequireOtp':True,'password':password},admin,'PUT')[0]==200
   assert signin('admin@ndorbit.co.nz',password)[1]['code']=='admin_otp_required'
   assert call('/api/auth/request-otp',{'email':'admin@ndorbit.co.nz'})[0]==200
   status,owner=signin('admin@ndorbit.co.nz',password,'000000');assert status==200
   admin=owner['token']
   process.terminate();process.wait(10);start()
   assert signin('admin@ndorbit.co.nz',password)[1]['code']=='admin_otp_required'
   assert call('/api/admin/security',{'adminRequireOtp':False,'password':password},admin,'PUT')[0]==200
   assert signin('admin@ndorbit.co.nz',password)[0]==200
   print('PASS: admin password-only bootstrap, optional OTP enforcement, password confirmation and restart persistence',flush=True)
   for path,method,data in [(f'/api/admin/users/{ownerid}','DELETE',None),(f'/api/admin/users/{ownerid}/suspension','PUT',{'suspended':True}),(f'/api/admin/users/{ownerid}','PUT',{'email':'changed@example.com'})]:assert call(path,data,admin,method)[0]==403
   email='managed@example.com'
   status,created=call('/api/admin/users',{'email':email,'password':memberpass,'confirmPassword':memberpass},admin);assert status==200,(status,created)
   uid=created['id'];status,member=signin(email);assert status==200
   token=member['token'];assert call('/api/user/me',token=token)[1]['emailVerified'] is False
   assert call('/api/user/verify-email/request',{},token)[0]==200
   assert call('/api/user/verify-email',{'code':'000000'},token)[0]==200
   assert call('/api/admin/users',{'email':'forbidden@example.com','password':memberpass,'confirmPassword':memberpass},token)[0]==403
   assert call('/api/admin/security',{'adminRequireOtp':False,'password':password},token,'PUT')[0]==403
   status,device=call('/api/user/devices/pair',{'name':'Fixture phone'},token);assert status==200
   did=device['id'];dt=device['deviceToken']
   assert call('/api/device/heartbeat',{},device=dt)[0]==200
   assert 'deviceToken' not in call(f'/api/admin/users/{uid}/devices',token=admin)[1][0]
   assert call(f'/api/admin/users/{uid}/devices/{did}/connection',{'connected':False},admin,'PUT')[0]==200
   assert call('/api/device/heartbeat',{},device=dt)[0]==401
   assert call(f'/api/admin/users/{uid}/devices/{did}/connection',{'connected':True},admin,'PUT')[0]==200
   assert call('/api/device/heartbeat',{},device=dt)[0]==200
   print('PASS: owner protection, admin-only account creation and device pause/resume without exposing phone credentials',flush=True)
   with closing(sqlite3.connect(dbfile)) as db,db:
    for i in range(45):
     db.execute("INSERT INTO Messages(Id,UserId,Direction,PeerNumber,Body,Status,ClientMessageId,EmailForwarded,CreatedAt,EmailForwardStatus) VALUES(?,?,?,?,?,?,?,0,datetime('now'),'not_requested')",(str(uuid.uuid4()).upper(),uid.upper(),'inbound' if i%2==0 else 'outbound','+100'+str(i),'needle' if i<3 else 'ordinary', 'received' if i%2==0 else 'sent','history-'+str(i)))
   paged=call('/api/user/messages/page?page=1&pageSize=10',token=token)[1];assert len(paged['items'])==10 and paged['total']==45 and paged['totalPages']==5
   second=call('/api/user/messages/page?page=2&pageSize=10',token=token)[1];assert not {m['id'] for m in paged['items']} & {m['id'] for m in second['items']}
   assert call('/api/user/messages/page?search=needle',token=token)[1]['total']==3
   assert call('/api/user/messages/page?direction=inbound',token=token)[1]['total']==23
   assert call(f'/api/admin/users/{uid}/messages/page?search=needle',token=admin)[1]['total']==3
   assert call(f'/api/admin/users/{uid}/messages/page',token=token)[0]==403
   assert call('/api/user/messages/page',token=admin)[1]['total']==0
   message=paged['items'][0]['id'];usage=call('/api/user/me',token=token)[1]['subscription']['usedMessages']
   assert call('/api/user/messages/delete',{'ids':[message]},admin)[0]==404
   assert call('/api/user/messages/delete',{'ids':[message]},token)[0]==200
   assert call('/api/user/messages/page',token=token)[1]['total']==44
   assert call('/api/user/me',token=token)[1]['subscription']['usedMessages']==usage
   with closing(sqlite3.connect(dbfile)) as db:
    row=db.execute('SELECT Body,PeerNumber,DeletedAt,ClientMessageId FROM Messages WHERE Id=?',(message.upper(),)).fetchone();assert row[0]==row[1]=='' and row[2] and row[3]
   assert call(f'/api/admin/users/{uid}/messages/delete',{'ids':[second['items'][0]['id']]},admin)[0]==200
   print('PASS: scoped server search/direction filters, deterministic pagination, individual/bulk deletion and unchanged usage',flush=True)
   assert call('/api/user/sms/send',{'toNumber':'+100000','body':'queued test only'},token)[0]==200
   assert call('/api/user/messages/delete',{'ids':[call('/api/user/messages/page',token=token)[1]['items'][0]['id']]},token)[1]['code']=='message_in_progress'
   assert call(f'/api/admin/users/{uid}/suspension',{'suspended':True},admin,'PUT')[0]==200
   assert call('/api/user/me',token=token)[0]==401 and signin(email)[0]==401 and call('/api/device/heartbeat',{},device=dt)[0]==401
   assert call(f'/api/admin/users/{uid}/suspension',{'suspended':False},admin,'PUT')[0]==200
   status,member=signin(email);assert status==200;token=member['token']
   with closing(sqlite3.connect(dbfile)) as db:assert db.execute("SELECT COUNT(*) FROM OutboundJobs WHERE Status='queued'").fetchone()[0]==0
   assert call('/api/device/disconnect',{},device=dt)[0]==200
   assert call(f'/api/admin/users/{uid}/devices/{did}/connection',{'connected':True},admin,'PUT')[1]['code']=='approval_required'
   assert call(f'/api/admin/users/{uid}',{'email':'renamed@example.com','username':'renamed.user','password':'replacement member pass 456','confirmPassword':'replacement member pass 456'},admin,'PUT')[0]==200
   assert call('/api/user/me',token=token)[0]==401
   status,member=signin('renamed@example.com','replacement member pass 456');assert status==200;token=member['token']
   assert call('/api/user/me',token=token)[1]['emailVerified'] is False
   assert call(f'/api/admin/users/{uid}',token=admin,method='DELETE')[0]==200
   assert call('/api/user/me',token=token)[0]==401 and signin('renamed@example.com','replacement member pass 456')[0]==401
   assert uid not in [u['id'] for u in call('/api/admin/users',token=admin)[1]]
   with closing(sqlite3.connect(dbfile)) as db:
    assert db.execute('SELECT COUNT(*) FROM Messages WHERE UserId=? AND (Body<>? OR PeerNumber<>?)',(uid.upper(),'','')).fetchone()[0]==0
    assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
   print('PASS: suspension blocks sessions/phones/login and cancels queued jobs; revoked phones require approval; edit reverifies email; deletion revokes access and scrubs contents',flush=True)
   freshpass='fresh account password 789'
   status,fresh=call('/api/auth/password/signup',{'email':'renamed@example.com','username':'renamed.user','password':freshpass,'confirmPassword':freshpass});assert status==200,(status,fresh)
   assert fresh['user']['id']!=uid and not call('/api/user/me',token=fresh['token'])[1]['emailVerified']
   assert call('/api/user/messages/page',token=fresh['token'])[1]['total']==0
   assert signin('renamed.user',freshpass)[0]==200
   with closing(sqlite3.connect(dbfile)) as db:assert db.execute('SELECT DeletedAt FROM Users WHERE Id=?',(uid.upper(),)).fetchone()[0]
   process.terminate();process.wait(10);start()
   assert signin('renamed@example.com',freshpass)[0]==200
   print('PASS: deleted email/username can register anew; old account stays closed, new account owns no old history, and filtered indexes survive restart',flush=True)
   for attempt in range(2):
    assert call('/api/auth/request-otp',{'email':'otp-reregister@example.com'})[0]==200
    status,verified=call('/api/auth/verify-otp',{'email':'otp-reregister@example.com','code':'000000'});assert status==200,(status,verified)
    if attempt==0:
     oldid=verified['user']['id'];assert verified['user']['subscription']['active']
     assert call(f'/api/admin/users/{oldid}',token=admin,method='DELETE')[0]==200
    else:
     assert verified['user']['id']!=oldid and not verified['user']['subscription']['active'] and verified['user']['subscription']['messageLimit']==0
   print('PASS: email-code sign-in can create a fresh account after deletion without restoring the old trial',flush=True)


  finally:
   if process and process.poll() is None:process.terminate();process.wait(10)
   log.close()
if __name__=='__main__':main()

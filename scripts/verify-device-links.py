"""Real API/SQLite tests for browser-approved phones, health and email allowances. No external mail/SMS/payments."""
from contextlib import closing
import json,os,socket,sqlite3,subprocess,tempfile,time,urllib.request,urllib.error,uuid
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def main():
 with tempfile.TemporaryDirectory(prefix="darvazeh-phone-test-") as folder:
  folder=Path(folder);database=folder/'app.db'
  with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
  base=f'http://127.0.0.1:{port}'
  env={**os.environ,'ASPNETCORE_URLS':base,'FieldSense__AcsMock':'true','FieldSense__AcsConnectionString':'','FieldSense__AdminEmail':'admin@ndorbit.co.nz','FieldSense__AdminPassword':'phone-tests-only','FieldSense__JwtKey':'isolated-device-link-test-'+'x'*40,'ConnectionStrings__Default':'Data Source='+str(database),'Billing__StripeEnabled':'false'}
  log=(folder/'api.log').open('wb');process=None
  def call(path,body=None,token=None,headers=None,method=None):
   h={'Content-Type':'application/json',**(headers or {})}
   if token:h['Authorization']='Bearer '+token
   req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers=h,method=method or ('POST' if body is not None else 'GET'))
   try:
    with urllib.request.urlopen(req,timeout=30) as r:return r.status,json.load(r)
   except urllib.error.HTTPError as e:return e.code,json.loads(e.read() or '{}')
  def start():
   nonlocal process
   process=subprocess.Popen(['dotnet',str(ROOT/'server/FieldSense.Api/bin/Debug/net8.0/FieldSense.Api.dll')],cwd=ROOT/'server/FieldSense.Api',env=env,stdout=log,stderr=log)
   for _ in range(100):
    if process.poll() is not None:raise AssertionError((folder/'api.log').read_text()[-4000:])
    try:
     if call('/health')[0]==200:return
    except OSError:pass
    time.sleep(.1)
   raise AssertionError('startup timeout')
  def login(email):
   assert call('/api/auth/request-otp',{'email':email})[0]==200
   data={'email':email,'code':'000000'}
   if email=='admin@ndorbit.co.nz':data['adminPassword']='phone-tests-only'
   status,result=call('/api/auth/verify-otp',data);assert status==200
   return result['token'],result['user']['id']
  def request_link(email,installation=None,previous=None):
   body={'email':email,'installationId':installation or str(uuid.uuid4()),'deviceName':'Test Android','appVersion':'2.2.0','previousDeviceToken':previous}
   status,result=call('/api/device-links/request',body);assert status==200,(status,result)
   return result
  def poll(link):return call('/api/device-links/'+link['id']+'/status',headers={'X-Link-Secret':link['secret']})
  def approve(link,token):return call('/api/device-links/'+link['id']+'/decision',{'approve':True,'comparisonCode':link['comparisonCode']},token)
  def me(token):return call('/api/user/me',token=token)[1]
  try:
   start();admin,aid=login('admin@ndorbit.co.nz');owner,oid=login('phone-owner@example.com');other,xid=login('other-owner@example.com')
   plans=call('/api/admin/plans',token=admin)[1];silver=next(p for p in plans if p['code']=='silver');gold=next(p for p in plans if p['code']=='gold')
   assert (silver['priceUsdCents'],silver['emailForwardLimit'],gold['priceUsdCents'],gold['emailForwardLimit'])==(200,10,300,200)
   assert me(owner)['subscription']['messageLimit']==20
   unknown=request_link('not-registered@example.com')
   with closing(sqlite3.connect(database)) as db:assert db.execute("SELECT COUNT(*) FROM Users WHERE Email='not-registered@example.com'").fetchone()[0]==0
   installation=str(uuid.uuid4());link=request_link('phone-owner@example.com',installation)
   assert call('/api/device-links/'+link['id']+'/status')[0]==404
   assert 'device' not in poll(link)[1] and poll(link)[1]['status']=='pending'
   assert approve(link,other)[0]==404
   assert call('/api/device-links/'+link['id']+'/decision',{'approve':True,'comparisonCode':'wrong'},owner)[0]==400
   assert len(call('/api/device-links/pending',token=owner)[1])==1
   assert approve(link,owner)[0]==200 and approve(link,owner)[0]==409
   device=poll(link)[1]['device'];dh={'X-Device-Token':device['deviceToken'],'X-Battery-Level':'15','X-Is-Charging':'false','X-Network-Type':'wifi','X-Network-Validated':'true','X-Sms-Permissions':'true','X-Battery-Exempt':'true'}
   assert call('/api/device/heartbeat',{},headers=dh)[0]==200
   assert me(owner)['devices'][0]['batteryLevel']==15
   print('PASS: no phone OTP, no account creation by public request, secret polling, owner approval and replay boundaries',flush=True)
   assert call('/api/admin/users/'+oid+'/subscription',{'planId':silver['id']},admin,method='PUT')[0]==200
   assert call('/api/user/settings',{'forwardEmailEnabled':True,'forwardEmailAddress':'phone-owner@example.com'},owner,method='PUT')[0]==200
   for i in range(12):
    status,msg=call('/api/device/inbound-sms',{'from':'+1000','body':'test','clientMessageId':'email-'+str(i)},headers=dh);assert status==200,(status,msg)
    assert msg['emailForwarded']==(i<10)
   sub=me(owner)['subscription'];assert sub['usedMessages']==12 and sub['usedEmailForwards']==10 and sub['remainingEmailForwards']==0
   assert call('/api/device/inbound-sms',{'from':'+1000','body':'test','clientMessageId':'email-0'},headers=dh)[0]==200
   assert me(owner)['subscription']['usedEmailForwards']==10
   msgs=call('/api/user/messages',token=owner)[1];assert all(m['batteryLevel']==15 for m in msgs)
   assert sum(m['emailForwardStatus']=='limit_reached' for m in msgs)==2
   status,out=call('/api/user/sms/send',{'toNumber':'+1000','body':'test outbound'},owner);assert status==200
   dh['X-Battery-Level']='25'
   assert call('/api/device/outbound/'+out['jobId']+'/result',{'status':'sent'},headers=dh)[0]==200
   assert next(m for m in call('/api/user/messages',token=owner)[1] if m['direction']=='outbound')['batteryLevel']==25
   assert call('/api/admin/users/'+oid+'/subscription',{'planId':gold['id']},admin,method='PUT')[0]==200
   assert me(owner)['subscription']['emailForwardLimit']==200
   assert call('/api/device/inbound-sms',{'from':'+1000','body':'gold','clientMessageId':'gold'},headers=dh)[1]['emailForwarded']
   print('PASS: Silver 10 forwards, Gold 200, independent SMS allowance, retry dedup and battery snapshots on incoming/outgoing messages',flush=True)
   # Force stale check-in while preserving low-battery metadata; monitor runs every 30s.
   with closing(sqlite3.connect(database)) as db,db:db.execute("UPDATE Devices SET LastSeenAt='2000-01-01 00:00:00',BatteryLevel=5,IsCharging=0,SmsPermissionsGranted=0 WHERE Id=?",(device['id'].upper(),))
   for _ in range(40):
    alerts=call('/api/user/alerts',token=owner)[1];kinds={a['kind'] for a in alerts if not a['resolvedAt']}
    if {'offline','battery_critical','sms_permission'}<=kinds:break
    time.sleep(1)
   assert {'offline','battery_critical','sms_permission'}<=kinds,kinds
   assert not call('/api/user/alerts',token=other)[1]
   a=next(a for a in alerts if a['kind']=='offline');assert call('/api/user/alerts/'+a['id']+'/read',{},other)[0]==404
   assert call('/api/user/alerts/'+a['id']+'/read',{},owner)[0]==200
   dh.update({'X-Battery-Level':'80','X-Is-Charging':'true'})
   assert call('/api/device/heartbeat',{},headers=dh)[0]==200
   for _ in range(40):
    alerts=call('/api/user/alerts',token=owner)[1]
    if all(a['resolvedAt'] for a in alerts):break
    time.sleep(1)
   assert all(a['resolvedAt'] for a in alerts)
   print('PASS: persistent offline/critical-battery/permission alerts, scoped dismissal and recovery',flush=True)
   # Switch account: old token is revoked before the second account approves.
   switch=request_link('other-owner@example.com',installation,device['deviceToken'])
   assert call('/api/device/heartbeat',{},headers=dh)[0]==401
   assert poll(switch)[1]['status']=='pending' and approve(switch,owner)[0]==404
   assert approve(switch,other)[0]==200
   second=poll(switch)[1]['device'];assert call('/api/device/heartbeat',{},headers={'X-Device-Token':second['deviceToken']})[0]==200
   assert any(d['id']==second['id'] for d in me(other)['devices'])
   retry=request_link('other-owner@example.com',installation,second['deviceToken']);assert approve(retry,other)[0]==200
   assert poll(switch)[1]['status']=='revoked'
   expired=request_link('phone-owner@example.com')
   with closing(sqlite3.connect(database)) as db,db:db.execute("UPDATE DeviceLinks SET ExpiresAt='2000-01-01 00:00:00' WHERE Id=?",(expired['id'].upper(),))
   assert poll(expired)[1]['status']=='expired' and approve(expired,owner)[0]==409
   cancel=request_link('phone-owner@example.com');assert call('/api/device-links/'+cancel['id']+'/cancel',{},headers={'X-Link-Secret':cancel['secret']})[0]==200
   assert poll(cancel)[1]['status']=='cancelled'
   with closing(sqlite3.connect(database)) as db:assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
   process.terminate();process.wait(10);start()
   assert me(owner)['subscription']['usedMessages']==1 and me(owner)['subscription']['usedEmailForwards']==1
   assert next(p for p in call('/api/admin/plans',token=admin)[1] if p['code']=='silver')['priceUsdCents']==200
   print('PASS: account switch revokes old credentials; second approval required; old polling claims revoked; expiry/cancel/restart/integrity',flush=True)
  finally:
   if process and process.poll() is None:process.terminate();process.wait(10)
   log.close()
if __name__=='__main__':main()

package com.fieldsense.smsforward;
import android.Manifest;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.os.BatteryManager;
import android.os.PowerManager;
import androidx.core.content.ContextCompat;
import org.json.JSONObject;
public final class DeviceHealth {
    public static JSONObject snapshot(Context c) {
        JSONObject j=new JSONObject();
        try {
            Intent battery=c.registerReceiver(null,new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
            int level=battery==null?-1:battery.getIntExtra(BatteryManager.EXTRA_LEVEL,-1);
            int scale=battery==null?100:battery.getIntExtra(BatteryManager.EXTRA_SCALE,100);
            if(level>=0 && scale>0)j.put("batteryLevel",Math.round(level*100f/scale));
            int status=battery==null?-1:battery.getIntExtra(BatteryManager.EXTRA_STATUS,-1);
            j.put("isCharging",status==BatteryManager.BATTERY_STATUS_CHARGING || status==BatteryManager.BATTERY_STATUS_FULL);
            ConnectivityManager cm=(ConnectivityManager)c.getSystemService(Context.CONNECTIVITY_SERVICE);
            NetworkCapabilities net=cm==null?null:cm.getNetworkCapabilities(cm.getActiveNetwork());
            String type=net==null?"offline":net.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)?"wifi":net.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)?"cellular":net.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)?"ethernet":net.hasTransport(NetworkCapabilities.TRANSPORT_VPN)?"vpn":"other";
            j.put("networkType",type);j.put("networkValidated",net!=null && net.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED));
            j.put("smsPermissionsGranted",ContextCompat.checkSelfPermission(c,Manifest.permission.RECEIVE_SMS)==PackageManager.PERMISSION_GRANTED && ContextCompat.checkSelfPermission(c,Manifest.permission.SEND_SMS)==PackageManager.PERMISSION_GRANTED);
            PowerManager pm=(PowerManager)c.getSystemService(Context.POWER_SERVICE);
            j.put("batteryOptimizationExempt",pm!=null && pm.isIgnoringBatteryOptimizations(c.getPackageName()));
        } catch(Exception ignored) { }
        return j;
    }
    public static String summary(Context c) {
        JSONObject j=snapshot(c);
        return "باتری "+(j.has("batteryLevel")?j.optInt("batteryLevel")+"٪":"نامشخص")+(j.optBoolean("isCharging")?" · در حال شارژ":"")+" · "+j.optString("networkType","نامشخص");
    }
}

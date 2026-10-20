package ru.kodvsego.smartdns;

import android.app.PendingIntent;
import android.appwidget.*;
import android.content.*;
import android.net.Uri;
import android.widget.RemoteViews;

public class DnsWidget extends AppWidgetProvider {
    public static final String CONNECT="ru.kodvsego.smartdns.WIDGET_CONNECT";
    @Override public void onUpdate(Context context,AppWidgetManager manager,int[] ids){for(int id:ids)update(context,manager,id);}
    @Override public void onEnabled(Context context){updateAll(context);}
    @Override public void onReceive(Context context,Intent intent){
        super.onReceive(context,intent);
        if(Intent.ACTION_MY_PACKAGE_REPLACED.equals(intent.getAction()))updateAll(context);
    }
    static void updateAll(Context context) {
        AppWidgetManager manager=AppWidgetManager.getInstance(context);
        for(Class<?> type:new Class<?>[]{DnsWidget.class,CompactDnsWidget.class})
            for(int id:manager.getAppWidgetIds(new ComponentName(context,type)))update(context,manager,id);
    }
    static RemoteViews views(Context context,int id,int layout) {
        RemoteViews views=new RemoteViews(context.getPackageName(),layout);
        boolean running=DnsVpnService.active||DnsVpnService.connecting;
        int selected=context.getSharedPreferences("smartdns",Context.MODE_PRIVATE).getInt("provider",0);
        if(selected<0||selected>=Provider.ALL.length)selected=0;
        DnsConfig current=DnsVpnService.current;
        String provider=Provider.at(current==null?selected:current.provider).name;
        views.setTextViewText(R.id.widget_status,running?(DnsVpnService.connecting?"Подключаем…":"DNS включён"):"DNS выключен");
        views.setTextViewText(R.id.widget_provider,provider+(current!=null&&current.encrypted?" · DoH":""));
        boolean compact=layout==R.layout.widget_dns_compact;
        views.setTextViewText(R.id.widget_toggle,compact?(running?"Выкл":"Вкл"):(running?"Выключить":"Включить"));
        Intent open=new Intent(context,MainActivity.class).setData(Uri.parse("smartdns://widget/"+id+"/open"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK|Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP);
        views.setOnClickPendingIntent(R.id.widget_root,PendingIntent.getActivity(context,id*3,open,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE));
        Intent toggle;
        PendingIntent action;
        if(running) {
            toggle=new Intent(context,DnsVpnService.class).setAction(DnsVpnService.STOP).setData(Uri.parse("smartdns://widget/"+id+"/stop"));
            action=PendingIntent.getService(context,id*3+1,toggle,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        }else {
            toggle=new Intent(open).setAction(CONNECT).setData(Uri.parse("smartdns://widget/"+id+"/connect"));
            action=PendingIntent.getActivity(context,id*3+2,toggle,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
        }
        views.setOnClickPendingIntent(R.id.widget_toggle,action);
        return views;
    }
    private static void update(Context context,AppWidgetManager manager,int id) {
        AppWidgetProviderInfo info=manager.getAppWidgetInfo(id);
        int layout=info==null?R.layout.widget_dns:info.initialLayout;
        manager.updateAppWidget(id,views(context,id,layout));
    }
}

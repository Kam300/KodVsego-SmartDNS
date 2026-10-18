package ru.kodvsego.smartdns;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.*;
import android.net.VpnService;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final ExecutorService tests=Executors.newSingleThreadExecutor();
    private android.content.SharedPreferences prefs;
    private boolean dark,rendering,testing;private int selected,agSelected;
    private LinearLayout content,serviceRow;private TextView state,addresses,probe;
    private Button connect,test;private Switch encrypted,ag;private Spinner providers,agProvider;
    private int ink,muted,accent,card;
    private DnsConfig pending;
    private Future<?> runningTest;private int testGeneration;
    private final Runnable refresh=new Runnable(){public void run(){updateState();handler.postDelayed(this,750);}};
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);prefs=getSharedPreferences("smartdns",MODE_PRIVATE);
        selected=prefs.getInt("provider",0);if(selected<0||selected>=Provider.ALL.length)selected=0;
        agSelected=prefs.getInt("agProvider",0);if(agSelected<0||agSelected>=Provider.AG_PROVIDERS.length)agSelected=0;
        dark=prefs.getBoolean("dark",false);render();
    }
    private int dp(float n){return Math.round(n*getResources().getDisplayMetrics().density);}
    private GradientDrawable shape(int color,int radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));return d;}
    private TextView text(String value,int size,int color,boolean bold) {
        TextView v=new TextView(this);v.setText(value);v.setTextSize(size);v.setTextColor(color);
        if(bold)v.setTypeface(Typeface.DEFAULT,Typeface.BOLD);v.setPadding(0,dp(5),0,dp(5));return v;
    }
    private Button button(String label){Button b=new Button(this);b.setText(label);b.setTextColor(Color.WHITE);b.setAllCaps(false);b.setBackgroundTintList(ColorStateList.valueOf(accent));b.setMinHeight(dp(48));return b;}
    private void space(int height){Space s=new Space(this);content.addView(s,new LinearLayout.LayoutParams(1,dp(height)));}
    private LinearLayout box(){LinearLayout b=new LinearLayout(this);b.setOrientation(LinearLayout.VERTICAL);b.setPadding(dp(16),dp(12),dp(16),dp(12));b.setBackground(shape(card,20));content.addView(b,new LinearLayout.LayoutParams(-1,-2));return b;}
    private void render() {
        rendering=true;
        ink=Color.parseColor(dark?"#EBF3F6":"#151D22");muted=Color.parseColor(dark?"#A0B8C2":"#52666F");
        accent=Color.parseColor(dark?"#008EAA":"#007F9F");card=Color.parseColor(dark?"#17262F":"#F0F7F9");
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);
        scroll.setBackground(new GradientDrawable(GradientDrawable.Orientation.TL_BR,new int[]{Color.parseColor(dark?"#0F181E":"#FCFDFE"),Color.parseColor(dark?"#15343C":"#DDF5FA")}));
        content=new LinearLayout(this);content.setOrientation(LinearLayout.VERTICAL);content.setPadding(dp(24),dp(20),dp(24),dp(24));scroll.addView(content);
        setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((view,insets)->{
            content.setPadding(dp(24),dp(20)+insets.getSystemWindowInsetTop(),dp(24),dp(24)+insets.getSystemWindowInsetBottom());return insets;
        });scroll.requestApplyInsets();
        getWindow().setStatusBarColor(Color.TRANSPARENT);getWindow().setNavigationBarColor(card);
        scroll.setSystemUiVisibility(dark?0:View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR);
        LinearLayout top=new LinearLayout(this);top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(text("SMART DNS · Android",13,accent,true),new LinearLayout.LayoutParams(0,-2,1));
        Button theme=button(dark?"☀":"☾");theme.setContentDescription("Переключить тему");top.addView(theme,new LinearLayout.LayoutParams(dp(64),dp(48)));
        theme.setOnClickListener(v->{dark=!dark;prefs.edit().putBoolean("dark",dark).apply();render();});content.addView(top);
        space(14);content.addView(text("Доступ\nк сервисам",36,ink,true));space(18);
        connect=button("ВКЛЮЧИТЬ DNS");connect.setTextSize(16);connect.setBackground(shape(accent,90));
        LinearLayout.LayoutParams circle=new LinearLayout.LayoutParams(dp(172),dp(172));circle.gravity=Gravity.CENTER_HORIZONTAL;content.addView(connect,circle);
        connect.setOnClickListener(v->{if(DnsVpnService.active||DnsVpnService.connecting)stopService(new Intent(this,DnsVpnService.class));else requestConnect();});
        space(14);state=text("DNS выключен",19,ink,true);state.setGravity(Gravity.CENTER);state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);content.addView(state);
        content.addView(text("После смены DNS полностью перезапустите нужное приложение.",13,muted,false));space(12);
        LinearLayout selection=box();selection.addView(text("Провайдер DNS",14,accent,true));providers=new Spinner(this);
        String[] names=new String[Provider.ALL.length];for(int i=0;i<names.length;i++)names[i]=Provider.ALL[i].name;
        providers.setAdapter(adapter(names));providers.setSelection(selected);selection.addView(providers);
        addresses=text("",13,ink,false);selection.addView(addresses);serviceRow=new LinearLayout(this);serviceRow.setOrientation(LinearLayout.VERTICAL);selection.addView(serviceRow);
        space(12);LinearLayout settings=box();
        encrypted=new Switch(this);encrypted.setText(R.string.encrypted_dns);encrypted.setTextColor(ink);encrypted.setMinHeight(dp(48));settings.addView(encrypted);
        encrypted.setChecked(prefs.getBoolean("encrypted",false)||Provider.at(selected).encryptedOnly);
        settings.addView(text("DoH шифрует DNS-запросы. При ошибке перехода на обычный DNS нет.",12,muted,false));
        ag=new Switch(this);ag.setText(R.string.antigravity_dns);ag.setTextColor(ink);ag.setMinHeight(dp(48));settings.addView(ag);ag.setChecked(prefs.getBoolean("ag",false));
        agProvider=new Spinner(this);agProvider.setAdapter(adapter(new String[]{"GeoHide","GeoHide EU","Bezmezhau"}));agProvider.setSelection(agSelected);settings.addView(agProvider);
        settings.addView(text("Отдельный провайдер только для двух API-доменов Antigravity. Доступ и исправление ошибки 400 не гарантируются.",12,muted,false));
        space(12);test=button(testing?"Остановить тест":"Тест DNS");content.addView(test);test.setOnClickListener(v->testDns());
        probe=text("Тест не меняет DNS. Проверяется ответ провайдера, а не доступ к аккаунту или сайту.",13,muted,false);content.addView(probe);
        space(12);LinearLayout info=box();info.addView(text("Как работает на телефоне",15,ink,true));
        info.addView(text("Без root. Android запросит разрешение VPN: локальный туннель обрабатывает только DNS. Сайты и приложения подключаются напрямую. Другой VPN одновременно использовать нельзя.\n\nСобственный Secure DNS браузера и DNS внутри приложений могут обходить настройку. Строгий частный DNS Android нужно переключить вручную. Не включайте блокировку соединений вне VPN.",13,muted,false));
        Button privateDns=button("Сетевые настройки Android");info.addView(privateDns);privateDns.setOnClickListener(v->{try{startActivity(new Intent(Settings.ACTION_WIRELESS_SETTINGS));}catch(ActivityNotFoundException ignored){}});
        space(14);content.addView(text("Код Всего · Android 1.0.0",13,accent,true));
        Button channel=button("Telegram · TotalC0de ↗");content.addView(channel);channel.setOnClickListener(v->startActivity(new Intent(Intent.ACTION_VIEW,android.net.Uri.parse("https://t.me/TotalC0de"))));
        providers.setOnItemSelectedListener(new SimpleSelection(){public void selected(int position){if(rendering)return;selected=position;updateProvider();save();}});
        agProvider.setOnItemSelectedListener(new SimpleSelection(){public void selected(int position){if(rendering)return;agSelected=position;save();}});
        encrypted.setOnCheckedChangeListener((b,on)->{if(!rendering){save();updateProvider();}});
        ag.setOnCheckedChangeListener((b,on)->{agProvider.setEnabled(on&&!DnsVpnService.active&&!DnsVpnService.connecting);if(!rendering)save();});
        updateProvider();rendering=false;updateState();
    }
    private ArrayAdapter<String> adapter(String[] names){return new ArrayAdapter<String>(this,android.R.layout.simple_spinner_dropdown_item,names){
        @Override public View getView(int p,View v,android.view.ViewGroup parent){TextView t=(TextView)super.getView(p,v,parent);t.setTextColor(ink);t.setMinHeight(dp(48));t.setGravity(Gravity.CENTER_VERTICAL);return t;}
        @Override public View getDropDownView(int p,View v,android.view.ViewGroup parent){TextView t=(TextView)super.getDropDownView(p,v,parent);t.setTextColor(Color.rgb(21,29,34));t.setBackgroundColor(Color.WHITE);t.setMinHeight(dp(48));return t;}
    };}
    private abstract static class SimpleSelection implements android.widget.AdapterView.OnItemSelectedListener {
        public abstract void selected(int position);public void onItemSelected(AdapterView<?> parent,View view,int position,long id){selected(position);}public void onNothingSelected(AdapterView<?> parent){}
    }
    private void updateProvider() {
        Provider p=Provider.at(selected);
        if(p.encryptedOnly)encrypted.setChecked(true);else if(p.doh==null)encrypted.setChecked(false);
        boolean locked=DnsVpnService.active||DnsVpnService.connecting;
        encrypted.setEnabled(!locked&&p.doh!=null&&!p.encryptedOnly);
        addresses.setText(encrypted.isChecked()?"DoH: "+p.doh:"DNS: "+android.text.TextUtils.join(" / ",p.servers));
        serviceRow.removeAllViews();LinearLayout row=null;
        for(int i=0;i<p.services.length;i++) {
            if(i%2==0){row=new LinearLayout(this);serviceRow.addView(row);}
            LinearLayout item=new LinearLayout(this);item.setGravity(Gravity.CENTER_VERTICAL);item.setPadding(0,dp(6),dp(8),dp(6));
            ImageView icon=new ImageView(this);icon.setContentDescription(p.services[i]);
            try(InputStream stream=getAssets().open(p.services[i].toLowerCase(java.util.Locale.ROOT)+".png")){icon.setImageDrawable(Drawable.createFromStream(stream,null));}catch(IOException ignored){}
            item.addView(icon,new LinearLayout.LayoutParams(dp(24),dp(24)));TextView label=text(p.services[i],13,ink,false);label.setPadding(dp(7),0,0,0);item.addView(label);
            row.addView(item,new LinearLayout.LayoutParams(0,-2,1));
        }
        if(selected==3)serviceRow.addView(text("Только Xbox Live · нейросети не поддерживаются",12,muted,false));
        if(selected==5)serviceRow.addView(text("Только DoH · DNS-ответ не гарантирует доступ к Gemini",12,muted,false));
    }
    private void save(){prefs.edit().putInt("provider",selected).putBoolean("encrypted",encrypted.isChecked()).putBoolean("ag",ag.isChecked()).putInt("agProvider",agSelected).apply();}
    private DnsConfig config(){return new DnsConfig(selected,encrypted.isChecked(),ag.isChecked()?Provider.AG_PROVIDERS[agSelected]:-1);}
    private void updateState() {
        if(state==null)return;boolean locked=DnsVpnService.active||DnsVpnService.connecting;
        if(!android.text.TextUtils.equals(state.getText(),DnsVpnService.status))state.setText(DnsVpnService.status);
        connect.setText(locked?"ВЫКЛЮЧИТЬ DNS":"ВКЛЮЧИТЬ DNS");
        providers.setEnabled(!locked);ag.setEnabled(!locked);agProvider.setEnabled(!locked&&ag.isChecked());
        encrypted.setEnabled(!locked&&Provider.at(selected).doh!=null&&!Provider.at(selected).encryptedOnly);
        test.setEnabled(!DnsVpnService.connecting);
    }
    private void requestConnect() {
        try{pending=config();PhysicalNetwork.checkPrivateDns(this,PhysicalNetwork.find(this));}
        catch(Exception e){message("Не удалось подключиться",e.getMessage());return;}
        if(PhysicalNetwork.otherVpn(this))new AlertDialog.Builder(this).setTitle("Другой VPN включён")
            .setMessage("Android отключит другое VPN-подключение при включении Smart DNS. Продолжить?").setNegativeButton("Отмена",null).setPositiveButton("Продолжить",(d,w)->prepareVpn()).show();
        else prepareVpn();
    }
    private void prepareVpn() {Intent permission=VpnService.prepare(this);if(permission==null)startDns();else startActivityForResult(permission,100);}
    private void startDns() {
        if(pending==null)return;
        if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},101);
        Intent start=new Intent(this,DnsVpnService.class).setAction(DnsVpnService.START).putExtra("provider",pending.provider).putExtra("encrypted",pending.encrypted).putExtra("antigravity",pending.antigravity);
        try{startForegroundService(start);}catch(Exception e){message("Ошибка подключения",e.getMessage());}pending=null;
    }
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(request==100){if(result==RESULT_OK){try{if(pending==null)pending=config();startDns();}catch(Exception e){message("Ошибка настройки",e.getMessage());}}else{pending=null;message("Подключение отменено","Разрешение VPN не предоставлено. DNS не изменён.");}}}
    private void message(String title,String body){if(!isFinishing()&&!isDestroyed())new AlertDialog.Builder(this).setTitle(title).setMessage(body).setPositiveButton("Понятно",null).show();}
    private void testDns() {
        if(testing){testGeneration++;if(runningTest!=null)runningTest.cancel(true);testing=false;test.setText(R.string.test_dns);probe.setText(R.string.test_stopped);updateState();return;}
        final DnsConfig config;try{config=config();}catch(Exception e){message("Ошибка настройки",e.getMessage());return;}
        testing=true;test.setText("Остановить тест");updateState();final int generation=++testGeneration;
        runningTest=tests.submit(()->{
            StringBuilder report=new StringBuilder("Проверка "+Provider.at(config.provider).name+(config.encrypted?" · DoH":" · UDP/TCP")+"\n\n");
            try(UpstreamResolver resolver=new UpstreamResolver(this,null)) {
                PhysicalNetwork.checkPrivateDns(this,PhysicalNetwork.find(this));
                for(String host:Provider.at(config.provider).testHosts()) {
                    if(Thread.currentThread().isInterrupted())break;
                    Provider target=config.forHost(host);String[] targets=config.encrypted?new String[]{target.doh}:target.servers;
                    for(String server:targets){
                        if(Thread.currentThread().isInterrupted())break;
                        long began=System.nanoTime();
                        try{byte[] answer=resolver.probe(DnsWire.query(host),target,config.encrypted,server);DnsWire.requireAddress(answer);
                            report.append("✓ ").append(host).append(" — ").append((System.nanoTime()-began)/1000000).append(" мс");
                        }catch(Exception e){report.append("✕ ").append(host).append(" — ").append(e.getMessage());}
                        report.append(" [").append(server).append("]\n");
                    }
                }
            }catch(Exception e){report.append(e.getMessage());}
            report.append("\nDNS-ответ не доказывает доступ к сайту, аккаунту или отсутствие региональной ошибки. Настройки не изменены.");
            handler.post(()->{if(isDestroyed()||generation!=testGeneration)return;testing=false;test.setText(R.string.test_dns);probe.setText(report.toString());updateState();});
        });
    }
    @Override protected void onResume(){super.onResume();handler.removeCallbacks(refresh);handler.post(refresh);}
    @Override protected void onPause(){handler.removeCallbacks(refresh);super.onPause();}
    @Override protected void onDestroy(){handler.removeCallbacksAndMessages(null);tests.shutdownNow();super.onDestroy();}
}

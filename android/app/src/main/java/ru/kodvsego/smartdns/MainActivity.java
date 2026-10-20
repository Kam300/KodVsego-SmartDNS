package ru.kodvsego.smartdns;

import android.Manifest;
import android.app.*;
import android.appwidget.AppWidgetManager;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.*;
import android.animation.ValueAnimator;
import android.net.VpnService;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.util.concurrent.*;
import java.util.HashMap;
import java.util.Map;

public final class MainActivity extends Activity {
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final ExecutorService tests=Executors.newSingleThreadExecutor();
    private android.content.SharedPreferences prefs;
    private boolean dark,rendering,testing,resumed;private int selected;
    private LinearLayout content,serviceRow;private TextView state,addresses,probe;
    private PowerButton connect;private Button test;private Switch encrypted;private Spinner providers;
    private int ink,muted,accent,card;
    private DnsConfig pending;
    private Future<?> runningTest;private int testGeneration;
    private Typeface heading,body,bold;
    private final Map<String,Drawable.ConstantState> iconCache=new HashMap<>();
    private String lastUiState,providerContent;
    private boolean updatingProvider;
    private final Runnable refresh=new Runnable(){public void run(){updateState();handler.postDelayed(this,1000);}};
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);prefs=getSharedPreferences("smartdns",MODE_PRIVATE);
        selected=prefs.getInt("provider",0);if(selected<0||selected>=Provider.ALL.length)selected=0;
        prefs.edit().remove("ag").remove("agProvider").apply();
        heading=font("Unbounded-ExtraBold.ttf",Typeface.DEFAULT_BOLD);body=font("Manrope-Medium.ttf",Typeface.DEFAULT);bold=font("Manrope-Bold.ttf",Typeface.DEFAULT_BOLD);
        dark=prefs.getBoolean("dark",true);render();
        if(saved==null)handleWidgetIntent();
    }
    private Typeface font(String name,Typeface fallback){try{return Typeface.createFromAsset(getAssets(),"fonts/"+name);}catch(RuntimeException e){return fallback;}}
    private int dp(float n){return Math.round(n*getResources().getDisplayMetrics().density);}
    private GradientDrawable shape(int color,int radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));return d;}
    private TextView text(String value,int size,int color,boolean bold) {
        TextView v=new TextView(this);v.setText(value);v.setTextSize(size);v.setTextColor(color);
        v.setTypeface(bold?this.bold:body);v.setPadding(0,dp(5),0,dp(5));return v;
    }
    private Button button(String label){Button b=new Button(this);b.setText(label);b.setTypeface(bold);b.setTextColor(dark?Color.rgb(21,29,34):Color.WHITE);b.setAllCaps(false);b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x226F53FF),shape(accent,14),null));b.setMinHeight(dp(48));return b;}
    private void space(int height){Space s=new Space(this);content.addView(s,new LinearLayout.LayoutParams(1,dp(height)));}
    private LinearLayout box(){LinearLayout b=new LinearLayout(this);b.setOrientation(LinearLayout.VERTICAL);b.setPadding(dp(16),dp(12),dp(16),dp(12));GradientDrawable background=shape(card,20);background.setStroke(dp(1),Color.parseColor(dark?"#2B283B":"#DED9EF"));b.setBackground(background);content.addView(b,new LinearLayout.LayoutParams(-1,-2));return b;}
    private void render() {
        setTheme(dark?R.style.AppThemeDark:R.style.AppTheme);
        rendering=true;
        lastUiState=null;providerContent=null;
        ink=Color.parseColor(dark?"#F7F6FC":"#151D22");muted=Color.parseColor(dark?"#A8A4BC":"#68637B");
        accent=Color.parseColor(dark?"#02A5CF":"#007F9F");card=Color.parseColor(dark?"#12121B":"#F1EFFA");
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);
        scroll.setBackground(new GradientDrawable(GradientDrawable.Orientation.TR_BL,new int[]{Color.parseColor(dark?"#27203F":"#F0EAFF"),Color.parseColor(dark?"#08080D":"#FAF9FF"),Color.parseColor(dark?"#10192B":"#EDF2FF")}));
        content=new LinearLayout(this);content.setOrientation(LinearLayout.VERTICAL);content.setPadding(dp(24),dp(20),dp(24),dp(24));scroll.addView(content);
        setContentView(scroll);
        scroll.setClipToPadding(true);
        scroll.setOnApplyWindowInsetsListener((view,insets)->{applyInsets(scroll,insets);return insets;});
        if(Build.VERSION.SDK_INT>=30)getWindow().setDecorFitsSystemWindows(false);
        getWindow().setStatusBarColor(Color.TRANSPARENT);getWindow().setNavigationBarColor(card);
        scroll.setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_STABLE|View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN|View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION|
            (dark?0:View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR));
        scroll.requestApplyInsets();
        LinearLayout top=new LinearLayout(this);top.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(text("SMART DNS · Android",13,accent,true),new LinearLayout.LayoutParams(0,-2,1));
        Button theme=button(dark?"☀":"☾");theme.setContentDescription("Переключить тему");top.addView(theme,new LinearLayout.LayoutParams(dp(64),dp(48)));
        theme.setOnClickListener(v->{dark=!dark;prefs.edit().putBoolean("dark",dark).apply();render();});content.addView(top);
        space(14);TextView title=text("Доступ",30,ink,true);title.setTypeface(heading);content.addView(title);
        TextView highlight=text("к сервисам",28,Color.rgb(21,29,34),true);highlight.setTypeface(heading);highlight.setPadding(dp(8),0,dp(8),dp(4));highlight.setBackground(shape(0xFF02A5CF,8));content.addView(highlight,new LinearLayout.LayoutParams(-2,-2));space(10);
        connect=new PowerButton(this,card,accent,muted,heading);
        LinearLayout.LayoutParams circle=new LinearLayout.LayoutParams(dp(210),dp(210));circle.gravity=Gravity.CENTER_HORIZONTAL;content.addView(connect,circle);
        connect.setId(R.id.dns_power);
        connect.setOnClickListener(v->{if(DnsVpnService.active||DnsVpnService.connecting)DnsVpnService.requestStop(this);else requestConnect();updateState();});
        space(4);state=text("DNS выключен",18,ink,true);state.setTypeface(heading);state.setGravity(Gravity.CENTER);state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);content.addView(state);
        content.addView(text("После смены DNS полностью перезапустите нужное приложение.",13,muted,false));space(12);
        LinearLayout selection=box();selection.addView(text("Провайдер DNS",14,accent,true));providers=new Spinner(this);
        String[] names=new String[Provider.ALL.length];for(int i=0;i<names.length;i++)names[i]=Provider.ALL[i].name;
        providers.setAdapter(adapter(names));providers.setSelection(selected);selection.addView(providers);
        addresses=text("",13,ink,false);selection.addView(addresses);serviceRow=new LinearLayout(this);serviceRow.setOrientation(LinearLayout.VERTICAL);selection.addView(serviceRow);
        space(12);LinearLayout settings=box();
        encrypted=new Switch(this);encrypted.setText(R.string.encrypted_dns);encrypted.setTypeface(body);encrypted.setTextColor(ink);encrypted.setMinHeight(dp(48));settings.addView(encrypted);
        encrypted.setChecked(prefs.getBoolean("encrypted",false)||Provider.at(selected).encryptedOnly);
        settings.addView(text("DoH шифрует DNS-запросы. При ошибке перехода на обычный DNS нет.",12,muted,false));
        space(12);test=button(testing?"Остановить тест":"Тест DNS");content.addView(test);test.setOnClickListener(v->testDns());
        probe=text("Тест не меняет DNS. Проверяется ответ провайдера, а не доступ к аккаунту или сайту.",13,muted,false);content.addView(probe);
        space(12);LinearLayout info=box();info.addView(text("Как работает на телефоне",15,ink,true));
        info.addView(text("Без root. Android запросит разрешение VPN: локальный туннель обрабатывает только DNS. Сайты и приложения подключаются напрямую. Другой VPN одновременно использовать нельзя.\n\nСобственный Secure DNS браузера и DNS внутри приложений могут обходить настройку. Строгий частный DNS Android нужно переключить вручную. Не включайте блокировку соединений вне VPN.",13,muted,false));
        Button privateDns=button("Сетевые настройки Android");info.addView(privateDns);privateDns.setOnClickListener(v->{try{startActivity(new Intent(Settings.ACTION_WIRELESS_SETTINGS));}catch(ActivityNotFoundException ignored){}});
        Button widgets=button("Добавить виджет");widgets.setId(R.id.add_widget);info.addView(widgets);widgets.setOnClickListener(v->chooseWidget());
        space(14);content.addView(text("Код Всего · Android "+BuildConfig.VERSION_NAME,13,accent,true));
        Button channel=button("Telegram · TotalC0de ↗");channel.setTextColor(Color.WHITE);GradientDrawable channelBg=new GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT,new int[]{0xFF7E53FF,0xFF4B74FF});channelBg.setCornerRadius(dp(16));channel.setBackground(new RippleDrawable(ColorStateList.valueOf(0x33FFFFFF),channelBg,null));content.addView(channel);channel.setOnClickListener(v->startActivity(new Intent(Intent.ACTION_VIEW,android.net.Uri.parse("https://t.me/TotalC0de"))));
        providers.setOnItemSelectedListener(new SimpleSelection(){public void selected(int position){if(rendering)return;selected=position;updateProvider();save();}});
        encrypted.setOnCheckedChangeListener((b,on)->{if(!rendering&&!updatingProvider){updateProvider();save();}});
        updateProvider();rendering=false;updateState();
        if(ValueAnimator.areAnimatorsEnabled()){content.setAlpha(.7f);content.setTranslationY(dp(5));content.animate().alpha(1).translationY(0).setDuration(180).start();}
        if(resumed)connect.resumeUi();
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
        updatingProvider=true;
        if(p.encryptedOnly)encrypted.setChecked(true);else if(p.doh==null)encrypted.setChecked(false);
        updatingProvider=false;
        boolean locked=DnsVpnService.active||DnsVpnService.connecting;
        encrypted.setEnabled(!locked&&p.doh!=null&&!p.encryptedOnly);
        addresses.setText(encrypted.isChecked()?"DoH: "+p.doh:"DNS: "+android.text.TextUtils.join(" / ",p.servers));
        String key=selected+":"+encrypted.isChecked();
        if(key.equals(providerContent))return;providerContent=key;
        serviceRow.removeAllViews();LinearLayout row=null;
        for(int i=0;i<p.services.length;i++) {
            if(i%2==0){row=new LinearLayout(this);serviceRow.addView(row);}
            LinearLayout item=new LinearLayout(this);item.setGravity(Gravity.CENTER_VERTICAL);item.setPadding(0,dp(6),dp(8),dp(6));
            ImageView icon=new ImageView(this);icon.setContentDescription(p.services[i]);
            String name=p.services[i].toLowerCase(java.util.Locale.ROOT);
            Drawable.ConstantState cached=iconCache.get(name);
            if(cached==null)try(InputStream stream=getAssets().open(name+".png")){Drawable source=Drawable.createFromStream(stream,null);if(source!=null){cached=source.getConstantState();if(cached!=null)iconCache.put(name,cached);}}catch(IOException ignored){}
            if(cached!=null)icon.setImageDrawable(cached.newDrawable(getResources()));
            item.addView(icon,new LinearLayout.LayoutParams(dp(24),dp(24)));TextView label=text(p.services[i],13,ink,false);label.setPadding(dp(7),0,0,0);item.addView(label);
            row.addView(item,new LinearLayout.LayoutParams(0,-2,1));
        }
        if(selected==3)serviceRow.addView(text("Только Xbox Live · нейросети не поддерживаются",12,muted,false));
        if(selected==5)serviceRow.addView(text("Только DoH · DNS-ответ не гарантирует доступ к Gemini",12,muted,false));
        if(!rendering&&ValueAnimator.areAnimatorsEnabled()){serviceRow.animate().cancel();serviceRow.setAlpha(.35f);serviceRow.setTranslationY(dp(5));serviceRow.animate().alpha(1).translationY(0).setDuration(180).start();}
    }
    private void save(){prefs.edit().putInt("provider",selected).putBoolean("encrypted",encrypted.isChecked()).apply();DnsWidget.updateAll(this);}
    private DnsConfig config(){return new DnsConfig(selected,encrypted.isChecked());}
    private void updateState() {
        if(state==null)return;boolean locked=DnsVpnService.active||DnsVpnService.connecting;
        String snapshot=DnsVpnService.status+":"+locked+":"+DnsVpnService.active+":"+DnsVpnService.connecting+":"+selected+":"+testing;
        if(snapshot.equals(lastUiState))return;lastUiState=snapshot;
        if(!android.text.TextUtils.equals(state.getText(),DnsVpnService.status))state.setText(DnsVpnService.status);
        connect.setState(DnsVpnService.active,DnsVpnService.connecting);state.setTextColor(DnsVpnService.active?accent:ink);
        providers.setEnabled(!locked);
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
        Intent start=new Intent(this,DnsVpnService.class).setAction(DnsVpnService.START).putExtra("provider",pending.provider).putExtra("encrypted",pending.encrypted);
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
                    Provider target=Provider.at(config.provider);String[] targets=config.encrypted?new String[]{target.doh}:target.servers;
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
    static void applyInsets(ScrollView scroll,WindowInsets insets) {
        if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());scroll.setPadding(bars.left,bars.top,bars.right,bars.bottom);}
        else scroll.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
    }
    private void chooseWidget(){
        new AlertDialog.Builder(this).setTitle("Виджеты Smart DNS").setItems(new String[]{"Компактный · 2 × 1","Расширенный · 4 × 2"},(dialog,which)->{
            AppWidgetManager manager=AppWidgetManager.getInstance(this);
            if(manager.isRequestPinAppWidgetSupported())manager.requestPinAppWidget(new ComponentName(this,which==0?CompactDnsWidget.class:DnsWidget.class),null,null);
            else message("Добавление виджета","Удерживайте пустое место на главном экране, откройте «Виджеты» и выберите Smart DNS.");
        }).setNegativeButton("Отмена",null).show();
    }
    private void handleWidgetIntent(){
        if(!DnsWidget.CONNECT.equals(getIntent().getAction()))return;
        getIntent().setAction(null);
        handler.post(()->{if(isFinishing()||isDestroyed()||DnsVpnService.active||DnsVpnService.connecting)return;requestConnect();});
    }
    @Override protected void onNewIntent(Intent intent){super.onNewIntent(intent);setIntent(intent);handleWidgetIntent();}
    private void settleMotion(){
        if(content!=null){content.animate().cancel();content.setAlpha(1);content.setTranslationY(0);}
        if(serviceRow!=null){serviceRow.animate().cancel();serviceRow.setAlpha(1);serviceRow.setTranslationY(0);}
    }
    @Override protected void onResume(){super.onResume();resumed=true;settleMotion();lastUiState=null;updateState();connect.resumeUi();handler.removeCallbacks(refresh);handler.post(refresh);DnsWidget.updateAll(this);}
    @Override protected void onPause(){resumed=false;handler.removeCallbacks(refresh);settleMotion();if(connect!=null)connect.pauseUi();super.onPause();}
    @Override protected void onDestroy(){handler.removeCallbacksAndMessages(null);tests.shutdownNow();super.onDestroy();}
}

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
    private TextView checkerStatus,connectionDetail;private String checkerKey="";private int checkerGeneration;
    private ProviderCheck.Result checkerResult;private Future<?> checkerTask;private volatile ProviderCheck.SystemIo checkerIo;
    private final ExecutorService checkerWorker=Executors.newSingleThreadExecutor();
    private RemoteCatalog catalog;
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final ExecutorService tests=Executors.newSingleThreadExecutor();
    private android.content.SharedPreferences prefs;
    private boolean dark,rendering,testing,resumed;private int selected;
    private LinearLayout content,serviceRow;private TextView state,addresses,probe,encryptionHint;
    private PowerButton connect;private Button test;private Switch encrypted;private TextView providers;
    private int ink,muted,accent,card;
    private DnsConfig pending;
    private Dialog providerDialog;
    private UpdateController updater;
    private Button updateButton;
    private boolean manualUpdateCheck,installAfterPermission,preparingInstall;
    private String updatePromptKey="";
    private final UpdateController.Listener updateListener=this::onUpdateChanged;
    private final java.util.List<Dialog> dialogs=new java.util.ArrayList<>();
    private Future<?> runningTest;private int testGeneration;
    private Typeface heading,body,bold;
    private final Map<String,Drawable.ConstantState> iconCache=new HashMap<>();
    private String lastUiState,providerContent;
    private boolean updatingProvider;
    private boolean tlsSelected;private final Map<Integer,Button> protocolChoices=new HashMap<>();private String lastTestReport="",exportJournal;
    private final Runnable refresh=new Runnable(){public void run(){updateState();showUpdatePrompt(false);handler.postDelayed(this,1000);}};
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);catalog=RemoteCatalog.get(this);catalog.applyIfIdle(true);prefs=getSharedPreferences("smartdns",MODE_PRIVATE);updater=UpdateController.get(this);AppJournal.init(this);AppJournal.record("Экран","Открыт; версия "+BuildConfig.VERSION_NAME);
        installAfterPermission=saved!=null&&saved.getBoolean("installAfterPermission",false);
        int savedProvider=prefs.getInt("provider",0);selected=Provider.preferredIndex(savedProvider);
        if(selected!=savedProvider)prefs.edit().putInt("provider",selected).apply();
        prefs.edit().remove("ag").remove("agProvider").apply();
        heading=font("Unbounded-ExtraBold.ttf",Typeface.DEFAULT_BOLD);body=font("Manrope-Medium.ttf",Typeface.DEFAULT);bold=font("Manrope-Bold.ttf",Typeface.DEFAULT_BOLD);
        tlsSelected=DnsConfig.saved(prefs,selected).protocol==DnsConfig.DOT;dark=prefs.getBoolean("dark",true);render();
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
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);scroll.setVerticalScrollBarEnabled(false);scroll.setHorizontalScrollBarEnabled(false);
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
        ImageButton settingsButton=new ImageButton(this);settingsButton.setImageResource(R.drawable.ic_settings);
        settingsButton.setTag("app-settings");settingsButton.setContentDescription("Настройки");settingsButton.setPadding(dp(12),dp(12),dp(12),dp(12));
        settingsButton.setBackground(new RippleDrawable(ColorStateList.valueOf(0x3302A5CF),shape(card,16),null));
        ImageButton theme=new ImageButton(this);theme.setImageResource(dark?R.drawable.ic_theme_sun:R.drawable.ic_theme_moon);theme.setTag("theme-toggle");
        theme.setContentDescription(dark?"Включить светлую тему":"Включить тёмную тему");theme.setPadding(dp(12),dp(12),dp(12),dp(12));theme.setBackground(new RippleDrawable(ColorStateList.valueOf(0x3302A5CF),shape(card,16),null));
        top.addView(theme,new LinearLayout.LayoutParams(dp(48),dp(48)));theme.setOnClickListener(v->{dark=!dark;prefs.edit().putBoolean("dark",dark).apply();render();});
        LinearLayout.LayoutParams gearParams=new LinearLayout.LayoutParams(dp(48),dp(48));gearParams.leftMargin=dp(8);top.addView(settingsButton,gearParams);settingsButton.setOnClickListener(v->showSettings());content.addView(top);
        space(14);TextView title=text("Доступ",30,ink,true);title.setTypeface(heading);content.addView(title);
        TextView highlight=text("к сервисам",28,Color.rgb(21,29,34),true);highlight.setTypeface(heading);highlight.setPadding(dp(8),0,dp(8),dp(4));highlight.setBackground(shape(0xFF02A5CF,8));content.addView(highlight,new LinearLayout.LayoutParams(-2,-2));space(10);
        connect=new PowerButton(this,card,accent,muted,heading);
        LinearLayout.LayoutParams circle=new LinearLayout.LayoutParams(dp(184),dp(184));circle.gravity=Gravity.CENTER_HORIZONTAL;content.addView(connect,circle);
        connect.setId(R.id.dns_power);
        connect.setOnClickListener(v->{if(DnsVpnService.active||DnsVpnService.connecting)DnsVpnService.requestStop(this);else requestConnect();updateState();});
        space(4);state=text("DNS выключен",18,ink,true);state.setTypeface(heading);state.setGravity(Gravity.CENTER);state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);content.addView(state);
        connectionDetail=text("",13,muted,false);connectionDetail.setTag("connection-detail");connectionDetail.setGravity(Gravity.CENTER);connectionDetail.setVisibility(View.GONE);content.addView(connectionDetail);connectionDetail.setOnClickListener(v->message("Подробности подключения",DnsVpnService.switchError.isEmpty()?DnsVpnService.status:DnsVpnService.switchError));
        checkerStatus=text("Чекер: DNS выключен",12,muted,false);checkerStatus.setTag("provider-checker");checkerStatus.setGravity(Gravity.CENTER);checkerStatus.setMinHeight(dp(48));checkerStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);content.addView(checkerStatus);checkerStatus.setOnClickListener(v->showChecker());
        content.addView(text("После смены DNS полностью перезапустите нужное приложение.",13,muted,false));space(20);
        LinearLayout selection=box();selection.addView(text("Провайдер DNS",12,muted,false));
        providers=text("",18,ink,true);providers.setId(R.id.dns_provider);providers.setGravity(Gravity.CENTER_VERTICAL);
        providers.setMinHeight(dp(56));providers.setPadding(dp(12),dp(10),dp(12),dp(10));
        providers.setCompoundDrawablesRelativeWithIntrinsicBounds(0,0,R.drawable.ic_chevron_down,0);providers.setCompoundDrawablePadding(dp(12));
        providers.setBackground(new RippleDrawable(ColorStateList.valueOf(0x2202A5CF),shape(dark?0xFF1B2530:0xFFE2F2F7,14),null));
        providers.setFocusable(true);providers.setOnClickListener(v->chooseProvider());
        LinearLayout.LayoutParams pickerParams=new LinearLayout.LayoutParams(-1,-2);pickerParams.setMargins(0,dp(8),0,dp(12));selection.addView(providers,pickerParams);
        addresses=text("",13,ink,false);selection.addView(addresses);serviceRow=new LinearLayout(this);serviceRow.setOrientation(LinearLayout.VERTICAL);selection.addView(serviceRow);
        space(16);
        encrypted=new Switch(this);encrypted.setId(R.id.encrypted_dns_switch);
        encrypted.setChecked(prefs.getBoolean("encrypted",false)||Provider.at(selected).encryptedOnly);
        LinearLayout quick=box();quick.addView(text("Шифрование DNS",12,muted,false));
        LinearLayout protocols=new LinearLayout(this);protocols.setTag("dns-protocol");quick.addView(protocols);protocolChoices.clear();
        String[] labels={"Обычный","DoH","DoT"};for(int mode=0;mode<3;mode++){final int choice=mode;Button option=secondaryButton(labels[mode]);option.setTextSize(14);option.setMinWidth(0);option.setMinimumWidth(0);option.setPadding(dp(4),dp(8),dp(4),dp(8));option.setTag("protocol-"+mode);protocolChoices.put(mode,option);LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(0,dp(48),1);if(mode>0)params.leftMargin=dp(8);protocols.addView(option,params);option.setOnClickListener(v->selectConfig(new DnsConfig(selected,choice)));}
        encryptionHint=text("",12,muted,false);quick.addView(encryptionHint);
        space(16);LinearLayout diagnostics=box();LinearLayout actions=new LinearLayout(this);diagnostics.addView(actions);
        Button auto=secondaryButton("Авто · beta");auto.setTag("dns-auto");LinearLayout.LayoutParams autoParams=new LinearLayout.LayoutParams(0,dp(52),1);autoParams.rightMargin=dp(8);actions.addView(auto,autoParams);auto.setOnClickListener(v->autoChoose());
        test=button(testing?"Стоп":"Тест DNS");actions.addView(test,new LinearLayout.LayoutParams(0,dp(52),1));test.setOnClickListener(v->testDns());
        probe=text("Проверяется DNS-ответ, а не вход в аккаунт или ответ ИИ.",13,muted,false);LinearLayout.LayoutParams reportParams=new LinearLayout.LayoutParams(-1,-2);reportParams.topMargin=dp(12);diagnostics.addView(probe,reportParams);probe.setMaxLines(5);probe.setEllipsize(android.text.TextUtils.TruncateAt.END);probe.setOnClickListener(v->message("Результаты DNS",lastTestReport.isEmpty()?probe.getText().toString():lastTestReport));
        Button copy=secondaryButton("Скопировать результат");LinearLayout.LayoutParams copyParams=new LinearLayout.LayoutParams(-1,dp(48));copyParams.topMargin=dp(12);diagnostics.addView(copy,copyParams);copy.setOnClickListener(v->{android.content.ClipboardManager clipboard=(android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE);clipboard.setPrimaryClip(ClipData.newPlainText("Smart DNS",lastTestReport.isEmpty()?probe.getText():lastTestReport));});
        space(24);content.addView(text("Код Всего · Android "+BuildConfig.VERSION_NAME,13,accent,true));space(8);
        Button channel=button("Telegram · TotalC0de ↗");channel.setTextColor(Color.WHITE);GradientDrawable channelBg=new GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT,new int[]{0xFF7E53FF,0xFF4B74FF});channelBg.setCornerRadius(dp(16));channel.setBackground(new RippleDrawable(ColorStateList.valueOf(0x33FFFFFF),channelBg,null));content.addView(channel);channel.setOnClickListener(v->startActivity(new Intent(Intent.ACTION_VIEW,android.net.Uri.parse("https://t.me/TotalC0de"))));
        encrypted.setOnCheckedChangeListener((b,on)->{if(!rendering&&!updatingProvider){if(!on)tlsSelected=false;updateProvider();save();}});
        updateProvider();rendering=false;updateState();
        if(ValueAnimator.areAnimatorsEnabled()){content.setAlpha(.7f);content.setTranslationY(dp(5));content.animate().alpha(1).translationY(0).setDuration(180).start();}
        if(resumed)connect.resumeUi();updateButtonState();
    }
    private Button secondaryButton(String label){
        Button b=button(label);b.setTextColor(accent);
        GradientDrawable background=shape(card,14);background.setStroke(dp(1),dark?0xFF304452:0xFFB7D6DF);
        b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x2202A5CF),background,null));return b;
    }
    private void chooseProvider(){
        if(DnsVpnService.connecting||DnsVpnService.switching)return;
        Dialog sheet=new Dialog(this);providerDialog=sheet;sheet.setOnDismissListener(dialog->{if(providerDialog==sheet)providerDialog=null;});sheet.requestWindowFeature(Window.FEATURE_NO_TITLE);
        LinearLayout panel=new LinearLayout(this);panel.setId(R.id.provider_sheet);panel.setOrientation(LinearLayout.VERTICAL);panel.setPadding(dp(20),dp(12),dp(20),dp(20));
        GradientDrawable background=shape(card,28);background.setCornerRadii(new float[]{dp(28),dp(28),dp(28),dp(28),0,0,0,0});panel.setBackground(background);
        View handle=new View(this);handle.setBackground(shape(dark?0xFF4C5360:0xFFB5C6CD,4));LinearLayout.LayoutParams handleParams=new LinearLayout.LayoutParams(dp(36),dp(4));handleParams.gravity=Gravity.CENTER_HORIZONTAL;handleParams.bottomMargin=dp(16);panel.addView(handle,handleParams);
        TextView title=text("Выберите DNS",21,ink,true);if(Build.VERSION.SDK_INT>=28)title.setAccessibilityHeading(true);panel.addView(title);
        panel.addView(text("Выберите провайдера для подключения",13,muted,false));
        ScrollView list=new ScrollView(this);list.setVerticalScrollBarEnabled(false);list.setHorizontalScrollBarEnabled(false);
        LinearLayout rows=new LinearLayout(this);rows.setOrientation(LinearLayout.VERTICAL);list.addView(rows);panel.addView(list,new LinearLayout.LayoutParams(-1,0,1));
        for(int i=0;i<Provider.ALL.length;i++){
            if(!Provider.isAvailable(i)||hiddenProviders().contains(String.valueOf(i))&&i!=selected)continue;
            final int position=i;Provider provider=Provider.at(i);boolean checked=i==selected;
            LinearLayout row=new LinearLayout(this);row.setOrientation(LinearLayout.HORIZONTAL);row.setGravity(Gravity.CENTER_VERTICAL);row.setPadding(dp(16),dp(12),dp(16),dp(12));row.setMinimumHeight(dp(84));row.setTag("provider-"+i);
            GradientDrawable tile=shape(checked?(dark?0xFF153643:0xFFDCF2F8):(dark?0xFF1B1E29:0xFFF9FAFD),18);tile.setStroke(dp(1),checked?accent:(dark?0xFF303441:0xFFE1E5EC));
            row.setBackground(new RippleDrawable(ColorStateList.valueOf(0x3302A5CF),tile,null));row.setFocusable(true);
            row.setSelected(checked);
            LinearLayout labels=new LinearLayout(this);labels.setOrientation(LinearLayout.VERTICAL);
            labels.addView(text(provider.name,16,checked?accent:ink,true));labels.addView(text(android.text.TextUtils.join(" · ",provider.services),12,muted,false));
            labels.addView(text((provider.encryptedOnly?"С шифрованием": "DNS")+(provider.doh!=null?" · DoH":"")+(provider.dot!=null?" · DoT":""),11,checked?accent:muted,false));row.addView(labels,new LinearLayout.LayoutParams(0,-2,1));
            TextView mark=text(checked?"✓":"○",22,checked?accent:muted,true);mark.setPadding(dp(12),0,0,0);row.addView(mark);
            row.setContentDescription(provider.name+", "+(checked?"выбран, ":"")+android.text.TextUtils.join(", ",provider.services));
            LinearLayout.LayoutParams rowParams=new LinearLayout.LayoutParams(-1,-2);rowParams.topMargin=dp(12);rows.addView(row,rowParams);
            row.setOnClickListener(v->{if(!DnsVpnService.connecting&&!DnsVpnService.switching){int protocol=tlsSelected&&provider.dot!=null?DnsConfig.DOT:encrypted.isChecked()&&provider.doh!=null?DnsConfig.DOH:DnsConfig.PLAIN;selectConfig(new DnsConfig(position,protocol));}sheet.dismiss();});
        }
        Button close=secondaryButton("Готово");LinearLayout.LayoutParams closeParams=new LinearLayout.LayoutParams(-1,-2);closeParams.topMargin=dp(16);panel.addView(close,closeParams);close.setOnClickListener(v->sheet.dismiss());
        sheet.setContentView(panel);Window window=sheet.getWindow();if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.setGravity(Gravity.BOTTOM);window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);window.setDimAmount(.55f);window.setWindowAnimations(R.style.ProviderSheetMotion);}
        sheet.show();if(window!=null){int height=getResources().getDisplayMetrics().heightPixels;window.setLayout(-1,(int)(height*.85f));window.setNavigationBarColor(card);}
    }
    private void updateProvider() {
        Provider p=Provider.at(selected);
        encrypted.setText(p.doh==null?"Зашифрованный DNS · недоступен":getString(R.string.encrypted_dns));
        if(tlsSelected&&p.dot==null)tlsSelected=false;
        encryptionHint.setText("DoH — DNS через HTTPS, порт 443. DoT — DNS через TLS, порт 853. Обычный DNS не зашифрован.");
        providers.setText(p.name);providers.setContentDescription("Провайдер DNS: "+p.name+". Выбрать другой");
        updatingProvider=true;
        if(p.encryptedOnly)encrypted.setChecked(true);else if(p.doh==null)encrypted.setChecked(false);
        updatingProvider=false;
        boolean locked=DnsVpnService.active||DnsVpnService.connecting;
        encrypted.setEnabled(!locked&&p.doh!=null&&!p.encryptedOnly);
        updateProtocolChoices();
        addresses.setText(tlsSelected?"DoT: "+p.dot:encrypted.isChecked()?"DoH: "+p.doh:"DNS: "+android.text.TextUtils.join(" / ",p.servers));
        String key=selected+":"+encrypted.isChecked()+":"+tlsSelected;
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
        if(selected==5)serviceRow.addView(text("Только с шифрованием · DNS-ответ не гарантирует доступ к Gemini",12,muted,false));
        if(!rendering&&ValueAnimator.areAnimatorsEnabled()){serviceRow.animate().cancel();serviceRow.setAlpha(.35f);serviceRow.setTranslationY(dp(5));serviceRow.animate().alpha(1).translationY(0).setDuration(180).start();}
    }
    private void updateProtocolChoices(){Provider p=Provider.at(selected);int chosen=tlsSelected?DnsConfig.DOT:encrypted.isChecked()?DnsConfig.DOH:DnsConfig.PLAIN;
        for(Map.Entry<Integer,Button> entry:protocolChoices.entrySet()){int mode=entry.getKey();Button option=entry.getValue();boolean supported=mode==DnsConfig.PLAIN?!p.encryptedOnly:mode==DnsConfig.DOH?p.doh!=null:p.dot!=null&&p.dotServers.length>0;option.setEnabled(supported&&!DnsVpnService.connecting&&!DnsVpnService.switching);option.setAlpha(supported?1f:.4f);option.setSelected(mode==chosen);option.setBackgroundTintList(ColorStateList.valueOf(mode==chosen?accent:card));option.setTextColor(mode==chosen?(dark?Color.rgb(21,29,34):Color.WHITE):accent);}
        encryptionHint.setText(chosen==DnsConfig.DOH?"DoH · DNS через HTTPS, порт 443":chosen==DnsConfig.DOT?"DoT · DNS через TLS, порт 853":"Обычный DNS · без шифрования");
    }
    private void save(){prefs.edit().putInt("provider",selected).putBoolean("encrypted",encrypted.isChecked()).putInt("protocol",tlsSelected?DnsConfig.DOT:encrypted.isChecked()?DnsConfig.DOH:DnsConfig.PLAIN).apply();DnsWidget.updateAll(this);}
    private DnsConfig config(){if(!Provider.isAvailable(selected))throw new IllegalArgumentException("Провайдер временно недоступен. Выберите другой DNS.");return new DnsConfig(selected,tlsSelected?DnsConfig.DOT:encrypted.isChecked()?DnsConfig.DOH:DnsConfig.PLAIN);}
    private void updateState() {
        if(state==null)return;if(catalog!=null&&catalog.applyIfIdle(!testing&&providerDialog==null&&dialogs.stream().noneMatch(Dialog::isShowing))){selected=Provider.preferredIndex(selected);updateProvider();lastUiState=null;checkerKey="";}updateChecker(false);boolean locked=DnsVpnService.active||DnsVpnService.connecting;
        DnsConfig current=DnsVpnService.current;if(DnsVpnService.active&&current!=null&&(selected!=current.provider||config().protocol!=current.protocol)){selected=current.provider;tlsSelected=current.protocol==DnsConfig.DOT;updatingProvider=true;encrypted.setChecked(current.encrypted);updatingProvider=false;updateProvider();}
        String snapshot=DnsVpnService.status+":"+locked+":"+DnsVpnService.active+":"+DnsVpnService.connecting+":"+selected+":"+testing+":"+DnsVpnService.switching+":"+DnsVpnService.switchError;
        if(snapshot.equals(lastUiState))return;lastUiState=snapshot;
        boolean failed=DnsVpnService.status.startsWith("Ошибка");String caption=failed?"Не удалось подключиться":DnsVpnService.status;
        if(!android.text.TextUtils.equals(state.getText(),caption))state.setText(caption);
        boolean switchFailed=!DnsVpnService.switchError.isEmpty();connectionDetail.setVisibility(failed||switchFailed?View.VISIBLE:View.GONE);
        if(switchFailed)connectionDetail.setText(DnsVpnService.switchError+" · Подробнее");else if(failed)connectionDetail.setText(connectionErrorHint(DnsVpnService.status)+" · Подробнее");
        DnsConfig target=DnsVpnService.switchTarget;providers.setText(target!=null?Provider.at(target.provider).name+" · проверяем…":Provider.at(selected).name);
        connect.setState(DnsVpnService.active,DnsVpnService.connecting);state.setTextColor(DnsVpnService.active?accent:ink);
        providers.setEnabled(!DnsVpnService.connecting&&!DnsVpnService.switching);
        providers.setAlpha(DnsVpnService.connecting||DnsVpnService.switching?.55f:1);
        updateProtocolChoices();
        encrypted.setEnabled(!locked&&Provider.at(selected).doh!=null&&!Provider.at(selected).encryptedOnly);
        test.setEnabled(!DnsVpnService.connecting);
    }
    static String connectionErrorHint(String detail){String error=detail.toLowerCase(java.util.Locale.ROOT);if(error.contains("защитить")||error.contains("eperm")||error.contains("not permitted"))return "Android запретил сетевое соединение. Отключите другой VPN; проверьте «Постоянный VPN» и «Блокировать подключения без VPN» в настройках Android.";if(error.contains("предыдущий vpn"))return "Прежний VPN не успел отключиться или переподключился. Отключите его вручную и проверьте настройку постоянного VPN.";if(error.contains("vpn"))return "Проверьте разрешение VPN и отключите другое VPN-подключение.";return "DNS не подключён. Проверьте интернет или выберите другой DNS / протокол.";}
    private void requestConnect() {
        try{pending=config();PhysicalNetwork.checkPrivateDns(this,PhysicalNetwork.find(this));}
        catch(Exception e){message("Не удалось подключиться",e.getMessage());return;}
        if(PhysicalNetwork.otherVpn(this)){
            CardDialog dialog=new CardDialog("Другой VPN включён","После подтверждения Android отключит прежний VPN. Smart DNS дождётся завершения переключения (до 8 секунд), затем подключится. Продолжить?");
            dialog.action("Продолжить",true,true,this::prepareVpn);dialog.action("Отмена",false,true,()->pending=null);dialog.show();
        }
        else prepareVpn();
    }
    private void prepareVpn() {Intent permission=VpnService.prepare(this);if(permission==null)startDns();else startActivityForResult(permission,100);}
    private void startDns() {
        if(pending==null)return;
        if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},101);
        Intent start=new Intent(this,DnsVpnService.class).setAction(DnsVpnService.START).putExtra("provider",pending.provider).putExtra("encrypted",pending.encrypted).putExtra("protocol",pending.protocol);
        try{startForegroundService(start);}catch(Exception e){message("Ошибка подключения",e.getMessage());}pending=null;
    }
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(request==402){saveJournalDocument(result,data);return;}if(request==202){installAfterPermission=getPackageManager().canRequestPackageInstalls();return;}if(request==100){if(result==RESULT_OK){try{if(pending==null)pending=config();startDns();}catch(Exception e){message("Ошибка настройки",e.getMessage());}}else{pending=null;message("Подключение отменено","Разрешение VPN не предоставлено. DNS не изменён.");}}}
    private void saveJournalDocument(int result,Intent data){String report=exportJournal==null?AppJournal.report():exportJournal;exportJournal=null;if(result!=RESULT_OK||data==null||data.getData()==null)return;
        try(OutputStream out=getContentResolver().openOutputStream(data.getData())){if(out==null)throw new IOException("Файл недоступен");out.write(report.getBytes(java.nio.charset.StandardCharsets.UTF_8));}
        catch(IOException|SecurityException error){message("Не удалось сохранить",error.getMessage());}
    }
    private void message(String title,String body){AppJournal.record(title,body);if(!isFinishing()&&!isDestroyed()){CardDialog dialog=new CardDialog(title,body);dialog.action("Понятно",true,true,()->{});dialog.show();}}
    private final class CardDialog {
        final Dialog dialog=new Dialog(MainActivity.this);
        final LinearLayout panel=new LinearLayout(MainActivity.this);
        final ScrollView scroll=new ScrollView(MainActivity.this);
        final TextView description;
        Runnable onDismiss=()->{};
        CardDialog(String title,String message){
            dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);panel.setOrientation(LinearLayout.VERTICAL);panel.setPadding(dp(24),dp(24),dp(24),dp(24));panel.setBackground(shape(card,24));
            TextView heading=text(title,21,ink,true);panel.addView(heading);
            description=text(message,14,muted,false);LinearLayout.LayoutParams textParams=new LinearLayout.LayoutParams(-1,-2);textParams.topMargin=dp(12);textParams.bottomMargin=dp(12);panel.addView(description,textParams);
            scroll.setVerticalScrollBarEnabled(false);scroll.addView(panel);dialog.setContentView(scroll);
            dialog.setOnDismissListener(d->{dialogs.remove(dialog);onDismiss.run();});dialogs.add(dialog);
        }
        void action(String label,boolean primary,boolean close,Runnable action){
            Button button=primary?button(label):secondaryButton(label);LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,-2);params.topMargin=dp(12);panel.addView(button,params);
            button.setOnClickListener(v->{if(close)dialog.dismiss();action.run();});
        }
        void show(){
            if(isFinishing()||isDestroyed()){dialog.dismiss();return;}
            Window window=dialog.getWindow();if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);window.setDimAmount(.6f);window.setWindowAnimations(R.style.ProviderSheetMotion);}
            dialog.show();if(window!=null){int width=Math.min(getResources().getDisplayMetrics().widthPixels-dp(32),dp(440));int maxHeight=(int)(getResources().getDisplayMetrics().heightPixels*.85f);scroll.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(maxHeight,View.MeasureSpec.AT_MOST));window.setLayout(width,Math.min(scroll.getMeasuredHeight(),maxHeight));}
        }
    }
    private void testDns() {
        if(testing){testGeneration++;if(runningTest!=null)runningTest.cancel(true);testing=false;test.setText(R.string.test_dns);probe.setText(R.string.test_stopped);updateState();return;}
        final DnsConfig config;try{config=config();}catch(Exception e){message("Ошибка настройки",e.getMessage());return;}
        testing=true;test.setText("Стоп");updateState();final int generation=++testGeneration;
        runningTest=tests.submit(()->{
            StringBuilder report=new StringBuilder("Проверка "+Provider.at(config.provider).name+" · "+config.label()+"\n\n");
            try(UpstreamResolver resolver=new UpstreamResolver(this,null)) {
                PhysicalNetwork.checkPrivateDns(this,PhysicalNetwork.find(this));
                for(String host:Provider.at(config.provider).testHosts()) {
                    if(Thread.currentThread().isInterrupted())break;
                    Provider target=Provider.at(config.provider);String[] targets=config.encrypted?new String[]{target.doh}:target.servers;
                    for(String server:targets){
                        if(Thread.currentThread().isInterrupted())break;
                        long began=System.nanoTime();
                        try{byte[] answer=config.protocol==DnsConfig.DOT?resolver.resolve(DnsWire.query(host),config):resolver.probe(DnsWire.query(host),target,config.encrypted,server);DnsWire.requireAddress(answer);
                            report.append("✓ ").append(host).append(" — ").append((System.nanoTime()-began)/1000000).append(" мс");
                        }catch(Exception e){report.append("✕ ").append(host).append(" — ").append(e.getMessage());}
                        report.append(" [").append(server).append("]\n");
                    }
                }
            }catch(Exception e){report.append(e.getMessage());}
            report.append("\nDNS-ответ не доказывает доступ к сайту, аккаунту или отсутствие региональной ошибки. Настройки не изменены.");
            lastTestReport=report.toString();AppJournal.record("Тест DNS",lastTestReport);handler.post(()->{if(isDestroyed()||generation!=testGeneration)return;testing=false;test.setText(R.string.test_dns);probe.setText(report.toString());updateState();});
        });
    }
    private java.util.Set<String> hiddenProviders(){return new java.util.HashSet<>(prefs.getStringSet("hiddenProviders",java.util.Collections.emptySet()));}
    private void selectConfig(DnsConfig next){
        if(DnsVpnService.connecting||DnsVpnService.switching)return;
        cancelSelection();
        if(DnsVpnService.active){AppJournal.record("Выбор DNS",Provider.at(next.provider).name+"; "+next.label());providers.setText(Provider.at(next.provider).name+" · проверяем…");startService(new Intent(this,DnsVpnService.class).setAction(DnsVpnService.SWITCH).putExtra("provider",next.provider).putExtra("protocol",next.protocol));return;}
        selected=next.provider;tlsSelected=next.protocol==DnsConfig.DOT;updatingProvider=true;encrypted.setChecked(next.encrypted);updatingProvider=false;updateProvider();save();lastUiState=null;updateState();
    }
    private void cancelSelection(){if(testing){testGeneration++;if(runningTest!=null)runningTest.cancel(true);testing=false;test.setText(R.string.test_dns);}}
    private void chooseProtocol(){Provider provider=Provider.at(selected);CardDialog dialog=new CardDialog("Шифрование DNS","DoH — HTTPS, порт 443. DoT — TLS, порт 853. Если один протокол недоступен в сети, попробуйте другой. Возврата к обычному DNS при ошибке нет.");
        if(provider.doh!=null)dialog.action("DoH · HTTPS",true,true,()->selectConfig(new DnsConfig(selected,DnsConfig.DOH)));
        if(provider.dot!=null&&provider.dotServers.length>0)dialog.action("DoT · TLS",false,true,()->selectConfig(new DnsConfig(selected,DnsConfig.DOT)));
        if(!provider.encryptedOnly)dialog.action("Без шифрования",false,true,()->selectConfig(new DnsConfig(selected,DnsConfig.PLAIN)));dialog.show();
    }
    private void autoChoose(){
        if(testing||DnsVpnService.connecting||DnsVpnService.switching)return;
        try{PhysicalNetwork.requireProbeNetwork(this);}catch(IOException error){message("Авто: проверьте подключение",error.getMessage());return;}
        testing=true;final int generation=++testGeneration;test.setText("Стоп");probe.setText("Авто · beta: проверяем DNS. Доступ к сайтам, аккаунтам и моделям не проверяется.");
        final java.util.Set<String> hidden=hiddenProviders();
        runningTest=tests.submit(()->{try(UpstreamResolver resolver=new UpstreamResolver(this,null)){PhysicalNetwork.requireProbeNetwork(this);
            java.util.List<DnsSelection.Result> results=DnsSelection.check(DnsSelection.candidates(hidden,true,-1),c->{long began=System.nanoTime();DnsWire.requireAddress(resolver.resolve(DnsWire.query("example.com"),c));return (System.nanoTime()-began)/1000000;},30000);
            DnsSelection.Result best=DnsSelection.best(results);StringBuilder report=new StringBuilder("Авто · DNS-ответ и задержка, без проверки входа и генерации\n");for(DnsSelection.Result result:results)report.append(Provider.at(result.config.provider).name).append(" · ").append(result.config.label()).append(" · ").append(result.error==null?result.label():result.error).append('\n');
            if(results.size()<DnsSelection.candidates(hidden,true,-1).size())report.append("Часть проверок не завершилась за 30 с.\n");
            lastTestReport=report.toString();AppJournal.record("Ручной Авто",lastTestReport);
            handler.post(()->{if(isDestroyed()||generation!=testGeneration)return;testing=false;test.setText(R.string.test_dns);probe.setText(lastTestReport);try{PhysicalNetwork.requireProbeNetwork(this);}catch(IOException error){message("Авто: подключение изменилось",error.getMessage());updateState();return;}if(best!=null){boolean active=DnsVpnService.active;selectConfig(best.config);if(!active)requestConnect();}else message("Авто: DNS не найден","Доступный зашифрованный DNS не найден. Прежнее подключение сохранено.");updateState();});
        }catch(Exception error){handler.post(()->{if(isDestroyed()||generation!=testGeneration)return;testing=false;test.setText(R.string.test_dns);message("Авто не завершено",error.getMessage());updateState();});}});
    }
    private void cancelChecker(){checkerGeneration++;if(checkerTask!=null)checkerTask.cancel(true);ProviderCheck.SystemIo io=checkerIo;checkerIo=null;if(io!=null)io.close();}
    private String checkerConnectionKey(){android.net.ConnectivityManager cm=getSystemService(android.net.ConnectivityManager.class);DnsConfig c=DnsVpnService.current;String network="";if(cm!=null){android.net.Network active=cm.getActiveNetwork();network=active+"/"+(active==null?"":cm.getLinkProperties(active));try{android.net.Network physical=PhysicalNetwork.find(this);network+="/"+physical+"/"+cm.getLinkProperties(physical);}catch(IOException ignored){}}return DnsVpnService.active+":"+DnsVpnService.switching+":"+System.identityHashCode(c)+":"+network;}
    private void updateChecker(boolean manual){
        if(checkerStatus==null)return;DnsConfig config=DnsVpnService.current;String key=checkerConnectionKey();
        if(!manual&&key.equals(checkerKey)){if(checkerResult!=null)paintChecker();return;}
        checkerKey=key;cancelChecker();checkerResult=null;checkerStatus.setTextColor(muted);
        if(!DnsVpnService.active||config==null){checkerStatus.setText("Чекер: DNS выключен");return;}
        if(DnsVpnService.switching){checkerStatus.setText("Чекер: ждём смены DNS");return;}
        if(!ProviderCheck.available(config.provider)){checkerStatus.setText("Чекер: для "+Provider.at(config.provider).name+" пока не подключён");return;}
        if(!manual&&!prefs.getBoolean("providerChecker",true)){checkerStatus.setText("Чекер: автопроверка выключена · проверить");return;}
        if(!resumed){checkerKey="";return;}
        checkerStatus.setText("Чекер: проверяем "+Provider.at(config.provider).name+"…");int generation=checkerGeneration;
        checkerTask=checkerWorker.submit(()->{try(ProviderCheck.SystemIo io=new ProviderCheck.SystemIo(this)){
            checkerIo=io;if(Thread.currentThread().isInterrupted())return;ProviderCheck.Result result=ProviderCheck.run(config.provider,io);
            handler.post(()->{if(isDestroyed()||generation!=checkerGeneration||!key.equals(checkerConnectionKey()))return;checkerResult=result;paintChecker();AppJournal.record("Чекер провайдера",result.report());});
        }catch(InterruptedException e){Thread.currentThread().interrupt();}finally{checkerIo=null;}});
    }
    private void paintChecker(){checkerStatus.setText(checkerResult.caption());checkerStatus.setTextColor(checkerResult.state==ProviderCheck.State.CONFIRMED?Color.rgb(35,160,110):checkerResult.state==ProviderCheck.State.NOT_CONFIRMED?Color.rgb(210,120,60):muted);}
    private void showChecker(){
        CardDialog dialog=new CardDialog("Чекер провайдера",checkerResult==null?checkerStatus.getText()+"\n\nПроверка выполняется через текущий DNS Android, без подстановки сервера. Не проверяет собственный DoH браузера и ответы ИИ.":checkerResult.report());
        if(DnsVpnService.active&&DnsVpnService.current!=null&&ProviderCheck.available(DnsVpnService.current.provider))dialog.action("Проверить снова",true,true,()->updateChecker(true));
        if(checkerResult!=null){final String report=checkerResult.report();dialog.action("Скопировать результат",false,false,()->{android.content.ClipboardManager clipboard=(android.content.ClipboardManager)getSystemService(CLIPBOARD_SERVICE);clipboard.setPrimaryClip(ClipData.newPlainText("Smart DNS · чекер",report));});}
        dialog.action("Закрыть",false,true,()->{});dialog.show();
    }
    private void showSettings(){
        CardDialog dialog=new CardDialog("Настройки","Провайдеры, автоматические проверки и обслуживание приложения.");
        dialog.panel.addView(text("Подключение",16,ink,true));
        dialog.action("DNS-провайдеры и автосмена",false,true,this::showDnsSettings);
        dialog.panel.getChildAt(dialog.panel.getChildCount()-1).setTag("dns-settings");
        Switch checkerOption=new Switch(this);checkerOption.setText("Автопроверка чекером провайдера");checkerOption.setTextColor(ink);checkerOption.setTypeface(body);checkerOption.setChecked(prefs.getBoolean("providerChecker",true));dialog.panel.addView(checkerOption);
        checkerOption.setOnCheckedChangeListener((view,on)->{prefs.edit().putBoolean("providerChecker",on).apply();checkerKey="";updateChecker(false);});
        dialog.panel.addView(text("Запрос к сайту выбранного провайдера после подключения или смены DNS. Это не проверка входа или ответа ИИ. Нажмите строку чекера на главном для подробностей.",12,muted,false));
        addCatalogSettings(dialog);
        dialog.panel.addView(text("Обслуживание",16,ink,true));
        dialog.action("Добавить виджет",false,true,this::chooseWidget);dialog.panel.getChildAt(dialog.panel.getChildCount()-1).setId(R.id.add_widget);
        dialog.action("Сетевые настройки Android",false,true,()->{try{startActivity(new Intent(Settings.ACTION_WIRELESS_SETTINGS));}catch(ActivityNotFoundException ignored){}});
        dialog.panel.getChildAt(dialog.panel.getChildCount()-1).setId(R.id.network_settings);
        updateButton=secondaryButton(getString(R.string.check_updates));updateButton.setId(R.id.check_updates);dialog.panel.addView(updateButton);updateButtonState();
        updateButton.setOnClickListener(v->{dialog.dialog.dismiss();handler.post(this::updateClicked);});
        dialog.action("Скачать журнал (.txt)",false,true,this::exportJournal);
        dialog.action("Закрыть",true,true,()->{});dialog.show();
    }
    private void addCatalogSettings(CardDialog dialog){
        Switch auto=new Switch(this);auto.setText("Обновлять адреса DNS автоматически");auto.setTextColor(ink);auto.setTypeface(body);auto.setChecked(catalog.automatic());auto.setMinHeight(dp(48));auto.setTag("catalog-auto");dialog.panel.addView(auto);
        auto.setOnCheckedChangeListener((view,on)->catalog.automatic(on));
        TextView status=text(catalog.summary(),12,muted,false);status.setTag("catalog-status");dialog.panel.addView(status);
        TextView check=text("Проверить каталог сейчас",14,accent,false);check.setTag("catalog-check");check.setMinHeight(dp(44));dialog.panel.addView(check);
        check.setOnClickListener(view->{check.setEnabled(false);status.setText("Проверяем каталог…");catalog.check(true,()->{if(!isDestroyed()&&dialog.dialog.isShowing()){check.setEnabled(true);status.setText(catalog.summary());}});});
    }
    private void exportJournal(){exportJournal=AppJournal.report();Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("text/plain").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,"SmartDNS-Android-"+BuildConfig.VERSION_NAME+".txt");try{startActivityForResult(intent,402);}catch(ActivityNotFoundException error){message("Сохранение недоступно","На устройстве нет системного приложения для выбора файла.");}}
    private void showDnsSettings(){
        CardDialog dialog=new CardDialog("Настройки DNS","Галочка — показывать провайдера и использовать в Авто. Скрытие не выключает текущее подключение. Задержка — время DNS-ответа, а не скорость интернета.");
        java.util.Set<String> hidden=hiddenProviders();java.util.Map<Integer,CheckBox> choices=new java.util.LinkedHashMap<>();java.util.Map<Integer,TextView> delays=new java.util.LinkedHashMap<>();
        Switch auto=new Switch(this);auto.setText("Автосмена при ошибках DNS · beta");auto.setTextColor(ink);auto.setTypeface(body);auto.setChecked(prefs.getBoolean("autoDns",false));dialog.panel.addView(auto);
        dialog.panel.addView(text("После 3 ошибок запросов пробует другой зашифрованный DNS. Не чаще раза в минуту, не более 3 подборов за 10 минут. Не читает другие приложения и не проверяет ответы ИИ.",12,muted,false));
        for(int id=0;id<Provider.ALL.length;id++)if(Provider.isAvailable(id)){final int provider=id;CheckBox check=new CheckBox(this);check.setText(Provider.at(id).name);check.setTextColor(ink);check.setTypeface(body);check.setChecked(!hidden.contains(String.valueOf(id)));check.setMinHeight(dp(48));dialog.panel.addView(check);choices.put(id,check);
            check.setTag("visibility-"+id);TextView delay=text("Задержка не измерена",12,muted,false);dialog.panel.addView(delay);delays.put(id,delay);
            if(Provider.at(id).url!=null){TextView info=text("О DNS · "+Provider.at(id).url,12,accent,false);info.setMinHeight(dp(40));info.setOnClickListener(v->{try{startActivity(new Intent(Intent.ACTION_VIEW,android.net.Uri.parse(Provider.at(provider).url)));}catch(ActivityNotFoundException ignored){}});dialog.panel.addView(info);}
        }
        Button measure=secondaryButton("Измерить задержку DNS");dialog.panel.addView(measure);measure.setOnClickListener(v->{measure.setEnabled(false);tests.submit(()->{try(UpstreamResolver resolver=new UpstreamResolver(this,null)){
            java.util.List<DnsConfig> configs=new java.util.ArrayList<>();for(int id:choices.keySet())configs.add(DnsConfig.saved(prefs,id));
            java.util.List<DnsSelection.Result> results=DnsSelection.check(configs,c->{long began=System.nanoTime();DnsWire.requireAddress(resolver.resolve(DnsWire.query("example.com"),c));return (System.nanoTime()-began)/1000000;},30000);
            handler.post(()->{if(isDestroyed()||!dialog.dialog.isShowing())return;for(TextView delay:delays.values())delay.setText("Нет результата за 30 с");for(DnsSelection.Result r:results){delays.get(r.config.provider).setText(r.config.label()+" · "+r.label());AppJournal.record("Задержка DNS",Provider.at(r.config.provider).name+"; "+r.config.label()+"; "+r.label());}measure.setEnabled(true);});
        }catch(Exception e){handler.post(()->{if(!isDestroyed()&&dialog.dialog.isShowing()){measure.setEnabled(true);dialog.description.setText(AppJournal.scrub(e.getMessage()));}});}});});

        dialog.action("Сохранить",true,false,()->{java.util.Set<String> next=new java.util.HashSet<>();for(java.util.Map.Entry<Integer,CheckBox> row:choices.entrySet())if(!row.getValue().isChecked())next.add(String.valueOf(row.getKey()));if(next.size()==choices.size()){dialog.description.setText("Оставьте хотя бы одного видимого провайдера.");return;}prefs.edit().putStringSet("hiddenProviders",next).putBoolean("autoDns",auto.isChecked()).apply();AppJournal.record("Настройки","Видимых DNS="+(choices.size()-next.size())+"; автосмена="+auto.isChecked());dialog.dialog.dismiss();});
        dialog.action("Отмена",false,true,()->{});dialog.show();
    }
    static void applyInsets(ScrollView scroll,WindowInsets insets) {
        if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());scroll.setPadding(bars.left,bars.top,bars.right,bars.bottom);}
        else scroll.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
    }
    private void chooseWidget(){
        CardDialog dialog=new CardDialog("Виджеты Smart DNS","Выберите размер. Если лаунчер не показывает окно добавления, используйте ручной способ ниже.");
        dialog.action("Компактный · 2 × 1",true,false,()->pinWidget(dialog,CompactDnsWidget.class));
        dialog.action("Расширенный · 4 × 2",false,false,()->pinWidget(dialog,DnsWidget.class));
        dialog.action("Как добавить вручную",false,true,()->message("Добавить виджет вручную",widgetInstructions()));
        dialog.action("Закрыть",false,true,()->{});dialog.show();
    }
    private String widgetInstructions(){return getString(R.string.widget_manual_instructions);}
    private void pinWidget(CardDialog dialog,Class<?> type){
        AppWidgetManager manager=AppWidgetManager.getInstance(this);
        WidgetPinning.Result result=WidgetPinning.request(new WidgetPinning.Launcher(){
            public boolean supported(){return manager.isRequestPinAppWidgetSupported();}
            public boolean request(){return manager.requestPinAppWidget(new ComponentName(MainActivity.this,type),null,null);}
        });
        dialog.description.setText(getString(result==WidgetPinning.Result.REQUESTED?R.string.widget_pin_requested:R.string.widget_pin_manual,widgetInstructions()));
    }
    private void updateButtonState(){
        if(updateButton==null||updater==null)return;
        switch(updater.phase){
            case CHECKING:updateButton.setText(R.string.update_checking);break;
            case VERIFYING:updateButton.setText(R.string.update_verifying);break;
            case DOWNLOADING:updateButton.setText(getString(R.string.update_download_button,updater.percent));break;
            case AVAILABLE:updateButton.setText(getString(R.string.update_available_button,updater.release.version));break;
            case READY:updateButton.setText(getString(R.string.update_install_button,updater.release.version));break;
            default:updateButton.setText(R.string.check_updates);
        }
        updateButton.setEnabled(updater.phase!=UpdateController.Phase.CHECKING&&updater.phase!=UpdateController.Phase.VERIFYING);
    }
    private void updateClicked(){
        if(updater.phase==UpdateController.Phase.READY){requestUpdateInstall();return;}
        if(updater.phase==UpdateController.Phase.AVAILABLE){showUpdatePrompt(true);return;}
        if(updater.phase==UpdateController.Phase.DOWNLOADING){
            CardDialog dialog=new CardDialog("Загрузка обновления","APK загружается в кэш приложения. При закрытии экрана загрузка продолжится, пока Android сохраняет процесс приложения.");
            dialog.action("Продолжить загрузку",true,true,()->{});dialog.action("Отменить загрузку",false,true,()->{updater.later();updater.cancelDownload();});dialog.show();return;
        }
        manualUpdateCheck=true;updater.check(true);
    }
    private void onUpdateChanged(UpdateController update){
        updateButtonState();if(!resumed||isFinishing()||isDestroyed())return;
        if(preparingInstall){if(update.phase==UpdateController.Phase.ERROR){preparingInstall=false;message("APK не прошёл проверку",update.error);}return;}
        if(installAfterPermission&&update.phase==UpdateController.Phase.READY){installAfterPermission=false;requestUpdateInstall();return;}
        if(manualUpdateCheck&&!update.busy()){
            manualUpdateCheck=false;
            if(update.phase==UpdateController.Phase.IDLE)message("Обновлений нет","У вас последняя доступная Android-версия "+BuildConfig.VERSION_NAME+".");
            else if(update.phase==UpdateController.Phase.ERROR)message("Не удалось обновить",update.error+"\n\nПовторите проверку позже.");
            else showUpdatePrompt(true);
        }else if(update.phase==UpdateController.Phase.ERROR&&update.release!=null&&updatePromptKey.startsWith("DOWNLOADING")){
            updatePromptKey="";message("Не удалось загрузить APK",update.error);
        }else showUpdatePrompt(false);
    }
    private void showUpdatePrompt(boolean force){
        if(updater==null||!resumed||isFinishing()||isDestroyed()||updater.release==null||installAfterPermission||!dialogs.isEmpty()||providerDialog!=null)return;
        boolean ready=updater.phase==UpdateController.Phase.READY;
        if(!ready&&updater.phase!=UpdateController.Phase.AVAILABLE)return;
        String key=updater.phase+":"+updater.release.version;
        if(!force&&(key.equals(updatePromptKey)||!updater.shouldPrompt()))return;updatePromptKey=key;
        AndroidUpdate.Release offered=updater.release;
        CardDialog dialog=new CardDialog(ready?"Обновление готово":"Доступно обновление",getString(ready?R.string.update_ready:R.string.update_offer,updater.release.version,BuildConfig.VERSION_NAME));
        dialog.onDismiss=()->{if(!isChangingConfigurations())updater.later();};
        dialog.action(ready?"Установить":"Обновить",true,true,()->{updater.remindNow();if(updater.release!=offered){updatePromptKey="";handler.post(()->showUpdatePrompt(true));return;}if(ready)requestUpdateInstall();else{updatePromptKey="DOWNLOADING:"+offered.version;updater.download();}});
        dialog.action("Позже",false,true,updater::later);dialog.show();
    }
    private void requestUpdateInstall(){
        if(updater.phase!=UpdateController.Phase.READY)return;preparingInstall=true;
        updater.verifyForInstall(()->{
            preparingInstall=false;
            if(!resumed||isFinishing()||isDestroyed())return;
            if(!getPackageManager().canRequestPackageInstalls()){
                CardDialog dialog=new CardDialog("Разрешите установку обновления","Android требует разрешить установку из приложения «Код Всего · Smart DNS». В следующем окне включите разрешение и вернитесь сюда.");
                dialog.action("Открыть настройки",true,true,()->{try{installAfterPermission=true;startActivityForResult(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,android.net.Uri.parse("package:"+getPackageName())),202);}catch(ActivityNotFoundException e){installAfterPermission=false;message("Откройте настройки вручную","Разрешите установку из Smart DNS в настройках Android → Приложения → Специальный доступ → Установка неизвестных приложений.");}});
                dialog.action("Позже",false,true,updater::later);dialog.show();return;
            }
            try{
                android.net.Uri uri=UpdateFileProvider.uri(this);Intent install=new Intent(Intent.ACTION_INSTALL_PACKAGE).setDataAndType(uri,"application/vnd.android.package-archive").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                install.setClipData(ClipData.newRawUri("Smart DNS APK",uri));updater.later();startActivity(install);
            }catch(ActivityNotFoundException|SecurityException e){message("Не удалось открыть установку","Системный установщик недоступен. Попробуйте скачать APK вручную из GitHub Releases.");}
        });
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
    @Override protected void onResume(){super.onResume();resumed=true;if(catalog!=null)catalog.check(false,()->{if(!isDestroyed()){lastUiState=null;updateState();}});updatePromptKey="";settleMotion();lastUiState=null;updateState();connect.resumeUi();handler.removeCallbacks(refresh);handler.post(refresh);DnsWidget.updateAll(this);updater.attach(updateListener);android.net.ConnectivityManager network=getSystemService(android.net.ConnectivityManager.class);if(network!=null&&network.getActiveNetwork()!=null)updater.check(false);}
    @Override protected void onPause(){resumed=false;cancelChecker();checkerKey="";updater.detach(updateListener);handler.removeCallbacks(refresh);settleMotion();if(connect!=null)connect.pauseUi();super.onPause();}
    @Override protected void onSaveInstanceState(Bundle state){state.putBoolean("installAfterPermission",installAfterPermission);super.onSaveInstanceState(state);}
    @Override protected void onDestroy(){updater.detach(updateListener);if(providerDialog!=null)providerDialog.dismiss();for(Dialog dialog:new java.util.ArrayList<>(dialogs))dialog.dismiss();handler.removeCallbacksAndMessages(null);cancelChecker();checkerWorker.shutdownNow();tests.shutdownNow();super.onDestroy();}
}

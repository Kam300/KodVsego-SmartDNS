package ru.kodvsego.smartdns;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.*;
import android.view.*;
import android.widget.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.BiConsumer;

/** A prominent, expandable comparison card. Results live only in this screen and are invalidated on network/catalog changes. */
final class ServiceHealthPanel extends LinearLayout {
    private final int ink,muted,accent,surface;private final BiConsumer<String,String> details;
    private final Handler handler=new Handler(Looper.getMainLooper());private final LinearLayout board,rows,services,protocols;
    private final TextView summary;private final Button run,expand;
    private final Map<String,TextView> labels=new HashMap<>();private ServiceHealth.Result[][] results;
    private final Set<ServiceHealth.SystemProbe> probes=ConcurrentHashMap.newKeySet();private ExecutorService pool,coordinator;
    private int protocol=DnsConfig.DOH,chosen=0;private volatile int generation;private boolean expanded,running;private String networkStamp="";private Provider[] catalog;
    boolean busy(){return running;}
    ServiceHealthPanel(Context context,int ink,int muted,int accent,int surface,BiConsumer<String,String> details){
        super(context);this.ink=ink;this.muted=muted;this.accent=accent;this.surface=surface;this.details=details;setOrientation(VERTICAL);setPadding(dp(16),dp(14),dp(16),dp(14));setTag("service-health-panel");setBackground(shape(surface,20));
        TextView title=label("Карта сервисов",19,ink,true);addView(title);
        addView(label("Сразу видно, какие DNS стоит попробовать",13,muted,false));
        summary=label("Ещё не проверено · результаты только для вашей сети",12,muted,false);summary.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);addView(summary);
        expand=button("Сравнить все DNS",accent);expand.setTag("service-health-expand");addView(expand,params(-1,48,10));
        board=new LinearLayout(context);board.setOrientation(VERTICAL);board.setVisibility(GONE);addView(board);
        services=new LinearLayout(context);HorizontalScrollView scroller=new HorizontalScrollView(context);scroller.setHorizontalScrollBarEnabled(false);scroller.addView(services);board.addView(scroller,params(-1,-2,12));
        protocols=new LinearLayout(context);board.addView(protocols,params(-1,-2,10));
        run=button("Проверить все DNS",accent);run.setTag("service-health-run");board.addView(run,params(-1,48,12));run.setOnClickListener(v->{if(running)cancel("Проверка остановлена");else start();});
        rows=new LinearLayout(context);rows.setOrientation(VERTICAL);board.addView(rows,params(-1,-2,12));
        board.addView(label("Ответ есть ≠ вход или генерация. «Нужен вход» — сервис ответил, но аккаунт/API-ключ не проверялся. Нажмите статус для деталей.",12,muted,false),params(-1,-2,12));
        expand.setOnClickListener(v->{expanded=!expanded;board.setVisibility(expanded?VISIBLE:GONE);expand.setText(expanded?"Скрыть карту":"Сравнить все DNS");if(expanded){freshness();paintChoices();paintRows();}});
        reset();
    }
    private int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}
    private GradientDrawable shape(int color,int radius){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));d.setStroke(dp(1),Color.argb(45,110,150,175));return d;}
    private LayoutParams params(int width,int height,int top){LayoutParams p=new LayoutParams(width<0?width:dp(width),height<0?height:dp(height));p.topMargin=dp(top);return p;}
    private TextView label(String text,int size,int color,boolean bold){TextView v=new TextView(getContext());v.setText(text);v.setTextColor(color);v.setTextSize(size);v.setTypeface(Typeface.create("sans-serif",bold?Typeface.BOLD:Typeface.NORMAL));v.setPadding(0,dp(4),0,dp(4));return v;}
    private Button button(String text,int color){Button b=new Button(getContext());b.setText(text);b.setAllCaps(false);b.setTextColor(color==accent?(Color.luminance(accent)>.25?0xff09232d:Color.WHITE):ink);b.setTextSize(14);b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x2602a5cf),shape(color,14),null));b.setPadding(dp(10),dp(4),dp(10),dp(4));return b;}
    private String stamp(){try{return PhysicalNetwork.find(getContext()).toString();}catch(Exception error){return "offline";}}
    private void reset(){catalog=Provider.ALL;networkStamp=stamp();results=new ServiceHealth.Result[catalog.length][ServiceHealth.TARGETS.length];for(int p=0;p<catalog.length;p++)for(int s=0;s<ServiceHealth.TARGETS.length;s++)results[p][s]=ServiceHealth.initial(p,s,protocol);}
    void freshness(){if(catalog!=Provider.ALL||!networkStamp.equals(stamp())){cancel("Сеть или каталог изменились · повторите проверку");reset();paintRows();}}
    private void paintChoices(){
        services.removeAllViews();for(int s=-1;s<ServiceHealth.TARGETS.length;s++){final int target=s;Button b=button(s<0?"Все сервисы":ServiceHealth.TARGETS[s].name,chosen==s?accent:surface);LayoutParams p=params(-2,48,0);p.rightMargin=dp(8);services.addView(b,p);b.setTag("health-service-"+s);b.setOnClickListener(v->{if(running)cancel("Выбор изменён · повторите проверку");chosen=target;paintChoices();paintRows();});}
        protocols.removeAllViews();String[] names={"Обычный","DoH","DoT"};for(int mode=0;mode<3;mode++){final int m=mode;Button b=button(names[mode],mode==protocol?accent:surface);LayoutParams p=new LayoutParams(0,dp(48),1);if(mode>0)p.leftMargin=dp(8);protocols.addView(b,p);b.setTag("health-protocol-"+mode);b.setOnClickListener(v->{if(running)cancel("Протокол изменён");protocol=m;reset();summary.setText("Протокол изменён · результаты сброшены");paintChoices();paintRows();});}
    }
    private int color(ServiceHealth.State state){switch(state){case RESPONSE:return Color.rgb(32,174,123);case FAILED:return Color.rgb(226,100,127);case LOGIN:case CHECKING:return accent;case BROWSER:return Color.rgb(154,127,227);case UNKNOWN:return Color.rgb(190,126,169);default:return muted;}}
    private void paintRows(){
        if(results==null)return;rows.removeAllViews();labels.clear();
        for(int p=0;p<catalog.length;p++)if(Provider.isAvailable(p)){
            LinearLayout row=new LinearLayout(getContext());row.setOrientation(VERTICAL);row.setPadding(dp(12),dp(8),dp(12),dp(8));row.setBackground(shape(surface,14));rows.addView(row,params(-1,-2,8));row.addView(label(catalog[p].name,15,ink,true));
            for(int s=0;s<ServiceHealth.TARGETS.length;s++)if(chosen<0||chosen==s){final int provider=p,target=s;TextView cell=label("",13,muted,false);cell.setMinHeight(dp(48));cell.setFocusable(true);cell.setTag("health-cell-"+p+"-"+s);row.addView(cell);labels.put(p+":"+s,cell);cell.setOnClickListener(v->{ServiceHealth.Result r=results[provider][target];details.accept(catalog[provider].name+" · "+ServiceHealth.TARGETS[target].name,r.caption()+"\n\n"+r.detail+(r.checked==0?"":"\n\nПроверено: "+android.text.format.DateFormat.format("HH:mm:ss",r.checked)));});paintCell(p,s);}
        }
    }
    private void paintCell(int p,int s){TextView view=labels.get(p+":"+s);if(view==null)return;ServiceHealth.Result result=results[p][s];view.setText((chosen<0?ServiceHealth.TARGETS[s].name+"  ·  ":"")+result.caption()+(result.checked==0?"":"  ·  "+android.text.format.DateFormat.format("HH:mm",result.checked)));view.setTextColor(color(result.state));view.setContentDescription(catalog[p].name+", "+ServiceHealth.TARGETS[s].name+", "+result.caption());}
    private void start(){
        freshness();try{PhysicalNetwork.requireProbeNetwork(getContext());}catch(Exception error){reset();paintRows();summary.setText(error.getMessage());return;}
        final int epoch=++generation,mode=protocol;final String network=networkStamp;final Provider[] snapshot=catalog;List<int[]> jobs=new ArrayList<>();
        for(int p=0;p<catalog.length;p++)for(int s=0;s<ServiceHealth.TARGETS.length;s++)if((chosen<0||chosen==s)&&ServiceHealth.initial(p,s,mode).state==ServiceHealth.State.UNTESTED){jobs.add(new int[]{p,s});results[p][s]=new ServiceHealth.Result(ServiceHealth.State.CHECKING,"Проверка выполняется…");paintCell(p,s);}
        if(jobs.isEmpty()){summary.setText("Нет заявленных провайдеров для этого сервиса и протокола");return;}
        running=true;run.setText("Остановить");summary.setText("Проверяем… 0 / "+jobs.size());pool=Executors.newFixedThreadPool(3);coordinator=Executors.newSingleThreadExecutor();final ExecutorService workers=pool,owner=coordinator;
        coordinator.submit(()->{CompletionService<int[]> completion=new ExecutorCompletionService<>(workers);int done=0;long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(120);
            try{for(int[] job:jobs)completion.submit(()->{ServiceHealth.Result result;try(ServiceHealth.SystemProbe probe=new ServiceHealth.SystemProbe(getContext(),new DnsConfig(job[0],mode))){probes.add(probe);try{if(epoch!=generation||Thread.currentThread().isInterrupted())throw new InterruptedException();result=ServiceHealth.check(ServiceHealth.TARGETS[job[1]],probe);}finally{probes.remove(probe);}}
                catch(InterruptedException cancelled){Thread.currentThread().interrupt();return job;}catch(Exception error){result=new ServiceHealth.Result(ServiceHealth.State.FAILED,AppJournal.scrub(error.getMessage()),System.currentTimeMillis());}
                final ServiceHealth.Result ready=result;handler.post(()->{if(epoch!=generation||catalog!=snapshot||!stamp().equals(network))return;results[job[0]][job[1]]=ready;paintCell(job[0],job[1]);});return job;});
                while(done<jobs.size()){long left=deadline-System.nanoTime();if(left<=0)break;Future<int[]> finished=completion.poll(left,TimeUnit.NANOSECONDS);if(finished==null)break;finished.get();final int completed=++done;handler.post(()->{if(epoch==generation)summary.setText("Готово "+completed+" / "+jobs.size());});}
            }catch(InterruptedException cancelled){Thread.currentThread().interrupt();}catch(ExecutionException ignored){}
            final boolean complete=done==jobs.size();handler.post(()->{if(epoch==generation){cancel(complete?"Проверка завершена · "+android.text.format.DateFormat.format("HH:mm",System.currentTimeMillis()):"Время истекло · часть проверок не завершена");if(complete){int answered=0,failed=0;for(int p=0;p<results.length;p++)for(int s=0;s<results[p].length;s++)if(chosen<0||chosen==s){ServiceHealth.State state=results[p][s].state;if(state==ServiceHealth.State.RESPONSE||state==ServiceHealth.State.LOGIN)answered++;if(state==ServiceHealth.State.FAILED)failed++;}summary.setText((chosen<0?"Все сервисы":ServiceHealth.TARGETS[chosen].name)+" · "+answered+" ответов · "+failed+" с проблемой · "+android.text.format.DateFormat.format("HH:mm",System.currentTimeMillis()));}}});owner.shutdown();
        });
    }
    void cancel(String message){generation++;running=false;if(pool!=null)pool.shutdownNow();if(coordinator!=null)coordinator.shutdownNow();for(ServiceHealth.SystemProbe probe:probes)probe.close();probes.clear();run.setText("Проверить все DNS");if(results!=null)for(int p=0;p<results.length;p++)for(int s=0;s<results[p].length;s++)if(results[p][s].state==ServiceHealth.State.CHECKING){results[p][s]=new ServiceHealth.Result(ServiceHealth.State.CANCELLED,"Проверка не завершена. Повторите её.");paintCell(p,s);}summary.setText(message);}
    @Override protected void onDetachedFromWindow(){cancel("Проверка остановлена · повторите при необходимости");handler.removeCallbacksAndMessages(null);super.onDetachedFromWindow();}
}

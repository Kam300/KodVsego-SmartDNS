package ru.kodvsego.smartdns;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.*;
import android.view.*;
import android.view.accessibility.AccessibilityNodeInfo;
import android.view.animation.DecelerateInterpolator;

/** One drawable control: no layouts or bitmap allocations during animation frames. */
public final class PowerButton extends View {
    private static final int YELLOW=Color.rgb(255,226,99), BLACK=Color.rgb(21,29,34);
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG), glow=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF arc=new RectF();
    private final int surface,accent,muted;
    private final Typeface heading;
    private ValueAnimator transition,spinner;
    private float progress,angle;
    private boolean connected,working,initialized;

    public PowerButton(Context context) {
        this(context,Color.rgb(18,18,27),YELLOW,Color.rgb(168,164,188),Typeface.DEFAULT_BOLD);
    }

    PowerButton(Context context,int surface,int accent,int muted,Typeface heading) {
        super(context);this.surface=surface;this.accent=accent;this.muted=muted;this.heading=heading;
        setClickable(true);setFocusable(true);setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_YES);
    }
    private float dp(float n){return n*getResources().getDisplayMetrics().density;}
    private static int blend(int a,int b,float t) {
        return Color.rgb((int)(Color.red(a)+(Color.red(b)-Color.red(a))*t),
            (int)(Color.green(a)+(Color.green(b)-Color.green(a))*t),
            (int)(Color.blue(a)+(Color.blue(b)-Color.blue(a))*t));
    }
    void setState(boolean connected,boolean working) {
        if(initialized&&this.connected==connected&&this.working==working)return;
        boolean first=!initialized;initialized=true;this.connected=connected;this.working=working;
        setContentDescription(working?"Отменить подключение DNS":connected?"Выключить DNS":"Включить DNS");
        if(transition!=null)transition.cancel();
        float target=connected?1:0;
        if(first||!ValueAnimator.areAnimatorsEnabled()||!isAttachedToWindow())progress=target;
        else {
            transition=ValueAnimator.ofFloat(progress,target);transition.setDuration(220);
            transition.setInterpolator(new DecelerateInterpolator());
            transition.addUpdateListener(a->{progress=(float)a.getAnimatedValue();invalidate();});transition.start();
        }
        updateSpinner();invalidate();
    }
    private void updateSpinner() {
        boolean animate=working&&isAttachedToWindow()&&getWindowVisibility()==VISIBLE&&ValueAnimator.areAnimatorsEnabled();
        if(!animate){if(spinner!=null){spinner.cancel();spinner=null;}return;}
        if(spinner!=null)return;
        spinner=ValueAnimator.ofFloat(0,360);spinner.setDuration(1100);spinner.setRepeatCount(ValueAnimator.INFINITE);
        spinner.setInterpolator(new android.view.animation.LinearInterpolator());
        spinner.addUpdateListener(a->{angle=(float)a.getAnimatedValue();invalidate();});spinner.start();
    }
    @Override protected void onSizeChanged(int w,int h,int oldw,int oldh) {
        super.onSizeChanged(w,h,oldw,oldh);
        glow.setShader(new RadialGradient(w/2f,h/2f,Math.max(1,Math.min(w,h)/2f),new int[]{0x38FFE263,0x00FFE263},null,Shader.TileMode.CLAMP));
    }
    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);float cx=getWidth()/2f,cy=getHeight()/2f,r=Math.min(cx,cy)-dp(16);
        glow.setAlpha((int)(255*progress));canvas.drawCircle(cx,cy,r+dp(15),glow);
        paint.setStyle(Paint.Style.FILL);paint.setColor(blend(surface,YELLOW,progress));canvas.drawCircle(cx,cy,r,paint);
        paint.setStyle(Paint.Style.STROKE);paint.setStrokeWidth(dp(1.5f));paint.setColor(blend(accent,YELLOW,progress));canvas.drawCircle(cx,cy,r,paint);
        if(working){paint.setStrokeWidth(dp(3));paint.setStrokeCap(Paint.Cap.ROUND);arc.set(cx-r+dp(8),cy-r+dp(8),cx+r-dp(8),cy+r-dp(8));canvas.drawArc(arc,angle,85,false,paint);}
        int glyph=blend(accent,BLACK,progress);paint.setColor(glyph);paint.setStrokeWidth(dp(6));paint.setStrokeCap(Paint.Cap.ROUND);
        float icon=dp(28),y=cy-dp(13);arc.set(cx-icon,y-icon,cx+icon,y+icon);canvas.drawArc(arc,-60,300,false,paint);
        canvas.drawLine(cx,y-dp(35),cx,y-dp(5),paint);
        paint.setStyle(Paint.Style.FILL);paint.setTypeface(heading);paint.setTextSize(dp(working?10:14));paint.setTextAlign(Paint.Align.CENTER);
        paint.setColor(blend(muted,BLACK,progress));canvas.drawText(working?"ПОДКЛЮЧАЕМ":connected?"ВКЛ":"ВЫКЛ",cx,cy+dp(51),paint);
    }
    @Override protected void drawableStateChanged() {
        super.drawableStateChanged();
        if(isAttachedToWindow()&&ValueAnimator.areAnimatorsEnabled()) {
            float scale=isPressed()?.97f:1;
            animate().scaleX(scale).scaleY(scale).setDuration(isPressed()?100:160).start();
        }
    }
    @Override public boolean performClick(){return super.performClick();}
    @Override public void onInitializeAccessibilityNodeInfo(AccessibilityNodeInfo info){super.onInitializeAccessibilityNodeInfo(info);info.setClassName(android.widget.Button.class.getName());}
    @Override protected void onAttachedToWindow(){super.onAttachedToWindow();updateSpinner();}
    @Override protected void onWindowVisibilityChanged(int visibility){super.onWindowVisibilityChanged(visibility);updateSpinner();}
    @Override protected void onDetachedFromWindow(){if(transition!=null)transition.cancel();if(spinner!=null){spinner.cancel();spinner=null;}animate().cancel();super.onDetachedFromWindow();}
}

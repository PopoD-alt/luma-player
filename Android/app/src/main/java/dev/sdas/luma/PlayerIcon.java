package dev.sdas.luma;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;

/** Small vector icons, drawn at any density without font-dependent glyphs. */
public final class PlayerIcon extends Drawable {
    public enum Kind { PLAY, PAUSE, PREVIOUS, NEXT, BACK, CLOSE, PLAYLIST, FULLSCREEN, EXIT_FULLSCREEN, REWIND, FORWARD, VOLUME, BRIGHTNESS, COMPUTER, PHONE, RETRY }
    private Kind kind;private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);private int color,alpha=255;
    public PlayerIcon(Kind kind,int color){this.kind=kind;this.color=color;}
    public void setKind(Kind value){kind=value;invalidateSelf();}
    @Override public void draw(Canvas canvas){
        int save=canvas.save();float size=Math.min(getBounds().width(),getBounds().height());
        canvas.translate(getBounds().centerX()-size/2,getBounds().centerY()-size/2);canvas.scale(size/24,size/24);
        paint.setColor(color);paint.setAlpha(alpha);paint.setStyle(Paint.Style.STROKE);paint.setStrokeWidth(1.8f);paint.setStrokeCap(Paint.Cap.ROUND);paint.setStrokeJoin(Paint.Join.ROUND);
        switch(kind){
            case PLAY: triangle(canvas,7,4,20,12,7,20);break;
            case PAUSE:paint.setStyle(Paint.Style.FILL);canvas.drawRoundRect(6,4,10,20,1,1,paint);canvas.drawRoundRect(14,4,18,20,1,1,paint);break;
            case PREVIOUS:canvas.drawLine(5,5,5,19,paint);triangle(canvas,19,5,8,12,19,19);break;
            case NEXT:canvas.drawLine(19,5,19,19,paint);triangle(canvas,5,5,16,12,5,19);break;
            case BACK:path(canvas,15,5,8,12,15,19);break;
            case CLOSE:canvas.drawLine(6,6,18,18,paint);canvas.drawLine(18,6,6,18,paint);break;
            case PLAYLIST:canvas.drawLine(4,6,20,6,paint);canvas.drawLine(4,11,15,11,paint);canvas.drawLine(4,16,12,16,paint);triangle(canvas,17,14,22,18,17,22);break;
            case FULLSCREEN:path(canvas,9,4,4,4,4,9);path(canvas,15,4,20,4,20,9);path(canvas,4,15,4,20,9,20);path(canvas,20,15,20,20,15,20);break;
            case EXIT_FULLSCREEN:path(canvas,4,9,9,9,9,4);path(canvas,15,4,15,9,20,9);path(canvas,4,15,9,15,9,20);path(canvas,15,20,15,15,20,15);break;
            case REWIND:case FORWARD:
                if(kind==Kind.FORWARD){canvas.translate(24,0);canvas.scale(-1,1);}
                canvas.drawArc(new RectF(3,3,21,21),-75,285,false,paint);path(canvas,3,3,3,9,9,9);
                if(kind==Kind.FORWARD){canvas.scale(-1,1);canvas.translate(-24,0);}
                paint.setStyle(Paint.Style.FILL);paint.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));paint.setTextAlign(Paint.Align.CENTER);paint.setTextSize(8);canvas.drawText("10",12,15,paint);break;
            case VOLUME:path(canvas,4,9,8,9,13,5,13,19,8,15,4,15,4,9);canvas.drawArc(new RectF(9,7,19,17),-65,130,false,paint);canvas.drawArc(new RectF(7,3,23,21),-55,110,false,paint);break;
            case BRIGHTNESS:canvas.drawCircle(12,12,4,paint);for(int i=0;i<8;i++){double angle=i*Math.PI/4;canvas.drawLine(12+(float)Math.cos(angle)*7,12+(float)Math.sin(angle)*7,12+(float)Math.cos(angle)*9.5f,12+(float)Math.sin(angle)*9.5f,paint);}break;
            case COMPUTER:canvas.drawRoundRect(3,4,21,16,2,2,paint);canvas.drawLine(12,16,12,20,paint);canvas.drawLine(8,20,16,20,paint);break;
            case PHONE:canvas.drawRoundRect(7,2,17,22,2,2,paint);canvas.drawLine(10,18,14,18,paint);break;
            case RETRY:canvas.drawArc(new RectF(4,4,20,20),-45,290,false,paint);path(canvas,14,3,20,3,20,9);break;
        }
        canvas.restoreToCount(save);
    }
    private void triangle(Canvas c,float...p){Paint.Style old=paint.getStyle();paint.setStyle(Paint.Style.FILL);path(c,p);paint.setStyle(old);}
    private void path(Canvas c,float...points){Path p=new Path();p.moveTo(points[0],points[1]);for(int i=2;i<points.length;i+=2)p.lineTo(points[i],points[i+1]);if(paint.getStyle()==Paint.Style.FILL)p.close();c.drawPath(p,paint);}
    @Override public void setAlpha(int alpha){this.alpha=Math.max(0,Math.min(255,alpha));invalidateSelf();}
    @Override public void setColorFilter(ColorFilter filter){paint.setColorFilter(filter);invalidateSelf();}
    @Override public int getOpacity(){return PixelFormat.TRANSLUCENT;}

    /** A real accessible Button with no visible text and a density-independent icon. */
    public static final class Button extends android.widget.Button {
        private final PlayerIcon icon;private final boolean prominent;
        public Button(Context context,Kind kind,String label,boolean prominent){
            super(context);this.prominent=prominent;icon=new PlayerIcon(kind,prominent?0xff11131b:0xfff7f8ff);
            setText("");setContentDescription(label);setMinWidth(0);setMinimumWidth(0);setMinHeight(0);setMinimumHeight(0);setPadding(0,0,0,0);setAllCaps(false);setStateListAnimator(null);
            GradientDrawable fill=new GradientDrawable();fill.setColor(prominent?0xfff2f4fa:0x66080b12);fill.setShape(GradientDrawable.OVAL);if(!prominent)fill.setStroke(Math.max(1,Math.round(context.getResources().getDisplayMetrics().density)),0x16ffffff);
            GradientDrawable mask=new GradientDrawable();mask.setColor(0xffffffff);mask.setShape(GradientDrawable.OVAL);
            setBackground(new RippleDrawable(ColorStateList.valueOf(prominent?0x3011131b:0x36ffffff),fill,mask));
        }
        public void setAction(Kind kind,String label){icon.setKind(kind);setContentDescription(label);invalidate();}
        @Override protected void onDraw(Canvas canvas){super.onDraw(canvas);int size=Math.round((prominent?30:24)*getResources().getDisplayMetrics().density);icon.setBounds((getWidth()-size)/2,(getHeight()-size)/2,(getWidth()+size)/2,(getHeight()+size)/2);int save=canvas.save();if(!isEnabled())canvas.saveLayerAlpha(0,0,getWidth(),getHeight(),75);icon.draw(canvas);canvas.restoreToCount(save);}
    }
}

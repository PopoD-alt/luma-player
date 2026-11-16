package dev.sdas.luma;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.LinearGradient;
import android.graphics.Outline;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.RadialGradient;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.Shader;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;

/** Native glass surfaces: translucent tints, soft light and a fine luminous rim. */
public final class LiquidGlass {
    private LiquidGlass() {}
    public static Drawable panel(Context context,float radiusDp){return new Surface(context,radiusDp,0,false,false);}
    public static Drawable strongPanel(Context context,float radiusDp){return new Surface(context,radiusDp,1,false,false);}
    public static Drawable outline(Context context,float radiusDp){return new Surface(context,radiusDp,2,false,false);}
    public static Drawable posterPlaceholder(Context context,float radiusDp){return new Surface(context,radiusDp,1,false,true);}
    public static Drawable control(Context context,float radiusDp,boolean accent){
        GradientDrawable mask=new GradientDrawable();mask.setColor(0xffffffff);mask.setCornerRadius(radiusDp*context.getResources().getDisplayMetrics().density);
        return new RippleDrawable(ColorStateList.valueOf(0x32ffffff),new Surface(context,radiusDp,0,accent,false),mask);
    }
    private static final class Surface extends Drawable {
        private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF bounds=new RectF(),inner=new RectF();
        private final float radius,density;private final int kind;private final boolean accent,placeholder;
        private Shader fill,rim,rose,blue;private int alpha=255;private ColorFilter colorFilter;
        Surface(Context context,float radiusDp,int kind,boolean accent,boolean placeholder){
            density=context.getResources().getDisplayMetrics().density;radius=radiusDp*density;this.kind=kind;this.accent=accent;this.placeholder=placeholder;
        }
        @Override protected void onBoundsChange(Rect value){
            bounds.set(value);float width=Math.max(1,bounds.width()),height=Math.max(1,bounds.height());
            int[] colors=kind==1?new int[]{0xe547405e,0xed282a43,0xf01a2136}:accent?new int[]{0xbcb6a2ca,0xb56c5c8b,0xc14d536f}:new int[]{0x9d77738f,0xad44425f,0xc02b344e};
            fill=new LinearGradient(bounds.left,bounds.top,bounds.right,bounds.bottom,colors,null,Shader.TileMode.CLAMP);
            rim=new LinearGradient(bounds.left,bounds.top,bounds.right,bounds.bottom,new int[]{0xc7f5edff,0x507d86af,0x477d8aa4,0x91c2c3d9},new float[]{0,.35f,.7f,1},Shader.TileMode.CLAMP);
            rose=new RadialGradient(bounds.left+width*.14f,bounds.top+height*.12f,Math.max(width*.58f,height),new int[]{accent?0x85f5a8c4:0x3cf1a8cc,0x00f1a8cc},null,Shader.TileMode.CLAMP);
            blue=new RadialGradient(bounds.right,bounds.bottom,Math.max(width*.6f,height),new int[]{0x3b86b9e3,0x0086b9e3},null,Shader.TileMode.CLAMP);
        }
        @Override public void draw(Canvas canvas){
            if(bounds.isEmpty())return;paint.setColorFilter(colorFilter);paint.setAlpha(alpha);paint.setStyle(Paint.Style.FILL);
            if(kind!=2){
                paint.setShader(fill);canvas.drawRoundRect(bounds,radius,radius,paint);
                paint.setShader(rose);canvas.drawRoundRect(bounds,radius,radius,paint);
                paint.setShader(blue);canvas.drawRoundRect(bounds,radius,radius,paint);
            }
            float stroke=Math.max(1,density*.75f);inner.set(bounds);inner.inset(stroke/2,stroke/2);paint.setStyle(Paint.Style.STROKE);paint.setStrokeWidth(stroke);paint.setShader(rim);canvas.drawRoundRect(inner,Math.max(0,radius-stroke/2),Math.max(0,radius-stroke/2),paint);
            if(kind!=2){
                int save=canvas.save();canvas.clipRect(bounds.left,bounds.top,bounds.right,bounds.top+Math.max(density*3,radius));inner.inset(density,density);paint.setShader(null);paint.setColor(0x2cffffff);paint.setAlpha(Math.round(alpha*.18f));canvas.drawRoundRect(inner,Math.max(0,radius-density),Math.max(0,radius-density),paint);canvas.restoreToCount(save);
            }
            if(placeholder){
                paint.setShader(null);paint.setStyle(Paint.Style.FILL);paint.setColor(0x54ece8ff);paint.setAlpha(Math.round(alpha*.33f));float cx=bounds.centerX(),cy=bounds.centerY(),size=Math.min(bounds.width(),bounds.height())*.17f;
                canvas.drawCircle(cx,cy,size*1.1f,paint);paint.setColor(0xfff0edf8);paint.setAlpha(Math.round(alpha*.65f));Path triangle=new Path();triangle.moveTo(cx-size*.22f,cy-size*.43f);triangle.lineTo(cx+size*.48f,cy);triangle.lineTo(cx-size*.22f,cy+size*.43f);triangle.close();canvas.drawPath(triangle,paint);
            }
            paint.setShader(null);
        }
        @Override public void getOutline(Outline outline){outline.setRoundRect(getBounds(),radius);outline.setAlpha(alpha/255f);}
        @Override public void setAlpha(int value){alpha=Math.max(0,Math.min(255,value));invalidateSelf();}
        @Override public void setColorFilter(ColorFilter value){colorFilter=value;invalidateSelf();}
        @Override public int getOpacity(){return PixelFormat.TRANSLUCENT;}
    }
}

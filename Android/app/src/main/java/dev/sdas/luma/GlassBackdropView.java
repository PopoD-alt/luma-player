package dev.sdas.luma;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.RadialGradient;
import android.graphics.Shader;
import android.view.View;

/** A quiet, static glow backdrop. It uses no artwork or animation behind text. */
public final class GlassBackdropView extends View {
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);
    private Shader base,violet,rose,blue,warm;
    public GlassBackdropView(Context context){super(context);setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);}
    @Override protected void onSizeChanged(int width,int height,int oldWidth,int oldHeight){
        float w=Math.max(1,width),h=Math.max(1,height),size=Math.max(w,h);
        base=new LinearGradient(0,0,w,h,new int[]{0xff101226,0xff1b1b32,0xff101627},new float[]{0,.52f,1},Shader.TileMode.CLAMP);
        violet=glow(w*.08f,h*.28f,size*.48f,0x805d3a91);
        rose=glow(w*.93f,h*.35f,size*.4f,0x7f94475e);
        blue=glow(w*.04f,h*.94f,size*.38f,0x65467196);
        warm=glow(w*.7f,h*.78f,size*.33f,0x625a2d61);
    }
    private Shader glow(float x,float y,float radius,int color){return new RadialGradient(x,y,radius,new int[]{color,color&0x00ffffff},null,Shader.TileMode.CLAMP);}
    @Override protected void onDraw(Canvas canvas){
        super.onDraw(canvas);if(base==null)return;
        for(Shader shader:new Shader[]{base,violet,rose,blue,warm}){paint.setShader(shader);canvas.drawRect(0,0,getWidth(),getHeight(),paint);}paint.setShader(null);
    }
}

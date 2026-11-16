package dev.sdas.luma;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.view.View;

/** A transient vertical level indicator, hidden except during a gesture. */
final class GestureLevelView extends View {
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);private int level;
    GestureLevelView(Context context){super(context);setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);}
    void setLevel(int value){level=Math.max(0,Math.min(100,value));invalidate();}
    @Override protected void onDraw(Canvas c){float width=getWidth(),height=getHeight(),track=Math.max(3,4*getResources().getDisplayMetrics().density),left=(width-track)/2;paint.setColor(0x40ffffff);c.drawRoundRect(left,0,left+track,height,track,track,paint);paint.setColor(0xfff5f5ff);c.drawRoundRect(left,height*(1-level/100f),left+track,height,track,track,paint);}
}

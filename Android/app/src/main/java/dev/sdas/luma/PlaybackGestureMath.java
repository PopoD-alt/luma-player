package dev.sdas.luma;

/** Pure gesture policy; native seek is committed by the UI only on ACTION_UP. */
public final class PlaybackGestureMath {
    private PlaybackGestureMath(){}
    public enum Axis { NONE, HORIZONTAL, VERTICAL }
    public static Axis axis(float dx,float dy,float slop){
        if(Math.max(Math.abs(dx),Math.abs(dy))<slop)return Axis.NONE;
        if(Math.abs(dx)>Math.abs(dy)*1.2f)return Axis.HORIZONTAL;
        if(Math.abs(dy)>Math.abs(dx)*1.2f)return Axis.VERTICAL;
        return Math.max(Math.abs(dx),Math.abs(dy))<slop*3?Axis.NONE:Math.abs(dx)>=Math.abs(dy)?Axis.HORIZONTAL:Axis.VERTICAL;
    }
    public static long seek(long start,float deltaX,float width,long length){
        if(length<=0||width<=0||!Float.isFinite(deltaX)||!Float.isFinite(width))return Math.max(0,start);
        double span=Math.min(length,120000);double candidate=start+deltaX/width*span;
        return (long)Math.max(0,Math.min(length,candidate));
    }
    public static int level(int start,float deltaY,float height){
        if(height<=0||!Float.isFinite(deltaY)||!Float.isFinite(height))return Math.max(0,Math.min(100,start));
        return Math.max(0,Math.min(100,Math.round(start-deltaY/height*150)));
    }
}

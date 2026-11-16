package dev.sdas.luma;

/** Platform-independent bounds used by the Android PiP presentation. */
public final class PipPlaybackPolicy {
    private PipPlaybackPolicy() {}
    public static double aspect(double width,double height,double sampleAspect){
        double value=width>0&&height>0&&Double.isFinite(width)&&Double.isFinite(height)&&Double.isFinite(sampleAspect)&&sampleAspect>0?width/height*sampleAspect:16.0/9;
        return Math.max(1.0/2.39,Math.min(2.39,value));
    }
    public static boolean eligible(boolean supported,boolean visible,boolean media,boolean nativeReady,boolean video,boolean failed){
        return supported&&visible&&media&&nativeReady&&video&&!failed;
    }
}

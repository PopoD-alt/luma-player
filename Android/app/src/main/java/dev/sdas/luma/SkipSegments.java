package dev.sdas.luma;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import org.json.JSONArray;
import org.json.JSONObject;

/** Local skip policy. A range must have real boundaries; no guessed fixed jumps. */
public final class SkipSegments {
    private SkipSegments() {}
    public static final class Range {
        public final String kind,source,label;
        public final double startSeconds,endSeconds;
        public Range(String kind,double start,double end,String source,String label) {
            this.kind=kind;this.startSeconds=start;this.endSeconds=end;this.source=source;this.label=label;
        }
        public String key(){return kind+":"+Math.round(startSeconds*1000)+":"+Math.round(endSeconds*1000);}
        public boolean contains(double time){return Double.isFinite(time)&&time>=startSeconds&&time<endSeconds-.1;}
        public String title(){return "intro".equals(kind)?"Skip intro":"Skip credits";}
    }
    public static final class Chapter {
        public final String name;public final double startSeconds,durationSeconds;
        public Chapter(String name,double start,double duration){this.name=name;this.startSeconds=start;this.durationSeconds=duration;}
    }
    public static String kind(String label) {
        if(label==null||label.length()>256)return null;
        String text=label.toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]+"," ").trim().replaceAll("\\s+"," ");
        if(text.matches("(?:intro|opening|op)(?: \\d{1,2})?")||text.matches("(?:opening credits|opening song|intro song)"))return "intro";
        if(text.matches("(?:credits|outro|ending|ed)(?: \\d{1,2})?")||text.matches("(?:end|ending|closing) credits|ending song"))return "credits";
        return null;
    }
    public static List<Range> validated(List<Range> input,double duration) {
        List<Range> candidates=new ArrayList<>();
        if(input==null)return Collections.emptyList();
        for(int i=0;i<Math.min(32,input.size());i++){
            Range r=input.get(i);if(r==null||!("intro".equals(r.kind)||"credits".equals(r.kind))||!("manual".equals(r.source)||"chapter".equals(r.source)))continue;
            if(!Double.isFinite(r.startSeconds)||!Double.isFinite(r.endSeconds)||r.startSeconds<0||r.endSeconds>31536000||r.endSeconds-r.startSeconds<.25)continue;
            if(Double.isFinite(duration)&&duration>0&&r.endSeconds>duration)continue;
            String label=r.label==null?null:r.label.substring(0,Math.min(64,r.label.length()));
            candidates.add(new Range(r.kind,r.startSeconds,r.endSeconds,r.source,label));
        }
        candidates.sort(Comparator.comparingDouble(r->r.startSeconds));List<Range> result=new ArrayList<>();
        for(int i=0;i<candidates.size()&&result.size()<8;i++){
            Range r=candidates.get(i);boolean overlap=false;
            for(int j=0;j<candidates.size();j++)if(i!=j&&r.startSeconds<candidates.get(j).endSeconds&&candidates.get(j).startSeconds<r.endSeconds){overlap=true;break;}
            if(!overlap)result.add(r);
        }
        return Collections.unmodifiableList(result);
    }
    public static List<Range> chapters(List<Chapter> input,double duration) {
        if(input==null||input.size()>1024||!Double.isFinite(duration)||duration<=0)return Collections.emptyList();
        List<Range> result=new ArrayList<>();
        for(int i=0;i<input.size();i++){
            Chapter c=input.get(i);String kind=kind(c.name);if(kind==null||!Double.isFinite(c.startSeconds)||c.startSeconds<0)continue;
            double end=duration;
            if(i+1<input.size()){
                double next=input.get(i+1).startSeconds;
                if(!Double.isFinite(next)||next<=c.startSeconds)continue;end=Math.min(end,next);
            }
            if(Double.isFinite(c.durationSeconds)&&c.durationSeconds>0)end=Math.min(end,c.startSeconds+c.durationSeconds);
            double length=end-c.startSeconds;
            if("intro".equals(kind)&&(length>Math.min(600,duration/2)||c.startSeconds>=duration/2))continue;
            if("credits".equals(kind)&&(length>1800||c.startSeconds<duration/2))continue;
            result.add(new Range(kind,c.startSeconds,end,"chapter",c.name));
        }
        return validated(result,duration);
    }
    /** User ranges override chapter ranges of that kind, including overlaps. */
    public static List<Range> resolve(List<Range> manual,List<Range> chapters,double duration){
        List<Range> result=new ArrayList<>(validated(manual,duration));Set<String> kinds=new HashSet<>();for(Range r:result)kinds.add(r.kind);
        for(Range c:validated(chapters,duration)){
            boolean overlap=false;for(Range r:result)if(r.startSeconds<c.endSeconds&&c.startSeconds<r.endSeconds)overlap=true;
            if(!kinds.contains(c.kind)&&!overlap)result.add(c);
        }
        return validated(result,duration);
    }
    public static Range active(List<Range> ranges,double time){for(Range r:ranges)if(r.contains(time))return r;return null;}
    public static List<Range> parse(JSONArray array,double duration)throws org.json.JSONException {
        if(array==null)return Collections.emptyList();if(array.length()>8)throw new org.json.JSONException("Too many skip ranges");
        List<Range> result=new ArrayList<>();
        for(int i=0;i<array.length();i++){
            JSONObject value=array.getJSONObject(i);Object start=value.opt("startSeconds"),end=value.opt("endSeconds");
            if(!(start instanceof Number)||!(end instanceof Number)||!(value.opt("kind") instanceof String)||!(value.opt("source") instanceof String))continue;
            Object label=value.opt("label");if(label!=null&&label!=JSONObject.NULL&&(!(label instanceof String)||((String)label).length()>64))continue;
            result.add(new Range(value.getString("kind"),((Number)start).doubleValue(),((Number)end).doubleValue(),value.getString("source"),label instanceof String?(String)label:null));
        }
        return validated(result,duration);
    }
    public static JSONArray json(List<Range> ranges)throws org.json.JSONException{
        JSONArray result=new JSONArray();for(Range r:ranges){JSONObject v=new JSONObject().put("kind",r.kind).put("startSeconds",r.startSeconds).put("endSeconds",r.endSeconds).put("source",r.source);if(r.label!=null)v.put("label",r.label);result.put(v);}return result;
    }
    public static final class Session {
        private final Set<String> consumed=new HashSet<>();
        private final List<Double> explicitSeekPositions=new ArrayList<>();
        public void reset(){consumed.clear();explicitSeekPositions.clear();}
        public void consume(Range range){if(range!=null)consumed.add(range.key());}
        private void rememberSeek(double time,List<Range> ranges){
            if(!Double.isFinite(time)||time<0||time>31536000)return;
            if(explicitSeekPositions.size()>=32)explicitSeekPositions.remove(0);
            explicitSeekPositions.add(time);consume(active(ranges,time));
        }
        public void userSeek(double time,List<Range> ranges){rememberSeek(time,ranges);}
        public void userSeek(double from,double target,List<Range> ranges){
            rememberSeek(from,ranges);rememberSeek(target,ranges);
        }
        public Range automatic(List<Range> ranges,double time,boolean enabled,boolean playing){
            for(double point:explicitSeekPositions)consume(active(ranges,point));
            Range r=active(ranges,time);if(!enabled||!playing||r==null||consumed.contains(r.key()))return null;
            consume(r);return r;
        }
    }
}

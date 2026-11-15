using System.Security.Cryptography;
using System.Text.Json;
using Luma.Core.Runtime;

namespace Luma.Core.Tests;
public static class RuntimeManifestTests
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"Luma-runtime-manifest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var files=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(string relative in new[]{"libvlc.dll","libvlccore.dll","plugins/codec/original.dll","Sources/CorrespondingSource.tar","Notices/COPYING.LIB"})
            {
                string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);byte[] bytes=System.Text.Encoding.UTF8.GetBytes("Original manifest fixture "+relative);File.WriteAllBytes(path,bytes);
                files[relative]=new{bytes=bytes.Length,sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()};
            }
            void Write(bool approved=true,bool development=false,Dictionary<string,object>? entries=null) => File.WriteAllText(Path.Combine(root,LibVlcRuntimeManifest.FileName),JsonSerializer.Serialize(new{schema=1,package="windows-original-test",redistributionApproved=approved,developmentOnly=development,files=entries??files}));
            Write();string sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"Sources/CorrespondingSource.tar")))).ToLowerInvariant();
            var approval=new LibVlcRuntimeApproval("windows-original-test",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,LibVlcRuntimeManifest.FileName)))).ToLowerInvariant(),sourceHash);
            LibVlcRuntimeApproval CurrentApproval()=>approval with { ManifestSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,LibVlcRuntimeManifest.FileName)))).ToLowerInvariant() };
            var valid=LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval());Check(valid.FileCount==5 && valid.Package=="windows-original-test","Central manifest and exact complete inventory accepted");
            Write(false,true);Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Development component cannot become an app-local deployment");
            Write(true,true);Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"A development-only runtime is rejected even with its approval bit set");
            Write();byte[] sameSize=File.ReadAllBytes(Path.Combine(root,"libvlc.dll"));sameSize[0]^=1;File.WriteAllBytes(Path.Combine(root,"libvlc.dll"),sameSize);Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Same-size binary hash mismatch rejected");
            File.WriteAllText(Path.Combine(root,"libvlc.dll"),"Original manifest fixture libvlc.dll");
            Write();File.AppendAllText(Path.Combine(root,"libvlc.dll"),"changed");Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Changed binary bytes/length rejected");
            File.WriteAllText(Path.Combine(root,"libvlc.dll"),"Original manifest fixture libvlc.dll");
            string extra=Path.Combine(root,"plugins/extra.dll");File.WriteAllText(extra,"unreviewed");Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Uninventoried plugin rejected");File.Delete(extra);
            var missingSource=new Dictionary<string,object>(files);missingSource.Remove("Sources/CorrespondingSource.tar");Write(entries:missingSource);Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Source companion required");
            foreach(string unsafePath in new[]{"../outside.dll","plugins\\other.dll","C:/outside.dll","plugins/CON.txt","plugins/./other.dll"})
            {var changed=new Dictionary<string,object>(files){[unsafePath]=files["libvlc.dll"]};Write(entries:changed);Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Unsafe inventory path rejected");}
            var duplicate=new Dictionary<string,object>(files){["LIBVLC.DLL"]=files["libvlc.dll"]};Write(entries:duplicate);Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,CurrentApproval()),"Case-insensitive collision rejected before Windows loading");
            Write();using(var canceled=new CancellationTokenSource()){canceled.Cancel();try{LibVlcRuntimeManifest.ValidateApproved(root,approval,canceled.Token);throw new InvalidOperationException("Cancellation ignored");}catch(OperationCanceledException){}}
            Write();var selfDeclared=new Dictionary<string,object>(files){["libvlc.dll"]=new{bytes=34,sha256=new string('1',64)}};Write(entries:selfDeclared);
            Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,approval),"A modified runtime cannot self-declare approval by rewriting its own manifest");
            Write();string external=Path.Combine(Path.GetTempPath(),"Luma-explicit-runtime-"+Guid.NewGuid().ToString("N")+".dll");File.WriteAllText(external,"Original replacement fixture");
            try
            {
                Check(LibVlcRuntimeManifest.ResolvePath(null,root,[external],approval)==Path.Combine(root,"libvlc.dll"),"Approved app-local deployment selected");
                Write(false,true);RejectLoad(()=>LibVlcRuntimeManifest.ResolvePath(null,root,[external],approval),"Invalid present bundle cannot silently select installed VLC");
                Check(LibVlcRuntimeManifest.ResolvePath(external,root,[],null)==external,"Explicit personal replacement remains available before bundle validation");
                RejectLoad(()=>LibVlcRuntimeManifest.ResolvePath(null,root,[external],null),"This build must pin its approved component before bundling");
                Check(LibVlcRuntimeManifest.ResolvePath(null,root+"-absent",[external])==external,"Absent bundle preserves separately installed fallback");
                RejectLoad(()=>LibVlcRuntimeManifest.ResolvePath("relative.dll",root,[external]),"Invalid explicit runtime is not silently replaced");
            }
            finally{File.Delete(external);}
            string manifest=Path.Combine(root,LibVlcRuntimeManifest.FileName);File.Delete(manifest);File.WriteAllText(Path.Combine(root,"redistribution-audit.json"),"{\"approvedForRedistribution\":true}");Reject(()=>LibVlcRuntimeManifest.ValidateApproved(root,approval),"Legacy unrelated approval file is not central approval");
            Console.WriteLine("PASS: approved central runtime manifest requires exact hashes/sizes, complete source/notices, no extra DLLs, safe paths and distinct Windows names; development-only/legacy/corrupt components rejected and cancellation honored");
        }
        finally{Directory.Delete(root,true);}
    }
    static void RejectLoad(Func<string> action,string label){try{_ = action();}catch(DllNotFoundException){return;}throw new InvalidOperationException("Runtime load: "+label);}
    static void Reject(Action action,string label){try{action();}catch(Exception error)when(error is InvalidDataException or IOException or JsonException){return;}throw new InvalidOperationException("Runtime manifest: "+label);}
    static void Check(bool value,string label){if(!value)throw new InvalidOperationException("Runtime manifest: "+label);}
}

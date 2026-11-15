using System.Runtime.InteropServices;
using Luma.WindowsApp;

namespace Luma.Core.Tests;
public static class PowerRequestTests
{
    public static void Run()
    {
        var api=new Fake();var lease=new SharingPowerRequest(api);
        lease.Update(false);Check(api.Created==0,"No lease merely because sharing or an auth connection exists");
        lease.Update(true);lease.Update(true);Check(lease.IsActive && api.Created==1 && api.Set==1,"One SystemRequired request across repeated stream polls");
        lease.Update(false);lease.Update(false);Check(!lease.IsActive && api.Cleared==1 && api.Closed==1,"Last stream releases and closes exactly once");
        lease.Update(true);lease.Dispose();lease.Dispose();lease.Update(true);Check(api.Created==2 && api.Cleared==2 && api.Closed==2,"Stop/close prevents reacquisition and cleans an active request");
        var failed=new Fake{SetSucceeds=false};using(var rejected=new SharingPowerRequest(failed))
        {rejected.Update(true);rejected.Update(true);Check(!rejected.IsActive && rejected.IsUnavailable && failed.Created==1 && failed.Closed==1 && failed.Cleared==0,"Declined request closes its handle and avoids repeated retry while the stream remains active");rejected.Update(false);rejected.Update(true);Check(failed.Created==2,"A later stream may retry after idle");}
        var unavailable=new Fake{Available=false};using(var rejected=new SharingPowerRequest(unavailable)){rejected.Update(true);Check(rejected.IsUnavailable && !rejected.IsActive && unavailable.Closed==0,"Unavailable native API has a truthful inactive fallback");}
        if(IntPtr.Size==8)
        {Check(Marshal.SizeOf<SharingPowerRequest.ReasonContext>()==32 && Marshal.OffsetOf<SharingPowerRequest.ReasonContext>(nameof(SharingPowerRequest.ReasonContext.Reason)).ToInt32()==8,"Full x64 REASON_CONTEXT includes the24byte detailed union at offset8");Check(Marshal.SizeOf<SharingPowerRequest.DetailedReason>()==24,"Detailed native union layout is preserved despite using simple reason text");}
        Console.WriteLine("PASS: streaming-only SystemRequired lease creates/clears/closes exactly once, does not retry unavailable requests per poll, has no display/away-mode request and matches native x64 union layout");
    }
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Power request: "+message);}
    sealed class Fake:SharingPowerRequest.IPowerApi
    {
        public int Created,Set,Cleared,Closed;public bool SetSucceeds=true,Available=true;
        public IntPtr Create(string reason){Created++;Check(reason.Contains("streaming",StringComparison.Ordinal),"Human-readable scoped reason");return Available?new IntPtr(Created):IntPtr.Zero;}
        public bool SetSystemRequired(IntPtr request){Set++;return SetSucceeds;}
        public bool ClearSystemRequired(IntPtr request){Cleared++;return true;}
        public bool Close(IntPtr request){Closed++;return true;}
    }
}

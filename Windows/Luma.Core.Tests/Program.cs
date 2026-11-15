namespace Luma.Core.Tests;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--sharing-test-host") return SharingTestHost.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
            var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("../Tests/Fixtures");
            DnsSdLifecycleTests.Run();
            SharingTests.Run(root);
            FoundationTests.Run(root);
            AudioTests.Run(root);
            VideoTests.Run(root);
            PcmContainerTests.Run(root);
            MatroskaTests.Run(root);
            TimelineTests.Run(root);
            IsoMediaTests.Run(root);
            NativePacketFixtures.Run(root);
            RemuxIntegrationTests.Run(root);
            Mp4InspectionTests.Run(root);
            HevcIssueFixtures.Run(root);
            HevcNormalizationTests.Run(root);
            Console.WriteLine("PASS: portable Windows core suites completed");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
}

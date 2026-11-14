namespace Luma.Core.Tests;

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("../Tests/Fixtures");
            FoundationTests.Run(root);
            AudioTests.Run(root);
            VideoTests.Run(root);
            Console.WriteLine("PASS: portable Windows core suites completed");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
}

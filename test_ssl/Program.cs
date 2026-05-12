using System;
using System.IO;
using SharpSvn;

Console.WriteLine("SharpSvn version: " + typeof(SvnClient).Assembly.GetName().Version);

var url = args.Length > 0 ? args[0] : "https://116.204.5.100/svn/repo2";

Console.WriteLine($"\nTesting: {url}");
Console.WriteLine(new string('-', 50));

// Test without SSL handler (expected to fail on self-signed cert)
Console.WriteLine("\n[1] Without SslServerTrustHandlers (expect failure):");
try
{
    using var client1 = new SvnClient();
    client1.Authentication.ForceCredentials("huangziyue", "Yksvn@123abcd");
    SvnInfoEventArgs? info = null;
    client1.Info(new Uri(url), new EventHandler<SvnInfoEventArgs>((s, e) => info = e));
    Console.WriteLine("  -> UNEXPECTED SUCCESS (cert was accepted?)");
}
catch (Exception ex)
{
    Console.WriteLine($"  -> {ex.GetType().Name}: {ex.Message}");
}

// Test with SSL handler (expected to succeed)
Console.WriteLine("\n[2] With SslServerTrustHandlers (expect success):");
try
{
    using var client2 = new SvnClient();
    client2.Authentication.SslServerTrustHandlers += (sender, e) =>
    {
        Console.WriteLine($"  [SSL Handler] Failures: {e.Failures}");
        e.AcceptedFailures = e.Failures;
        e.Save = true;
    };
    client2.Authentication.ForceCredentials("huangziyue", "Yksvn@123abcd");

    SvnInfoEventArgs? info = null;
    client2.Info(new Uri(url), new EventHandler<SvnInfoEventArgs>((s, e) => info = e));
    Console.WriteLine($"  -> SUCCESS: {info?.RepositoryRoot}");
}
catch (Exception ex)
{
    Console.WriteLine($"  -> {ex.GetType().Name}: {ex.Message}");
}

// Test with CreateClient-style helper
Console.WriteLine("\n[3] With CreateClient() helper (expect success):");
try
{
    SvnClient CreateClient()
    {
        var c = new SvnClient();
        c.Authentication.SslServerTrustHandlers += (sender, e) =>
        {
            Console.WriteLine($"  [SSL Handler] Failures: {e.Failures}");
            e.AcceptedFailures = e.Failures;
            e.Save = true;
        };
        return c;
    }

    using var client3 = CreateClient();
    client3.Authentication.ForceCredentials("huangziyue", "Yksvn@123abcd");
    SvnInfoEventArgs? info = null;
    client3.Info(new Uri(url), new EventHandler<SvnInfoEventArgs>((s, e) => info = e));
    Console.WriteLine($"  -> SUCCESS: {info?.RepositoryRoot}");
}
catch (Exception ex)
{
    Console.WriteLine($"  -> {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine("\nDone.");

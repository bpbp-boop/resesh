using Renci.SshNet;

// Isolates whether SSH.NET's keepalive is what kills connections to the FxSsh test server.

using (var quiet = new SshClient("127.0.0.1", 2200, "test", "test123"))
{
    quiet.HostKeyReceived += (_, e) => e.CanTrust = true;
    quiet.Connect();
    Console.WriteLine($"[probe] no-keepalive connected: {quiet.IsConnected}");
    Thread.Sleep(45_000);
    Console.WriteLine($"[probe] no-keepalive alive after 45s: {quiet.IsConnected}");
    quiet.Disconnect();
}

using (var chatty = new SshClient("127.0.0.1", 2200, "test", "test123"))
{
    chatty.HostKeyReceived += (_, e) => e.CanTrust = true;
    chatty.KeepAliveInterval = TimeSpan.FromSeconds(5);
    chatty.Connect();
    Console.WriteLine($"[probe] keepalive(5s) connected: {chatty.IsConnected}");
    Thread.Sleep(15_000);
    Console.WriteLine($"[probe] keepalive(5s) alive after 15s: {chatty.IsConnected}");
}

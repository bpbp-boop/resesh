using System.Text;
using FxSsh;
using FxSsh.Algorithms;
using FxSsh.Services;

// Throwaway local SSH echo server for exercising the Sessions terminal end-to-end.
// Listens on loopback:2200, accepts test/test123, echoes typed lines, and understands:
//   big   -> streams ~10 MB of numbered lines (throughput/batching test)
//   slow  -> ten ticks over ~6 s, first after 1.5 s (background-tab activity test)
//   codex -> emits Codex's default animated OSC 2 title (SSH agent-icon test)
//   shell -> emits a stock shell prompt OSC 2 title (agent-exit test)
//   bye   -> closes the channel from the server side
// Window-change requests are acknowledged in-band so resize plumbing is observable.

// Persist the host key across restarts so the client's known_hosts entry stays valid.
var keyPath = Path.Combine(AppContext.BaseDirectory, "hostkey.txt");
var hostKey = File.Exists(keyPath) ? new RsaKey(256, File.ReadAllText(keyPath)) : new RsaKey(256, null);
File.WriteAllText(keyPath, hostKey.ExportKey());
var server = new SshServer(new StartingInfo(System.Net.IPAddress.Loopback, 2200, "SSH-2.0-SessionsTestServer"));
server.AddHostKey("rsa-sha2-256", hostKey.ExportKey());
server.ExceptionRaised += (_, ex) => Console.WriteLine($"[server] exception: {ex.Message}");

server.ConnectionAccepted += (_, session) =>
{
    Console.WriteLine("[server] connection accepted");
    // FxSsh drops idle sockets after ~30s; its own keepalive keeps the link busy so
    // idle client sessions (client keepalive is 30s too — a race) survive.
    session.ConfigureKeepalive(TimeSpan.FromSeconds(10));
    session.ServiceRegistered += (_, service) =>
    {
        switch (service)
        {
            case UserAuthService auth:
                auth.UserAuth += (_, e) =>
                {
                    e.Result = e is { AuthMethod: "password", Username: "test", Password: "test123" };
                    Console.WriteLine($"[server] auth {e.Username}/{e.AuthMethod}: {e.Result}");
                };
                break;

            case ConnectionService connection:
                connection.PtyReceived += (_, pty) =>
                    Console.WriteLine($"[server] pty {pty.Terminal} {pty.WidthChars}x{pty.HeightRows}");
                connection.CommandOpened += (_, e) =>
                {
                    if (e.ShellType != "shell")
                        return;
                    e.Agreed = true;
                    var channel = e.Channel;
                    var lineBuffer = new StringBuilder();

                    void Send(string s) => channel.SendData(Encoding.UTF8.GetBytes(s));

                    Send("Welcome to the Sessions test server!\r\n");
                    Send("Commands: big (10 MB dump), slow (delayed ticks), codex and shell (agent-title tests), bye (server-side close). Anything else echoes.\r\n$ ");

                    channel.WindowChange += (_, wc) =>
                        Send($"\r\n[window-change: {wc.WidthColumns}x{wc.HeightRows}]\r\n$ ");

                    channel.DataReceived += (_, data) =>
                    {
                        var text = Encoding.UTF8.GetString(data.Span);
                        foreach (var ch in text)
                        {
                            if (ch is '\r' or '\n')
                            {
                                var line = lineBuffer.ToString();
                                lineBuffer.Clear();
                                Send("\r\n");
                                switch (line.Trim())
                                {
                                    case "":
                                        break;
                                    case "big":
                                        // Send from a worker so the session's receive pump keeps
                                        // processing the client's window-adjust messages; chunked
                                        // so each SendData stays well under window/packet limits.
                                        _ = Task.Run(() =>
                                        {
                                            try
                                            {
                                                Console.WriteLine("[server] big: start");
                                                var block = new StringBuilder();
                                                var sent = 0L;
                                                for (var i = 1; sent < 10_000_000; i++)
                                                {
                                                    block.Append($"line {i:D8} abcdefghijklmnopqrstuvwxyz0123456789\r\n");
                                                    if (block.Length >= 16_384)
                                                    {
                                                        var bytes = Encoding.UTF8.GetBytes(block.ToString());
                                                        channel.SendData(bytes);
                                                        sent += bytes.Length;
                                                        block.Clear();
                                                    }
                                                }
                                                Console.WriteLine($"[server] big: sent {sent} bytes");
                                                Send("[done]\r\n$ ");
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"[server] big failed: {ex}");
                                            }
                                        });
                                        break;
                                    case "codex":
                                        Send("\x1b]2;⠋ remote-project\x07");
                                        break;
                                    case "shell":
                                        Send("\x1b]2;root@rct-keep:/srv/rct-keep\x07");
                                        break;
                                    case "slow":
                                        // Delayed trickle: output that arrives well after the user
                                        // has switched away (exercises unseen-output indicators).
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                await Task.Delay(1500);
                                                for (var i = 1; i <= 10; i++)
                                                {
                                                    Send($"tick {i}\r\n");
                                                    await Task.Delay(500);
                                                }
                                                Send("[done]\r\n$ ");
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine($"[server] slow failed: {ex}");
                                            }
                                        });
                                        break;
                                    case "bye":
                                        Send("bye!\r\n");
                                        channel.SendClose(0);
                                        return;
                                    default:
                                        Send($"echo: {line}\r\n");
                                        break;
                                }
                                Send("$ ");
                            }
                            else if (ch == '\x7f')
                            {
                                if (lineBuffer.Length > 0)
                                {
                                    lineBuffer.Length--;
                                    Send("\b \b");
                                }
                            }
                            else
                            {
                                lineBuffer.Append(ch);
                                Send(ch.ToString()); // local echo
                            }
                        }
                    };
                };
                break;
        }
    };
};

server.Start();
Console.WriteLine("[server] listening on 127.0.0.1:2200 (test/test123). Ctrl+C to stop.");
await Task.Delay(Timeout.Infinite);

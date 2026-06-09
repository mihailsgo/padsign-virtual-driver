using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Padsign.Listener; // SignedPdfReceiver, PadsignConfig, FileLogger (compiled from the real source)

namespace Padsign.Listener.Tests;

/// <summary>
/// Self-contained integration test for the receive-back logic. Drives the REAL
/// SignedPdfReceiver against an in-process HttpListener stub that implements the
/// server contract (/signedPdf/pending, /signedPdf, /signedPdf/ack). No Docker,
/// no live ps-server, no GUI.
/// Run: dotnet run --project tests/Padsign.Listener.ReceiveBackTests -c Release
/// </summary>
internal static class TestRunner
{
    private static int _pass, _fail;

    private static void Check(string name, bool cond, string extra = "")
    {
        if (cond) { _pass++; Console.WriteLine("PASS " + name); }
        else { _fail++; Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(extra) ? "" : " :: " + extra)); }
    }

    private static async Task<int> Main()
    {
        var port = GetFreePort();
        using var stub = new StubServer(port);
        stub.Start();
        var root = Path.Combine(Path.GetTempPath(), "rbtest-cs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ScenarioCatchUp(port, stub, NewDirs(root, "s1"));
            await ScenarioPollUntilReady(port, stub, NewDirs(root, "s2"));
            await ScenarioNullDocidSkips(port, stub, NewDirs(root, "s3"));
            await ScenarioAckRetryNoRedownload(port, stub, NewDirs(root, "s4"));
        }
        catch (Exception ex) { _fail++; Console.WriteLine("EXCEPTION " + ex); }
        finally { stub.Stop(); try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static (string work, string outDir) NewDirs(string root, string name)
    {
        var work = Path.Combine(root, name, "work");
        var outDir = Path.Combine(root, name, "out");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outDir);
        return (work, outDir);
    }

    private static PadsignConfig MakeConfig(int port, string work, string outDir) => new()
    {
        ApiUrl = $"http://localhost:{port}/api/registerPDF",
        AuthenticationHeaderName = "Authorization",
        AuthenticationHeaderValue = "Bearer testkey",
        Email = "a@x.com",
        Company = "Amit",
        WorkingDirectory = work,
        SignedOutputPath = outDir,
        UploadTimeoutSeconds = 10,
        ReceiveBackEnabled = true,
        ReceiveBackPollSeconds = 1,
        ReceiveBackTimeoutMinutes = 1
    };

    private static SignedPdfReceiver MakeReceiver(PadsignConfig cfg)
        => new(cfg, new FileLogger(Path.Combine(cfg.WorkingDirectory, "logs", "test.log")));

    private static Dictionary<string, object> Item(string docid, string num, string filename, int size) => new()
    {
        ["docid"] = docid,
        ["documentNumber"] = num,
        ["signedAt"] = "2026-06-08T10:00:00.000Z",
        ["filename"] = filename,
        ["sizeBytes"] = size
    };

    // 1) Startup catch-up: pending -> download (save under X-Padsign-Filename) -> ack (drains pending).
    private static async Task ScenarioCatchUp(int port, StubServer stub, (string work, string outDir) d)
    {
        stub.Reset();
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 catchup");
        const string fn = "100542_2026.06.08_10_00_00.pdf";
        stub.SetDoc("D1", bytes, fn);
        stub.AddPending(Item("D1", "100542", fn, bytes.Length));

        await MakeReceiver(MakeConfig(port, d.work, d.outDir)).CatchUpAsync(CancellationToken.None);

        var saved = Path.Combine(d.outDir, fn);
        Check("catch-up: file saved under X-Padsign-Filename", File.Exists(saved), saved);
        Check("catch-up: saved bytes match", File.Exists(saved) && File.ReadAllText(saved) == "%PDF-1.4 catchup");
        Check("catch-up: downloaded exactly once", stub.DownloadCount("D1") == 1, "count=" + stub.DownloadCount("D1"));
        Check("catch-up: acked once", stub.AckCount == 1, "ackCount=" + stub.AckCount);
        Check("catch-up: pending drained after ack", stub.PendingCount == 0, "pending=" + stub.PendingCount);
        Check("catch-up: bearer auth sent", stub.LastAuth == "Bearer testkey", stub.LastAuth ?? "(null)");
    }

    // 2) Per-job poll waits until ITS docid appears, then delivers and returns (no hang to timeout).
    private static async Task ScenarioPollUntilReady(int port, StubServer stub, (string work, string outDir) d)
    {
        stub.Reset();
        var bytes = Encoding.UTF8.GetBytes("%PDF poll");
        const string fn = "200777_y.pdf";
        stub.SetDoc("D2", bytes, fn);
        // pending starts empty; becomes ready ~1.5s in (after the first poll tick)
        _ = Task.Run(async () => { await Task.Delay(1500); stub.AddPending(Item("D2", "200777", fn, bytes.Length)); });

        var start = DateTime.UtcNow;
        await MakeReceiver(MakeConfig(port, d.work, d.outDir)).PollAndFetchAsync("D2", "jobPoll", CancellationToken.None);
        var elapsed = (DateTime.UtcNow - start).TotalSeconds;

        Check("poll: waited then saved its doc", File.Exists(Path.Combine(d.outDir, fn)));
        Check("poll: downloaded D2 once", stub.DownloadCount("D2") == 1, "count=" + stub.DownloadCount("D2"));
        Check("poll: acked once", stub.AckCount == 1, "ackCount=" + stub.AckCount);
        Check("poll: returned well under timeout", elapsed < 30, "elapsed=" + elapsed.ToString("F1") + "s");
    }

    // 3) Null docid (upload returned no docId): skip the poll, never touch other jobs' pending docs.
    private static async Task ScenarioNullDocidSkips(int port, StubServer stub, (string work, string outDir) d)
    {
        stub.Reset();
        var bytes = Encoding.UTF8.GetBytes("%PDF other");
        stub.SetDoc("D3", bytes, "other.pdf");
        stub.AddPending(Item("D3", "300000", "other.pdf", bytes.Length)); // belongs to a different job

        var start = DateTime.UtcNow;
        await MakeReceiver(MakeConfig(port, d.work, d.outDir)).PollAndFetchAsync(null, "jobNull", CancellationToken.None);
        var elapsed = (DateTime.UtcNow - start).TotalSeconds;

        Check("null-docid: no download (does not drain others)", stub.DownloadCount("D3") == 0, "count=" + stub.DownloadCount("D3"));
        Check("null-docid: no ack", stub.AckCount == 0, "ackCount=" + stub.AckCount);
        Check("null-docid: nothing saved locally", Directory.GetFiles(d.outDir).Length == 0);
        Check("null-docid: returns immediately", elapsed < 5, "elapsed=" + elapsed.ToString("F1") + "s");
        Check("null-docid: other job's pending untouched", stub.PendingCount == 1, "pending=" + stub.PendingCount);
    }

    // 4) Ack fails the first time -> file kept, retried next pass WITHOUT re-downloading.
    private static async Task ScenarioAckRetryNoRedownload(int port, StubServer stub, (string work, string outDir) d)
    {
        stub.Reset();
        var bytes = Encoding.UTF8.GetBytes("%PDF ackretry");
        const string fn = "300888_z.pdf";
        stub.SetDoc("D4", bytes, fn);
        stub.AddPending(Item("D4", "300888", fn, bytes.Length));
        stub.AckFailuresRemaining = 1; // first ack -> HTTP 500

        var cfg = MakeConfig(port, d.work, d.outDir);
        var receiver = MakeReceiver(cfg);

        await receiver.CatchUpAsync(CancellationToken.None); // download + save; ack fails
        Check("ack-retry: saved despite ack failure", File.Exists(Path.Combine(d.outDir, fn)));
        Check("ack-retry: downloaded once (first pass)", stub.DownloadCount("D4") == 1, "count=" + stub.DownloadCount("D4"));
        Check("ack-retry: ack attempted once (failed)", stub.AckCount == 1, "ackCount=" + stub.AckCount);
        Check("ack-retry: saved-state recorded", File.Exists(Path.Combine(d.work, "received-pending-ack.json")));

        await receiver.CatchUpAsync(CancellationToken.None); // must NOT re-download; ack now succeeds
        Check("ack-retry: NOT re-downloaded on retry", stub.DownloadCount("D4") == 1, "count=" + stub.DownloadCount("D4"));
        Check("ack-retry: ack retried and succeeded", stub.AckCount == 2, "ackCount=" + stub.AckCount);
        Check("ack-retry: pending drained after success", stub.PendingCount == 0, "pending=" + stub.PendingCount);
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

/// <summary>In-process HTTP stub implementing the PadSign receive-back contract.</summary>
internal sealed class StubServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<Dictionary<string, object>> _pending = new();
    private readonly Dictionary<string, (byte[] bytes, string filename)> _docs = new();
    private readonly Dictionary<string, int> _downloadCount = new();

    public int AckFailuresRemaining;
    public int AckCount;
    public string? LastAuth;

    public StubServer(int port) => _listener.Prefixes.Add($"http://localhost:{port}/");
    public void Start() { _listener.Start(); _ = Task.Run(LoopAsync); }
    public void Stop() { try { _listener.Stop(); } catch { } }
    public void Dispose() { Stop(); ((IDisposable)_listener).Dispose(); }

    public void Reset() { lock (_gate) { _pending.Clear(); _docs.Clear(); _downloadCount.Clear(); AckFailuresRemaining = 0; AckCount = 0; LastAuth = null; } }
    public void SetDoc(string id, byte[] bytes, string filename) { lock (_gate) { _docs[id] = (bytes, filename); } }
    public void AddPending(Dictionary<string, object> item) { lock (_gate) { _pending.Add(item); } }
    public int PendingCount { get { lock (_gate) { return _pending.Count; } } }
    public int DownloadCount(string id) { lock (_gate) { return _downloadCount.TryGetValue(id, out var c) ? c : 0; } }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            lock (_gate) { LastAuth = ctx.Request.Headers["Authorization"]; }

            if (path.EndsWith("/signedPdf/pending"))
            {
                string json;
                lock (_gate) { json = JsonSerializer.Serialize(_pending); }
                WriteJson(ctx, 200, json);
            }
            else if (path.EndsWith("/signedPdf/ack"))
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream)) body = sr.ReadToEnd();
                lock (_gate)
                {
                    AckCount++;
                    if (AckFailuresRemaining > 0) { AckFailuresRemaining--; WriteJson(ctx, 500, "{\"error\":\"boom\"}"); }
                    else
                    {
                        string? docid = null;
                        try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("docid", out var e)) docid = e.GetString(); } catch { }
                        if (docid != null) _pending.RemoveAll(p => (string)p["docid"] == docid);
                        WriteJson(ctx, 200, "{\"acknowledged\":true,\"removed\":true}");
                    }
                }
            }
            else if (path.EndsWith("/signedPdf"))
            {
                var docid = ctx.Request.QueryString["docid"];
                (byte[] bytes, string filename) d = default;
                var found = false;
                lock (_gate)
                {
                    if (docid != null && _docs.TryGetValue(docid, out d))
                    {
                        found = true;
                        _downloadCount[docid] = (_downloadCount.TryGetValue(docid, out var c) ? c : 0) + 1;
                    }
                }
                if (found)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/pdf";
                    ctx.Response.AddHeader("X-Padsign-Filename", d.filename);
                    ctx.Response.AddHeader("X-Padsign-Document-Id", docid!);
                    ctx.Response.OutputStream.Write(d.bytes, 0, d.bytes.Length);
                }
                else WriteJson(ctx, 404, "{\"error\":\"not found\"}");
            }
            else WriteJson(ctx, 404, "{}");
        }
        catch { try { ctx.Response.StatusCode = 500; } catch { } }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    private static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(b, 0, b.Length);
    }
}

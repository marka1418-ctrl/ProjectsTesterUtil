using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// The same apphost serves as an inert CAM executable. The actual production
// tester launches it; no ENCY executable or native CAM library is used.
if (Environment.GetEnvironmentVariable("PROJECT_TESTER_FAKE_CAM_MARKERS") is { Length: > 0 } markers)
{
    Directory.CreateDirectory(markers);
    await File.WriteAllTextAsync(Path.Combine(markers, Environment.ProcessId + ".json"),
        JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Arguments = args }));
    await Task.Delay(TimeSpan.FromSeconds(60));
    return;
}

if (args is not ["--tester", var sourceTester])
    throw new ArgumentException("Usage: dotnet run --project tests/PackagedLaunchChecks -- --tester <published ProjectTestingUtility.exe>");
sourceTester = Path.GetFullPath(sourceTester);
Require(File.Exists(sourceTester), "Published tester does not exist.");

var sandbox = Path.Combine(Path.GetTempPath(), "ProjectsTesterUtil-PackagedLaunch-" + Guid.NewGuid().ToString("N"));
var testerDirectory = Path.Combine(sandbox, "tester");
var fakeDirectory = Path.Combine(sandbox, "fake-cam");
var markerDirectory = Path.Combine(sandbox, "markers");
Directory.CreateDirectory(testerDirectory);
Directory.CreateDirectory(fakeDirectory);
Directory.CreateDirectory(markerDirectory);
var copiedTester = Path.Combine(testerDirectory, "ProjectTestingUtility.exe");
File.Copy(sourceTester, copiedTester);

// Copy managed apphost dependencies, with a unique executable name so the
// production runner's prime-process cleanup cannot match a real application.
foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
    File.Copy(file, Path.Combine(fakeDirectory, Path.GetFileName(file)));
var fakeCam = Path.Combine(fakeDirectory, "LaunchCheckCam-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
File.Copy(Environment.ProcessPath!, fakeCam);
Require(!File.Exists(Path.Combine(fakeDirectory, "CAMIPC.Helper.Cam.dll")), "Fake CAM folder must not contain native CAM code.");
Require(!File.Exists(Path.Combine(testerDirectory, "tools", "ffmpeg", "ffmpeg.exe")) &&
        !File.Exists(Path.Combine(sandbox, "tools", "ffmpeg", "ffmpeg.exe")) &&
        !File.Exists(Path.Combine(testerDirectory, "ffmpeg.exe")), "Recorder must be absent: this check must never capture the desktop.");

// Raw loopback TCP avoids HTTP URL ACL registration and accepts no remote clients.
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var requests = new ConcurrentQueue<string>();
var projectBytes = Encoding.UTF8.GetBytes("Synthetic project bytes; never opened by ENCY.");
var localToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var server = ServeAsync(listener, requests, projectBytes, localToken, cancellation.Token);
await File.WriteAllTextAsync(Path.Combine(testerDirectory, "settings.json"), JsonSerializer.Serialize(new
{
    // Generated values are only accepted by the loopback mock, never a real service.
    Login = Guid.NewGuid().ToString("N"),
    Password = Guid.NewGuid().ToString("N"),
    BaseUrl = $"http://127.0.0.1:{port}",
    LaunchCamTimeoutInSeconds = 2,
    OpenProjectTimeoutInSeconds = 2,
    TestProjectTimeoutInSeconds = 2,
    KillCamProcessTimeoutInSeconds = 5,
    CountOfTestedProjectsBeforeRestart = 1,
    UseJobMode = true,
    ValidateBackgroundSimulation = false
}));

var start = new ProcessStartInfo(copiedTester)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = testerDirectory,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
start.ArgumentList.Add(fakeCam);
start.Environment["PATH"] = "";
start.Environment["PROJECT_TESTER_FAKE_CAM_MARKERS"] = markerDirectory;
start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
start.Environment.Remove("HTTP_PROXY");
start.Environment.Remove("HTTPS_PROXY");
start.Environment.Remove("ALL_PROXY");
using var tester = Process.Start(start) ?? throw new InvalidOperationException("Published tester did not start.");
var stdout = tester.StandardOutput.ReadToEndAsync();
var stderr = tester.StandardError.ReadToEndAsync();
Console.WriteLine($"Isolated packaged tester artifacts: {sandbox}");
try
{
    await tester.WaitForExitAsync(cancellation.Token);
    await File.WriteAllTextAsync(Path.Combine(sandbox, "tester-console.txt"), (await stdout) + (await stderr));
    Require(tester.ExitCode == 0, $"Tester exited with code {tester.ExitCode}; inspect isolated logs.");
    Require(requests.Count(value => value == "/api/v1/authenticate") == 1, "Real authorization did not reach the loopback mock exactly once.");
    Require(requests.Count(value => value == "/api/v2/projects/count") == 1 && requests.Count(value => value == "/api/v2/projects") == 1,
        "Real project list requests were not completed.");
    Require(requests.Count(value => value == "/api/v1/download-project/91001") == 1, "Retry must reuse the originally downloaded project.");
    Console.WriteLine("PASS real authorization, project listing and download against the loopback mock");

    var markerFiles = Directory.GetFiles(markerDirectory, "*.json");
    Require(markerFiles.Length == 2, $"Expected two real CAM launches, found {markerFiles.Length}.");
    var processIds = new List<int>();
    foreach (var marker in markerFiles)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        var id = document.RootElement.GetProperty("ProcessId").GetInt32();
        processIds.Add(id);
        Require(document.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "/JOB_MODE" }),
            "Actual CAM launch arguments did not preserve /JOB_MODE.");
        Require(!IsAlive(id), "Production tester left its fake CAM process running.");
    }
    Require(processIds.Distinct().Count() == 2, "Retry reused the same process.");
    Console.WriteLine("PASS production Process.Start launched two distinct fake CAM processes with /JOB_MODE and cleaned them up");

    using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(testerDirectory, "results", "91001.json")));
    var attempts = result.RootElement.GetProperty("Attempts").EnumerateArray().ToArray();
    Require(attempts.Length == 2 && result.RootElement.GetProperty("RetryOutcome").GetString() == "FailedAgain", "Expected exactly two recorded connection failures.");
    foreach (var attempt in attempts)
    {
        Require(!attempt.GetProperty("IsTested").GetBoolean() && attempt.GetProperty("FailedStage").GetString() == "Connect",
            "Failure occurred before real CAM process launch instead of at the missing native helper.");
        Require(attempt.GetProperty("Error").GetString()!.Contains("CAMIPC.Helper.Cam.dll", StringComparison.Ordinal), "Expected missing fake native helper error.");
        Require(attempt.GetProperty("VideoPath").ValueKind == JsonValueKind.Null && attempt.GetProperty("RecordingError").GetString()!.Contains("FFmpeg was not found", StringComparison.Ordinal),
            "Missing recorder must be non-fatal and must not capture video.");
    }
    var diagnosticDirectory = Path.Combine(testerDirectory, result.RootElement.GetProperty("DiagnosticsDirectory").GetString()!);
    Require(File.ReadAllBytes(Path.Combine(diagnosticDirectory, "original.stcp")).SequenceEqual(projectBytes), "Retained project differs from downloaded bytes.");
    Require(!Directory.EnumerateFiles(sandbox, "*.mkv", SearchOption.AllDirectories).Any(), "This check must not produce desktop video.");
    using var status = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(testerDirectory, "run-status.json")));
    Require(status.RootElement.GetProperty("State").GetString() == "Completed", "Completed test run did not publish its terminal state.");
    Console.WriteLine("PASS missing recorder does not block launches; both Connect failures retained and terminal status is Completed");
    Console.WriteLine("All packaged launch checks passed. No ENCY process, real project service or desktop recording was used.");
}
finally
{
    if (!tester.HasExited)
    {
        tester.Kill(entireProcessTree: true);
        await tester.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    cancellation.Cancel();
    listener.Stop();
    await server;
}

static async Task ServeAsync(TcpListener listener, ConcurrentQueue<string> requests, byte[] projectBytes, string token, CancellationToken cancellation)
{
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellation) ?? throw new IOException("Missing HTTP request line.");
            var target = requestLine.Split(' ')[1].Split('?')[0];
            var contentLength = 0;
            var authorized = false;
            while (await reader.ReadLineAsync(cancellation) is { Length: > 0 } line)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(line[15..].Trim());
                if (line.Equals("Authorization: Bearer " + token, StringComparison.OrdinalIgnoreCase))
                    authorized = true;
            }
            if (contentLength > 0)
            {
                var body = new char[contentLength];
                var received = 0;
                while (received < body.Length)
                {
                    var read = await reader.ReadAsync(body.AsMemory(received), cancellation);
                    if (read == 0)
                        throw new IOException("Incomplete HTTP request body.");
                    received += read;
                }
            }
            requests.Enqueue(target);
            var valid = target == "/api/v1/authenticate" || authorized;
            var payload = valid ? target switch
            {
                "/api/v1/authenticate" => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { access_token = token })),
                "/api/v2/projects/count" => Encoding.UTF8.GetBytes("1"),
                "/api/v2/projects" => Encoding.UTF8.GetBytes("{\"Content\":[{\"Id\":91001}]}"),
                "/api/v1/download-project/91001" => projectBytes,
                _ => Array.Empty<byte>()
            } : Array.Empty<byte>();
            var status = payload.Length == 0 ? "400 Bad Request" : "200 OK";
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellation);
            await stream.WriteAsync(payload, cancellation);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    catch (SocketException) when (cancellation.IsCancellationRequested) { }
    catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
}

static bool IsAlive(int id)
{
    try { using var process = Process.GetProcessById(id); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

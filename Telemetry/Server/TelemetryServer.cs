using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KSATelemetryOverlay.Rendering;

namespace KSATelemetryOverlay.Telemetry.Server;

public sealed class TelemetryServer : IDisposable
{
    private const string LogPrefix = "[KSATelemetryOverlay Server] ";
    private const double BroadcastInterval = 1.0 / 60.0; // 60 FPS max broadcast rate

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<WebSocket, byte> _sockets = new();
    private readonly ConcurrentDictionary<WebSocket, int> _sendingFlags = new();
    private readonly System.Diagnostics.Stopwatch _broadcastTimer = System.Diagnostics.Stopwatch.StartNew();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private string _webRoot = string.Empty;
    private string _fontRoot = string.Empty;
    private int _boundPort;
    private int _targetPort;
    private bool _desiredRun;
    private bool _startFailed;
    private string? _lastModDirectory;
    private string _lastError = string.Empty;
    private byte[]? _lastPacketBytes;

    public bool IsRunning => _listener is not null;
    public int ConnectedClients => _sockets.Count;
    public int Port => _boundPort;

    public string StatusText
    {
        get
        {
            if (!string.IsNullOrEmpty(_lastError))
            {
                return $"Error: {_lastError}";
            }

            if (!IsRunning)
            {
                return "Stopped";
            }

            int count = ConnectedClients;
            return $"Running on http://127.0.0.1:{_boundPort}/ ({count} client{(count == 1 ? "" : "s")})";
        }
    }

    public void EnsureState(bool shouldRun, int port, string? modDirectory)
    {
        _lastModDirectory = modDirectory;

        if (shouldRun != _desiredRun || port != _targetPort)
        {
            _desiredRun = shouldRun;
            _targetPort = port;
            _startFailed = false;

            if (shouldRun)
            {
                Start(port, modDirectory);
            }
            else
            {
                Stop();
            }
        }
        else if (shouldRun && !IsRunning && !_startFailed)
        {
            Start(port, modDirectory);
        }
    }

    public void Restart(string? modDirectory = null)
    {
        _startFailed = false;
        int port = _boundPort > 0 ? _boundPort : (_targetPort > 0 ? _targetPort : 8088);
        Start(port, modDirectory ?? _lastModDirectory);
    }

    public void Start(int port, string? modDirectory)
    {
        Stop();
        _lastError = string.Empty;
        _startFailed = false;
        _lastModDirectory = modDirectory;

        _webRoot = ResolveWebRoot(modDirectory);
        _fontRoot = ResolveFontRoot(modDirectory);

        try
        {
            // Bind dual-stack listener so both IPv4 (127.0.0.1) and IPv6 (::1/localhost) work seamlessly.
            // Unlike HttpListener (http.sys), TcpListener never requires administrator rights
            // or URL-ACL reservations, and functions reliably across Windows, Wine, Proton, and Linux.
            _listener = TcpListener.Create(port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            _boundPort = port;
            _cts = new CancellationTokenSource();
            _listenerTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));

            Console.WriteLine(LogPrefix + $"started on http://localhost:{port}/ (serving '{_webRoot}')");
        }
        catch (Exception ex)
        {
            _startFailed = true;
            _lastError = ex.Message;
            Console.WriteLine(LogPrefix + $"failed to start on port {port}: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        if (_cts is not null)
        {
            try
            {
                _cts.Cancel();
            }
            catch { }
            _cts.Dispose();
            _cts = null;
        }

        foreach (WebSocket ws in _sockets.Keys)
        {
            RemoveSocket(ws);
        }
        _sockets.Clear();
        _sendingFlags.Clear();

        if (_listener is not null)
        {
            try
            {
                _listener.Stop();
            }
            catch { }
            _listener = null;
        }

        _boundPort = 0;
    }

    public void Update(double dt, TelemetrySnapshot snapshot, OverlayConfig config)
    {
        if (!IsRunning)
        {
            return;
        }

        if (_broadcastTimer.Elapsed.TotalSeconds >= BroadcastInterval)
        {
            _broadcastTimer.Restart();
            BroadcastSnapshot(snapshot, config);
        }
    }

    private void BroadcastSnapshot(TelemetrySnapshot snapshot, OverlayConfig config)
    {
        try
        {
            TelemetryPacket packet = BuildPacket(snapshot, config);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, _jsonOptions);
            _lastPacketBytes = bytes;

            if (_sockets.IsEmpty)
            {
                return;
            }

            foreach (WebSocket ws in _sockets.Keys)
            {
                if (ws.State == WebSocketState.Open)
                {
                    _ = SendToSocketAsync(ws, bytes);
                }
                else
                {
                    RemoveSocket(ws);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"broadcast error: {ex.Message}");
        }
    }

    private async Task SendToSocketAsync(WebSocket ws, byte[] bytes)
    {
        // Prevent concurrent SendAsync on the same WebSocket instance
        if (!_sendingFlags.TryUpdate(ws, 1, 0))
        {
            // A previous frame send is still in flight; drop intermediate frame to avoid queue lag
            return;
        }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
            }
        }
        catch
        {
            RemoveSocket(ws);
        }
        finally
        {
            _sendingFlags.TryUpdate(ws, 0, 1);
        }
    }

    private void RemoveSocket(WebSocket ws)
    {
        _sockets.TryRemove(ws, out _);
        _sendingFlags.TryRemove(ws, out _);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            ws.Dispose();
        }
        catch { }
    }

    private static double FiniteOrZero(double v) => double.IsFinite(v) ? v : 0.0;
    private static float FiniteOrZero(float v) => float.IsFinite(v) ? v : 0f;

    private static TelemetryPacket BuildPacket(TelemetrySnapshot s, OverlayConfig config)
    {
        string missionName = config.MissionName ?? s.VehicleName;

        int engineCount = s.Engines.Count;
        EnginePacket[] engines = new EnginePacket[engineCount];
        for (int i = 0; i < engineCount; i++)
        {
            EngineSample sample = s.Engines[i];
            engines[i] = new EnginePacket
            {
                Status = sample.Status.ToString(),
                X = sample.DiagramX,
                Y = sample.DiagramY,
                Radius = sample.SizeFactor,
                Throttle = sample.Throttle,
                Burning = sample.IsBurning,
            };
        }

        EventPacket[] events = [];
        if (s.Events is not null)
        {
            var recorded = s.Events.Recorded;
            var predicted = s.Events.Predicted;
            events = new EventPacket[recorded.Count + predicted.Count];
            int idx = 0;
            for (int i = 0; i < recorded.Count; i++)
            {
                var ev = recorded[i];
                events[idx++] = new EventPacket
                {
                    Kind = ev.Kind.ToString(),
                    Label = ev.Label.ToString(),
                    Explainer = ev.Explainer.ToString(),
                    Time = FiniteOrZero(ev.MissionTime),
                    IsPrediction = false,
                };
            }
            for (int i = 0; i < predicted.Count; i++)
            {
                var ev = predicted[i];
                events[idx++] = new EventPacket
                {
                    Kind = ev.Kind.ToString(),
                    Label = ev.Label.ToString(),
                    Explainer = ev.Explainer.ToString(),
                    Time = FiniteOrZero(ev.MissionTime),
                    IsPrediction = true,
                };
            }
        }

        string[] leftSlots = new string[config.LeftSlots.Length];
        for (int i = 0; i < config.LeftSlots.Length; i++)
        {
            leftSlots[i] = config.LeftSlots[i].ToString();
        }

        string[] rightSlots = new string[config.RightSlots.Length];
        for (int i = 0; i < config.RightSlots.Length; i++)
        {
            rightSlots[i] = config.RightSlots[i].ToString();
        }

        return new TelemetryPacket
        {
            HasVehicle = s.HasVehicle,
            HasLiftoff = s.HasLiftoff,
            HasSurfaceContact = s.HasSurfaceContact,
            OnRails = s.OnRails,
            Signal = s.Signal.ToString(),
            IsFrozen = s.IsFrozen,
            CountingDown = s.CountingDown,
            MissionTime = FiniteOrZero(s.MissionElapsedSeconds),
            VehicleName = s.VehicleName,
            MissionName = missionName,
            SurfaceSpeed = FiniteOrZero(s.SurfaceSpeed),
            OrbitalSpeed = FiniteOrZero(s.OrbitalSpeed),
            VerticalSpeed = FiniteOrZero(s.VerticalSpeed),
            SpeedRef = config.SpeedReference.ToString(),
            Altitude = FiniteOrZero(s.Altitude),
            RadarAltitude = FiniteOrZero(s.RadarAltitude),
            TerrainRelative = config.TerrainRelativeAltitude,
            Apoapsis = FiniteOrZero(s.Apoapsis),
            Periapsis = FiniteOrZero(s.Periapsis),
            GForce = FiniteOrZero(s.GLoad),
            DynamicPressure = FiniteOrZero(s.DynamicPressure),
            MaxDynamicPressure = FiniteOrZero(s.MaxDynamicPressure),
            Downrange = FiniteOrZero(s.Downrange),
            Thrust = FiniteOrZero(s.Thrust),
            ThrustToWeight = FiniteOrZero(s.ThrustToWeight),
            PropellantFraction = FiniteOrZero(s.PropellantFraction),
            PropellantMass = FiniteOrZero(s.PropellantMass),
            PropellantCapacity = FiniteOrZero(s.PropellantCapacity),
            BurningEngines = s.BurningEngineCount,
            TotalEngines = s.TotalEngineCount,
            Engines = engines,
            Events = events,
            LeftSlots = leftSlots,
            RightSlots = rightSlots,
            ShowTelemetryBar = config.ShowTelemetryBar,
            ShowEngineDiagram = config.ShowEngineDiagram,
            ShowTimeline = config.ShowTimeline,
            ShowMissionClock = config.ShowMissionClock,
            ShowPropellants = config.ShowPropellants,
            Scale = config.Scale,
            Opacity = config.Opacity,
            SmoothingSeconds = config.SmoothingSeconds,
            TimelineWindowSeconds = config.TimelineWindowSeconds,
            SpeedArcFullScale = config.SpeedArcFullScale,
            AltitudeArcFullScaleKm = config.AltitudeArcFullScaleKm,
            GForceArcFullScale = config.GForceArcFullScale,
            EngineDiagramRotation = config.EngineDiagramRotation,
        };
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                Console.WriteLine(LogPrefix + $"accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            try
            {
                string? requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2)
                {
                    return;
                }

                string method = parts[0];
                string url = parts[1];

                string wsKey = string.Empty;
                bool isWsUpgrade = false;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(ct)))
                {
                    if (header.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase) &&
                        header.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isWsUpgrade = true;
                    }
                    else if (header.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        wsKey = header.Substring("Sec-WebSocket-Key:".Length).Trim();
                    }
                }

                if (isWsUpgrade && !string.IsNullOrEmpty(wsKey))
                {
                    await ProcessWebSocketUpgradeAsync(stream, wsKey, ct);
                }
                else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessHttpRequestAsync(stream, url, ct);
                }
            }
            catch { }
        }
    }

    private async Task ProcessWebSocketUpgradeAsync(NetworkStream stream, string wsKey, CancellationToken ct)
    {
        WebSocket? ws = null;
        try
        {
            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.UTF8.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

            byte[] headBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(headBytes, ct);
            await stream.FlushAsync(ct);

            ws = WebSocket.CreateFromStream(
                stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

            _sockets.TryAdd(ws, 0);
            _sendingFlags.TryAdd(ws, 0);
            Console.WriteLine(LogPrefix + $"client connected ({_sockets.Count} total)");

            if (_lastPacketBytes is not null)
            {
                _ = SendToSocketAsync(ws, _lastPacketBytes);
            }

            byte[] buffer = new byte[256];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                    }
                    catch { }
                    break;
                }
            }
        }
        catch { }
        finally
        {
            if (ws is not null)
            {
                RemoveSocket(ws);
                Console.WriteLine(LogPrefix + $"client disconnected ({_sockets.Count} total)");
            }
        }
    }

    private async Task ProcessHttpRequestAsync(NetworkStream stream, string url, CancellationToken ct)
    {
        try
        {
            string path = url.Split('?')[0];
            if (path == "/" || string.IsNullOrWhiteSpace(path))
            {
                path = "/index.html";
            }

            string filePath;
            if (path.StartsWith("/montserrat/", StringComparison.OrdinalIgnoreCase))
            {
                string fontName = Path.GetFileName(path);
                filePath = Path.Combine(_fontRoot, fontName);
            }
            else
            {
                string relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                filePath = Path.GetFullPath(Path.Combine(_webRoot, relative));

                // Path traversal check
                if (!filePath.StartsWith(_webRoot, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseAsync(stream, 403, "text/plain", "Forbidden"u8.ToArray(), ct);
                    return;
                }
            }

            if (!File.Exists(filePath))
            {
                Console.WriteLine(LogPrefix + $"404 Not Found: {url} (checked '{filePath}')");
                await WriteHttpResponseAsync(stream, 404, "text/plain", "File Not Found"u8.ToArray(), ct);
                return;
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string mime = ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css"  => "text/css; charset=utf-8",
                ".js"   => "application/javascript; charset=utf-8",
                ".ttf"  => "font/ttf",
                ".png"  => "image/png",
                ".svg"  => "image/svg+xml",
                ".json" => "application/json",
                _       => "application/octet-stream",
            };

            byte[] content = await File.ReadAllBytesAsync(filePath, ct);
            await WriteHttpResponseAsync(stream, 200, mime, content, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"HTTP serve error: {ex.Message}");
        }
    }

    private static async Task WriteHttpResponseAsync(
        NetworkStream stream, int statusCode, string mimeType, byte[] body, CancellationToken ct)
    {
        string statusText = statusCode switch
        {
            200 => "OK",
            403 => "Forbidden",
            404 => "Not Found",
            _   => "Error",
        };

        string headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         $"Content-Type: {mimeType}\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Connection: close\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                         "Pragma: no-cache\r\n" +
                         "Expires: 0\r\n\r\n";

        byte[] headBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headBytes, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string ResolveWebRoot(string? modDirectory)
    {
        if (!string.IsNullOrEmpty(modDirectory))
        {
            string path = Path.Combine(modDirectory, "web");
            if (Directory.Exists(path)) return Path.GetFullPath(path);
        }

        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "web");
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

        string current = Path.Combine(Environment.CurrentDirectory, "web");
        if (Directory.Exists(current)) return Path.GetFullPath(current);

        string? asm = Path.GetDirectoryName(typeof(TelemetryServer).Assembly.Location);
        if (!string.IsNullOrEmpty(asm))
        {
            string asmPath = Path.Combine(asm, "web");
            if (Directory.Exists(asmPath)) return Path.GetFullPath(asmPath);
        }

        return Path.GetFullPath(candidate);
    }

    private static string ResolveFontRoot(string? modDirectory)
    {
        if (!string.IsNullOrEmpty(modDirectory))
        {
            string path = Path.Combine(modDirectory, "montserrat");
            if (Directory.Exists(path)) return Path.GetFullPath(path);
        }

        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "montserrat");
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

        string current = Path.Combine(Environment.CurrentDirectory, "montserrat");
        if (Directory.Exists(current)) return Path.GetFullPath(current);

        string? asm = Path.GetDirectoryName(typeof(TelemetryServer).Assembly.Location);
        if (!string.IsNullOrEmpty(asm))
        {
            string asmPath = Path.Combine(asm, "montserrat");
            if (Directory.Exists(asmPath)) return Path.GetFullPath(asmPath);
        }

        return Path.GetFullPath(candidate);
    }

    public void Dispose()
    {
        Stop();
    }
}

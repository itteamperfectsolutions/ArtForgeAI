using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ArtForgeAI.Services;

/// <summary>
/// Calls the Python sidecar Flask server for PyTorch-based background removal.
/// Auto-starts the Python server if not running. Provides access to:
/// - BRIA RMBG 2.0 (best quality)
/// - BiRefNet (state-of-art edges)
/// - InSPyReNet (fast + accurate)
/// </summary>
public sealed class PythonBgSidecarService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PythonBgSidecarService> _logger;
    private readonly string _scriptDir;
    private readonly string _outputDir;
    private readonly int _port;
    private Process? _pythonProcess;
    private bool _serverReady;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public PythonBgSidecarService(
        HttpClient httpClient,
        IWebHostEnvironment env,
        ILogger<PythonBgSidecarService> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(5); // models can take time on first load
        _logger = logger;
        _port = 5100;
        _scriptDir = Path.Combine(env.ContentRootPath, "PythonBgService");
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>Check if the Python sidecar is reachable.</summary>
    public async Task<bool> IsRunningAsync()
    {
        try
        {
            var resp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/health");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ensure the Python server is running. Installs deps + starts if needed.</summary>
    public async Task EnsureRunningAsync()
    {
        if (_serverReady && await IsRunningAsync()) return;

        await _startLock.WaitAsync();
        try
        {
            if (_serverReady && await IsRunningAsync()) return;

            // Check if already running externally
            if (await IsRunningAsync())
            {
                _serverReady = true;
                _logger.LogInformation("Python BG sidecar already running on port {Port}", _port);
                return;
            }

            // Install dependencies if needed
            var reqFile = Path.Combine(_scriptDir, "requirements.txt");
            if (File.Exists(reqFile))
            {
                _logger.LogInformation("Installing Python dependencies...");
                var pipProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "pip",
                    Arguments = $"install -r \"{reqFile}\" --quiet",
                    WorkingDirectory = _scriptDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (pipProcess is not null)
                {
                    await pipProcess.WaitForExitAsync();
                    if (pipProcess.ExitCode != 0)
                    {
                        var err = await pipProcess.StandardError.ReadToEndAsync();
                        _logger.LogWarning("pip install warnings: {Err}", err);
                    }
                }
            }

            // Start the Python server
            var scriptPath = Path.Combine(_scriptDir, "bg_server.py");
            _logger.LogInformation("Starting Python BG sidecar: {Script} on port {Port}", scriptPath, _port);

            _pythonProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" --port {_port}",
                WorkingDirectory = _scriptDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (_pythonProcess is null || _pythonProcess.HasExited)
            {
                throw new InvalidOperationException("Failed to start Python sidecar process");
            }

            // Forward Python output to logger
            _pythonProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) _logger.LogInformation("[Python] {Line}", e.Data);
            };
            _pythonProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) _logger.LogInformation("[Python] {Line}", e.Data);
            };
            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();

            // Wait for server to become ready (up to 60s — model download can take time)
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                if (await IsRunningAsync())
                {
                    _serverReady = true;
                    _logger.LogInformation("Python BG sidecar ready on port {Port}", _port);
                    return;
                }
                if (_pythonProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Python sidecar exited with code {_pythonProcess.ExitCode}. Check Python dependencies.");
                }
            }

            throw new TimeoutException("Python BG sidecar did not start within 60 seconds");
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Remove background using a PyTorch model.
    /// Methods: "bria", "birefnet", "inspyrenet"
    /// </summary>
    public async Task<byte[]> RemoveBackgroundAsync(byte[] imageBytes, string method = "bria")
    {
        await EnsureRunningAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(imageBytes), "image", "image.png");
        content.Add(new StringContent(method), "method");

        var response = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/remove-bg", content);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Python sidecar error ({response.StatusCode}): {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Full pipeline: remove bg + save files.</summary>
    public async Task<BackgroundRemovalResult> RemoveAndSaveAsync(
        byte[] imageBytes, string method = "bria", string backgroundColor = "white")
    {
        var transparentBytes = await RemoveBackgroundAsync(imageBytes, method);

        // Ensure RGBA PNG
        using var img = Image.Load<Rgba32>(transparentBytes);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 });
        transparentBytes = ms.ToArray();

        // Save transparent
        var transFile = $"{Guid.NewGuid():N}_py_{method}_trans.png";
        var transPath = Path.Combine(_outputDir, transFile);
        await File.WriteAllBytesAsync(transPath, transparentBytes);

        // Create colored version
        using var fg = Image.Load<Rgba32>(transparentBytes);
        var bgColor = Rgba32.ParseHex(backgroundColor == "white" ? "#FFFFFF" : backgroundColor);
        using var canvas = new Image<Rgba32>(fg.Width, fg.Height, bgColor);
        canvas.ProcessPixelRows(fg, (bgAcc, fgAcc) =>
        {
            for (int y = 0; y < bgAcc.Height; y++)
            {
                var bgRow = bgAcc.GetRowSpan(y);
                var fgRow = fgAcc.GetRowSpan(y);
                for (int x = 0; x < bgAcc.Width; x++)
                {
                    var px = fgRow[x];
                    if (px.A == 0) continue;
                    float a = px.A / 255f, ia = 1f - a;
                    bgRow[x] = new Rgba32(
                        (byte)(px.R * a + bgRow[x].R * ia),
                        (byte)(px.G * a + bgRow[x].G * ia),
                        (byte)(px.B * a + bgRow[x].B * ia), 255);
                }
            }
        });

        var colorFile = $"{Guid.NewGuid():N}_py_{method}_col.png";
        var colorPath = Path.Combine(_outputDir, colorFile);
        canvas.SaveAsPng(colorPath, new PngEncoder { ColorType = PngColorType.Rgb });

        return new BackgroundRemovalResult($"generated/{colorFile}", $"generated/{transFile}", transparentBytes);
    }

    public void Dispose()
    {
        if (_pythonProcess is { HasExited: false })
        {
            try
            {
                _pythonProcess.Kill(entireProcessTree: true);
                _pythonProcess.Dispose();
                _logger.LogInformation("Python BG sidecar stopped");
            }
            catch { }
        }
        _startLock.Dispose();
    }
}

using System.IO.Pipes;
using ControlCenter.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightOrder.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" })) return await SelfTests.RunAsync();
        if (args.Length != 0 && !args.SequenceEqual(new[] { "--console" }))
        {
            Console.Error.WriteLine("Usage: LightOrder.Service.exe [--console | --self-test]");
            return 2;
        }
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("LightOrder requires Windows.");
            if (args.Length == 0 && !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
                throw new InvalidOperationException("Use --console for foreground mode, or start the installed Windows service.");
            var settings = ServiceSettings.Load();
            // Do not consume environment/CLI config providers as privileged service configuration.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
            if (args.Length == 0) builder.Services.AddWindowsService(options => options.ServiceName = "LightOrder.Service");
            builder.Services.Configure<HostOptions>(options =>
            {
                options.ShutdownTimeout = TimeSpan.FromMinutes(15);
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
            });
            builder.Services.AddSingleton(settings);
            builder.Services.AddHostedService<ServiceWorker>();
            await builder.Build().RunAsync();
            return Environment.ExitCode;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"LightOrder startup failed ({e.GetType().Name}): {e.Message}");
            return 1;
        }
    }
}

public sealed class ServiceWorker(ServiceSettings settings, ILogger<ServiceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var runtime = new ServiceRuntime(new ProfileStore(ServiceSettings.StateDirectory));
        // Keep the exclusive endpoint reserved until restoration and worker cleanup finish.
        NamedPipeServerStream? pipe = null;
        try
        {
            pipe = ServiceSecurity.CreatePipe(ServiceSecurity.ValidateOwnerSid(settings.OwnerSid));
            await runtime.StartAsync(settings, stoppingToken);
            runtime.ConfigureAutomation(new AutomationEngine(new AutomationFileStore(ServiceSettings.StateDirectory),
                runtime.ExecuteAutomatedAsync, runtime.ValidateCommand, new WindowsAutomationProcessSource(settings.OwnerSid)));
            runtime.StartControlLoop(stoppingToken);
            logger.LogInformation("LightOrder service ready on {Pipe}. Protocol major {Version}.", ServiceSecurity.PipeName, Wire.Version);
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                try { await ServeClientAsync(pipe, runtime, stoppingToken); }
                catch (Exception e) when (e is IOException or OperationCanceledException or System.Text.DecoderFallbackException or InvalidDataException)
                {
                    // Peer disconnect and malformed framing are connection-local failures.
                    runtime.AddDiagnostic("Client connection closed: " + e.GetType().Name + ".");
                }
                finally { if (pipe.IsConnected) pipe.Disconnect(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            Environment.ExitCode = 1;
            logger.LogError("Service stopped after {Category}; check protected deployment configuration.", e.GetType().Name);
            throw;
        }
        finally
        {
            try { await runtime.DisposeAsync(); }
            finally { pipe?.Dispose(); }
            foreach (var diagnostic in runtime.Diagnostics) logger.LogInformation("{Diagnostic}", diagnostic);
            logger.LogInformation("Plugin release and process cleanup completed. Hard crashes cannot guarantee device restoration.");
        }
    }

    private static async Task ServeClientAsync(NamedPipeServerStream pipe, ServiceRuntime runtime, CancellationToken stoppingToken)
    {
        var framing = new RpcFraming(pipe);
        // Prevent idle clients from monopolizing the single serial command endpoint indefinitely.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        session.CancelAfter(TimeSpan.FromMinutes(2));
        for (var requestCount = 0; requestCount < 128; requestCount++)
        {
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var line = await framing.ReadAsync(readDeadline.Token);
            if (line is null) return;
            RpcResponse response;
            try
            {
                var request = ServiceProtocol.Parse(line);
                var result = await runtime.HandleAsync(request.Method, request.Params, request.Id, stoppingToken);
                response = new RpcResponse("2.0", request.Id, result, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception e) { response = ServiceProtocol.Error(ServiceProtocol.RecoverId(line), e); }
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            await framing.WriteAsync(response, writeDeadline.Token);
        }
    }
}

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
        var runtime = new ServiceRuntime(new ProfileStore(ServiceSettings.StateDirectory));
        Exception? failure = null;
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
            await RunConnectionsAsync(pipe, client => ServeClientAsync(client, runtime, stoppingToken),
                runtime.AddDiagnostic, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            failure = e;
            Environment.ExitCode = 1;
            logger.LogError("Service stopped; check protected deployment configuration. {Details}", SafeExceptionDetails(e));
            throw;
        }
        finally
        {
            try { await runtime.DisposeAsync(); }
            catch (Exception cleanupError)
            {
                logger.LogError("Service cleanup failed. {Details}", SafeExceptionDetails(cleanupError));
                // Preserve the original host failure if cleanup also fails.
                if (failure is null) throw;
            }
            finally { pipe?.Dispose(); }
            foreach (var diagnostic in runtime.Diagnostics) logger.LogInformation("{Diagnostic}", diagnostic);
            logger.LogInformation("Plugin release and process cleanup completed. Hard crashes cannot guarantee device restoration.");
        }
    }

    // Reuse the same reserved server instance; creation/ACL and non-peer listener errors remain fatal.
    internal static async Task RunConnectionsAsync(NamedPipeServerStream pipe,
        Func<NamedPipeServerStream, Task> serveClient, Action<string> diagnostic, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var resetConnection = false;
            try
            {
                try { await pipe.WaitForConnectionAsync(token); }
                catch (IOException error) when (IsPeerDisconnect(error))
                {
                    resetConnection = true;
                    diagnostic("Client disconnected while accepting: " + SafeExceptionDetails(error));
                    continue;
                }
                resetConnection = true;
                try { await serveClient(pipe); }
                catch (Exception error) when (error is IOException or OperationCanceledException or
                    System.Text.DecoderFallbackException or InvalidDataException)
                {
                    diagnostic("Client connection closed: " + SafeExceptionDetails(error));
                }
            }
            finally
            {
                // IsConnected is cached state and may already be false after a failed read/write.
                // Disconnect must still reset that instance before the next accept.
                if (resetConnection) DisconnectClient(pipe, diagnostic);
            }
        }
    }

    internal static void DisconnectClient(NamedPipeServerStream pipe, Action<string> diagnostic)
    {
        try { pipe.Disconnect(); }
        catch (IOException error) when (IsPeerDisconnect(error))
        {
            diagnostic("Client disconnected during reset: " + SafeExceptionDetails(error));
        }
    }

    private static bool IsPeerDisconnect(IOException error) => (error.HResult & 0xffff) is 109 or 232 or 233;

    // Exception messages/Data can contain arbitrary request or plugin text. Retain safe debugging
    // details, including the inner cause and call sites, without publishing those values.
    internal static string SafeExceptionDetails(Exception error)
    {
        var details = new System.Text.StringBuilder();
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (details.Length > 0) details.Append(" -> ");
            details.Append(cause.GetType().FullName).Append(" HResult=0x").Append(cause.HResult.ToString("X8"));
            if (cause is System.ComponentModel.Win32Exception native) details.Append(" NativeError=").Append(native.NativeErrorCode);
            details.AppendLine().Append(cause.StackTrace);
        }
        return details.ToString();
    }

    internal static async Task ServeClientAsync(NamedPipeServerStream pipe, ServiceRuntime runtime, CancellationToken stoppingToken)
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

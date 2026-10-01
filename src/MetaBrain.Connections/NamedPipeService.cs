using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MetaBrain.Application;
using MetaBrain.Core.Security;

namespace MetaBrain.Connections;

internal sealed class NamedPipeService
{
    private static readonly JsonSerializerOptions InputOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions OutputOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ServiceSettings _settings;
    private readonly ServiceRequestHandler _handler;

    public NamedPipeService(ServiceSettings settings)
    {
        _settings = settings;
        using var identity = WindowsIdentity.GetCurrent();
        var serviceSid = identity.User?.Value ?? throw new InvalidDataException("The service process has no user SID.");
        var authority = new GrantAuthority(new FileGrantStore(settings.PolicyStorePath, serviceSid));
        var bindings = settings.ManagedResources ?? Array.Empty<ManagedResourceBinding>();
        IManagedResourceReader? reader = null;
        if (bindings.Length > 0)
        {
            Directory.CreateDirectory(settings.ResourceRootPath);
            var fileReader = new ManagedResourceFileReader(settings.ResourceRootPath, serviceSid);
            foreach (var binding in bindings)
            {
                fileReader.Register(binding.ResourceId, binding.ZoneId, binding.FileName);
            }

            reader = fileReader;
        }

        var catalog = new ManagedResourceCatalog(bindings.Select(binding => (binding.ResourceId, binding.ZoneId)));
        _handler = new ServiceRequestHandler(authority, catalog, reader);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var listener = WindowsPipeServer.Create(_settings, firstInstance: true);
        Console.WriteLine("READY protocol=1 channel=owner-only");
        await ListenAsync(listener, cancellationToken).ConfigureAwait(false);
    }

    private async Task ListenAsync(NamedPipeServerStream initialListener, CancellationToken cancellationToken)
    {
        var listener = initialListener;
        var firstInstance = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var connected = listener;
                listener = WindowsPipeServer.Create(_settings, firstInstance);
                await HandleConnectionAsync(connected, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Dispose();
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            timeout.CancelAfter(RequestTimeout);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true) { AutoFlush = true };

            try
            {
                var identity = WindowsClientIdentityReader.Read(pipe);
                if (!SidEquals(identity.UserSid, _settings.OwnerSid))
                {
                    await WriteErrorAsync(writer, "unauthorized", timeout.Token).ConfigureAwait(false);
                    return;
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new PipeAuthenticationReply(true, null), OutputOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
                var requestLine = await ReadBoundedLineAsync(reader, 16 * 1024, timeout.Token).ConfigureAwait(false);
                if (requestLine is null)
                {
                    await WriteErrorAsync(writer, "invalid_request", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var request = JsonSerializer.Deserialize<ServiceRequest>(requestLine, InputOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.Operation))
                {
                    await WriteErrorAsync(writer, "invalid_request", timeout.Token).ConfigureAwait(false);
                    return;
                }

                var reply = _handler.Handle(AuthenticatedContext.ForOwner(), request);
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, OutputOptions).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    await WriteErrorAsync(writer, "request_timeout", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await WriteErrorAsync(writer, "unauthorized", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static bool SidEquals(string left, string right)
    {
        try
        {
            return string.Equals(new SecurityIdentifier(left).Value,
                new SecurityIdentifier(right).Value,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximumLength, CancellationToken cancellationToken)
    {
        var character = new char[1];
        var line = new StringBuilder(Math.Min(maximumLength, 1024));
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            if (character[0] == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }

            if (line.Length == maximumLength)
            {
                throw new InvalidDataException("The client message is too large.");
            }

            line.Append(character[0]);
        }
    }

    private static async Task WriteErrorAsync(StreamWriter writer, string error, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { error }, OutputOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}

internal sealed record PipeAuthenticationReply(bool Authenticated, string? Error);

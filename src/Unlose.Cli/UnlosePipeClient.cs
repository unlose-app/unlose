using Unlose.Core.Interfaces;
using Unlose.Core.Ipc;
using System.IO.Pipes;
using System.Text.Json;

namespace Unlose.Cli;

public class UnlosePipeClient : IPipeClient
{
    private const string PipeName = "unlosePipe";

    public async Task<string> SendCommandAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var msg = new PipeMessage { Command = command };

        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                msg.Parameters[parameter.Key] = parameter.Value;
            }
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(msg));
        var response = await reader.ReadLineAsync(ct);
        return response ?? string.Empty;
    }
}

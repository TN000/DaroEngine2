// ============================================================================
// Mosart Test Client - Standalone diagnostic tool
// Run with: dotnet run -- test-client
// ============================================================================
// This is a separate tool for testing, not part of the main application.
// To use: Create a separate console project or include as conditional compilation.

#if MOSART_TEST_CLIENT

using System.Net.Sockets;
using System.Text;

Console.WriteLine("=== Mosart Test Client ===");
Console.WriteLine("Commands: CUE=0, PLAY=1, STOP=2");
Console.WriteLine("Format: GUID|COMMAND");
Console.WriteLine("Type 'quit' to exit\n");

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5555;

try
{
    using var client = new TcpClient();
    await client.ConnectAsync(host, port);
    Console.WriteLine($"Connected to {host}:{port}\n");

    await using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.UTF8);
    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

    // Start background reader
    var readerTask = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var response = await reader.ReadLineAsync();
                if (response == null) break;
                Console.WriteLine($"<< {response}");
            }
        }
        catch (IOException)
        {
            Console.WriteLine("Connection closed by server");
        }
    });

    // Command loop
    while (true)
    {
        Console.Write(">> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input)) continue;
        if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

        // Helper commands
        if (input.StartsWith("cue ", StringComparison.OrdinalIgnoreCase))
        {
            var guid = input[4..].Trim();
            input = $"{guid}|0";
        }
        else if (input.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            // Use last GUID or placeholder
            input = "00000000-0000-0000-0000-000000000000|1";
        }
        else if (input.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            input = "00000000-0000-0000-0000-000000000000|2";
        }

        await writer.WriteLineAsync(input);
    }

    client.Close();
}
catch (SocketException ex)
{
    Console.WriteLine($"Connection error: {ex.Message}");
}

#endif

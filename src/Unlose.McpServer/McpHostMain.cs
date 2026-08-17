using Unlose.McpServer;

var server = new PipeBackedMcpBridge();
await server.RunAsync();
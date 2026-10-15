using StWorkbench.Mcp;

// This test-only executable also acts as an unresponsive SSH fixture. It never contacts a host.
if (args.Length > 0 && args[0] == "-o")
{
    if (!args.Contains("StrictHostKeyChecking=yes") || !args.Contains("BatchMode=yes")) return;
    _ = await Console.In.ReadLineAsync();
    await Task.Delay(Timeout.Infinite);
    return;
}
await WorkspaceTests.RunAsync();
await ControlTests.RunAsync();
await ShutdownTests.RunAsync();
await ProtocolTests.RunAsync();

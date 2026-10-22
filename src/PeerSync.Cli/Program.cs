using PeerSync.Cli.Diagnostics;
using PeerSync.Cli;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("PeerSync CLI");
    Console.WriteLine("  psp status [--json]");
    Console.WriteLine("  psp add <path> [--json]");
    Console.WriteLine("  psp hosts <folder> <host:port ...> [--json]");
    Console.WriteLine("  psp hosts <folder> --clear [--json]");
    Console.WriteLine("  psp join <key> <path> [--json]");
    Console.WriteLine("  psp share <folder> [--json]");
    Console.WriteLine("  psp peers <folder> [--json]");
    Console.WriteLine("  psp dump-index <folder> [--json]");
    Console.WriteLine("  psp diagnostics [--logs <directory>] [--output <new-zip>] [--include-network-addresses]");
    Console.WriteLine("  psp probe <host:port>");
    return args.Length == 0 ? 2 : 0;
}

if (args[0] == "diagnostics") return DiagnosticsCommand.Run(args[1..]);
if (args[0] == "probe") return await ProbeCommand.RunAsync(args[1..]);
if (args[0] == "status") return await StatusCommand.RunAsync(args[1..]);
if (args[0] is "add" or "join" or "hosts" or "share" or "peers" or "dump-index")
    return await FolderCommand.RunAsync(args[0], args[1..]);
Console.Error.WriteLine($"Unknown command '{args[0]}'. Run 'psp --help' for usage.");
return 2;

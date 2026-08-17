using Unlose.Core.Config;
using Unlose.Core.Data;
using Unlose.Core.Interfaces;
using Unlose.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "unlose");
Directory.CreateDirectory(dataDir);

var logPath = Path.Combine(dataDir, "logs", "service-.log");
Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var configPath = Path.Combine(dataDir, "config.json");
    // First run: no config file yet -> detect every eligible local volume (fixed NTFS drives,
    // which is what VSS can shadow-copy) and default-protect all of them, writing config.json so
    // the choice is visible and later user edits build on it. On subsequent runs the user's config
    // always wins and the detector is never consulted again.
    if (!File.Exists(configPath))
    {
        var detected = DefaultVolumeDetector.Detect();
        var firstRun = new UnloseConfig { Snapshot = new SnapshotConfig { Volumes = detected } };
        await ConfigLoader.SaveAsync(firstRun, configPath);
        Log.Information("First run: created {Path} with {N} protected volume(s): {Volumes}",
            configPath, detected.Length, string.Join(", ", detected));
    }
    var config = await ConfigLoader.LoadAsync(configPath);
    var dbPath = Path.Combine(dataDir, "snapshots.db");
    await DatabaseInitializer.EnsureCreatedAsync(dbPath);

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => { options.ServiceName = "unloseService"; });
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(new SqliteRepository(dbPath));
    builder.Services.AddSingleton<EventBus>();
    builder.Services.AddSingleton<VssAdapter>();
    builder.Services.AddSingleton<IVssGateway>(sp => sp.GetRequiredService<VssAdapter>());
    builder.Services.AddSingleton<SnapshotManager>();
    builder.Services.AddSingleton<StorageGuard>();
    builder.Services.AddSingleton<IStorageInfo>(sp => sp.GetRequiredService<StorageGuard>());
    builder.Services.AddSingleton<RetentionPolicyEngine>();
    builder.Services.AddSingleton<SnapshotScheduler>();
    builder.Services.AddSingleton<AgentSessionManager>();
    builder.Services.AddSingleton<ProtectionPauseManager>();
    builder.Services.AddSingleton<ISnapshotService>(sp => sp.GetRequiredService<SnapshotManager>());
    builder.Services.AddSingleton<DiffEngine>();
    builder.Services.AddSingleton<IPipeSecurityHelper, PipeSecurityHelper>();
    builder.Services.AddSingleton<UnlosePipeServer>();
    builder.Services.AddSingleton<IPipeServer>(sp => sp.GetRequiredService<UnlosePipeServer>());
    builder.Services.AddSingleton<HeartbeatService>();
    builder.Services.AddSingleton<AuditService>(sp =>
        new AuditService(
            sp.GetRequiredService<ILogger<AuditService>>(),
            sp.GetRequiredService<SqliteRepository>()));
    builder.Services.AddSingleton<IAuditService>(sp => sp.GetRequiredService<AuditService>());
    builder.Services.AddSingleton<SystemRestoreService>();
    builder.Services.AddSingleton<ISystemRestoreGateway>(sp => sp.GetRequiredService<SystemRestoreService>());
    builder.Services.AddSingleton(sp =>
        new GlobalMemoryInjector(sp.GetRequiredService<ILogger<GlobalMemoryInjector>>()));
    builder.Services.AddSingleton(sp =>
        new SkillDeployer(sp.GetRequiredService<ILogger<SkillDeployer>>()));
    builder.Services.AddSingleton(sp =>
        new McpConfigInjector(sp.GetRequiredService<ILogger<McpConfigInjector>>()));
    builder.Services.AddSingleton<CommandDispatcher>(sp =>
        new CommandDispatcher(
            sp.GetRequiredService<ILogger<CommandDispatcher>>(),
            sp.GetRequiredService<ISnapshotService>(),
            sp.GetRequiredService<ProtectionPauseManager>(),
            sp.GetRequiredService<IAuditService>(),
            sp.GetRequiredService<SqliteRepository>(),
            sp.GetRequiredService<ISystemRestoreGateway>(),
            sp.GetRequiredService<UnloseConfig>(),
            configPath,
            sp.GetRequiredService<GlobalMemoryInjector>(),
            sp.GetRequiredService<SkillDeployer>(),
            sp.GetRequiredService<RetentionPolicyEngine>(),
            sp.GetRequiredService<McpConfigInjector>()));
    builder.Services.AddSingleton<IProtectionStateManager>(sp => sp.GetRequiredService<ProtectionPauseManager>());
    builder.Services.AddHostedService<StorageGuard>(sp => sp.GetRequiredService<StorageGuard>());
    builder.Services.AddHostedService<SnapshotScheduler>(sp => sp.GetRequiredService<SnapshotScheduler>());
    builder.Services.AddHostedService<WalCheckpointService>();
    builder.Services.AddHostedService<AgentSessionManager>(sp => sp.GetRequiredService<AgentSessionManager>());
    builder.Services.AddHostedService<ProtectionPauseManager>(sp => sp.GetRequiredService<ProtectionPauseManager>());
    builder.Services.AddHostedService<UnloseWorker>();
    builder.Services.AddSerilog();

    var host = builder.Build();

    var pauseManager = host.Services.GetRequiredService<ProtectionPauseManager>();
    pauseManager.RegisterSuspendable(host.Services.GetRequiredService<SnapshotScheduler>());

    // Global memory injection: idempotently append the unlose snapshot guard instructions
    // (auto snapshot at session start + snapshot before dangerous operations, with
    // v1→v2 auto-migration) to real users' ~/AGENTS.md and the global memory files
    // of installed agents.
    // Skill package deployment: write unlose-snapshot/SKILL.md into installed agents' skill directories.
    // Failures of either track-B delivery channel do not block service startup.
    try
    {
        await host.Services.GetRequiredService<GlobalMemoryInjector>().InjectForAllUsersAsync();
    }
    catch (Exception injectEx)
    {
        Log.Warning(injectEx, "GlobalMemoryInject failed (non-fatal)");
    }

    try
    {
        await host.Services.GetRequiredService<SkillDeployer>().DeployForAllUsersAsync();
    }
    catch (Exception deployEx)
    {
        Log.Warning(deployEx, "SkillDeploy failed (non-fatal)");
    }

    // MCP config injection: idempotently add the unlose MCP server to installed agents'
    // JSON MCP configs, making the agent's MCP channel available automatically
    // (initialize triggers an automatic "session start" snapshot + the create_snapshot tool).
    // Failures do not block service startup.
    try
    {
        await host.Services.GetRequiredService<McpConfigInjector>().InjectForAllUsersAsync();
    }
    catch (Exception mcpEx)
    {
        Log.Warning(mcpEx, "McpConfigInject failed (non-fatal)");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

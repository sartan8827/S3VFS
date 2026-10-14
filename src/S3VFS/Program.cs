using Microsoft.Extensions.Logging;
using System.CommandLine;
using S3VFS;
using S3VFS.Configuration;
using S3VFS.Credentials;
using S3VFS.Diagnostics;
using S3VFS.Logging;
using S3VFS.LostAndFound;
using S3VFS.Notifications;
using S3VFS.ProjFs;

const int ExitGeneralException = 2;

var cliOptions = new MountCliOptions();
var credentialStore = new WindowsCredentialStore();

var rootCommand = new RootCommand(
    "S3VFS — S3 Virtual File System for Windows: mount a cloud object-store " +
    "bucket/container as a local folder via ProjFS. Mounts are configured exclusively " +
    "through s3vfs.toml / %APPDATA%\\S3VFS\\config.toml; only --verbose / --log-format are " +
    "accepted on the command line as one-off process-level overrides.");
cliOptions.AddTo(rootCommand);

rootCommand.Subcommands.Add(CredentialsCommandFactory.Build(credentialStore));
rootCommand.Subcommands.Add(MountCommandFactory.BuildMountCommand(credentialStore, cliOptions));
rootCommand.Subcommands.Add(MountCommandFactory.BuildMountAllCommand(credentialStore, cliOptions));
rootCommand.Subcommands.Add(DoctorCommandFactory.Build(credentialStore, cliOptions));
rootCommand.Subcommands.Add(LostAndFoundCommandFactory.Build(credentialStore, cliOptions));

rootCommand.SetAction(parseResult =>
{
    var cliConfigPath = parseResult.GetValue(cliOptions.ConfigPath);
    S3vfsConfigFile? fileConfig;
    try
    {
        fileConfig = S3vfsConfigFileLoader.LoadFromDefaultLocations(cliConfigPath);
    }
    catch (S3vfsConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return ExitGeneralException;
    }

    var verbose = cliOptions.GetVerbose(parseResult) ?? fileConfig?.Verbose ?? false;
    var logFormat = parseResult.GetValue(cliOptions.LogFormat) ?? fileConfig?.LogFormat ?? LogFormat.Text;

    using var loggerFactory = LogConsoleFactory.Create(verbose, logFormat);
    var logger = loggerFactory.CreateLogger("S3VFS");
    using var refreshNotifier = new WindowsBalloonTipNotifier(
        loggerFactory.CreateLogger<WindowsBalloonTipNotifier>());

    if (fileConfig is null || fileConfig.Mounts.Count == 0)
    {
        logger.LogError(
            "No mount is configured. Create s3vfs.toml next to s3vfs.exe or " +
            "%APPDATA%\\S3VFS\\config.toml (or pass --config <path>) with at least " +
            "'bucket' and 'root-folder' (legacy single-mount form) or one or more " +
            "[[mount]] entries.");
        return ExitGeneralException;
    }

    // Multi-mount configs cannot be resolved by the bare root command because the
    // CLI has no per-mount overrides any more. Push the operator toward the named
    // subcommands so the intent is unambiguous.
    if (fileConfig.Mounts.Count > 1)
    {
        logger.LogError(
            "Config defines {Count} mounts; the bare root command runs only when a single " +
            "mount is configured. Use 's3vfs mount-all' to start every mount in this process, " +
            "or 's3vfs mount --name <one-of: {Names}>' to start one.",
            fileConfig.Mounts.Count,
            string.Join(", ", fileConfig.Mounts.Select(m => m.Name)));
        return ExitGeneralException;
    }

    var mountConfig = fileConfig.Mounts[0];

    ProjFsProviderOptions options;
    try
    {
        options = MountOptionsBuilder.Build(mountConfig, credentialStore, logger, refreshNotifier);
    }
    catch (S3vfsConfigException ex)
    {
        logger.LogError("{Message}", ex.Message);
        return ExitGeneralException;
    }

    options = options with { Verbose = verbose };

    try
    {
        return MountHost.Run(
            [new MountHost.MountInvocation(mountConfig.Name, options)],
            loggerFactory,
            logger);
    }
    catch (BucketVersioningNotEnabledException ex)
    {
        // The exception's message is the user-facing remediation banner — log it as a
        // single error so the copy-paste fix command lands intact in the terminal,
        // without the stack trace chrome a generic critical log would add.
        logger.LogError("{Message}", ex.Message);
        return ExitGeneralException;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "fatal");
        return ExitGeneralException;
    }
});

return rootCommand.Parse(args).Invoke();

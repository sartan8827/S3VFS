using Microsoft.Extensions.Logging;
using S3VFS.Configuration;
using S3VFS.Credentials;
using S3VFS.Logging;
using S3VFS.Notifications;
using S3VFS.ProjFs;
using System.CommandLine;

namespace S3VFS;

/// <summary>
/// Builds the <c>mount</c> and <c>mount-all</c> sub-commands. Both consume
/// the entire mount configuration from <c>s3vfs.toml</c> /
/// <c>%APPDATA%\S3VFS\config.toml</c>; the only CLI surface the subcommands
/// accept is <c>--name</c> (for selecting a mount) and the process-level
/// flags shared with the root command.
/// </summary>
internal static class MountCommandFactory
{
    /// <summary>
    /// Builds <c>s3vfs mount --name &lt;name&gt;</c>. Per-mount settings are
    /// taken exclusively from the config file; <c>--name</c> is the only
    /// runtime flag because it picks <em>which</em> mount to start when more
    /// than one is configured.
    /// </summary>
    public static Command BuildMountCommand(
        IAwsCredentialStore credentialStore, MountCliOptions cliOptions)
    {
        var nameOption = new Option<string?>("--name")
        {
            Description =
                "Selects a mount by 'name' from s3vfs.toml. Required when the config defines " +
                "more than one [[mount]]; optional when there is exactly one (in which case " +
                "the single entry is used).",
        };

        var command = new Command(
            "mount",
            "Start a single mount selected by --name from the s3vfs.toml [[mount]] array.")
        {
            nameOption,
        };
        cliOptions.AddTo(command);

        command.SetAction(parseResult => RunSingleMount(
            parseResult, cliOptions, nameOption, credentialStore));

        return command;
    }

    /// <summary>
    /// Builds <c>s3vfs mount-all</c>. The command takes no per-mount surface;
    /// it consumes the configured <c>[[mount]]</c> entries verbatim and starts
    /// one provider per entry inside the same process.
    /// </summary>
    public static Command BuildMountAllCommand(
        IAwsCredentialStore credentialStore, MountCliOptions cliOptions)
    {
        var command = new Command(
            "mount-all",
            "Start every mount declared in the s3vfs.toml [[mount]] array, in declaration " +
            "order, inside a single process. Each mount runs its own ProjFsProvider; the " +
            "process stays alive until Enter is pressed and then disposes every mount.");
        cliOptions.AddTo(command);

        command.SetAction(parseResult => RunAllMounts(parseResult, cliOptions, credentialStore));

        return command;
    }

    /// <summary>
    /// Action body for <c>s3vfs mount --name &lt;name&gt;</c>. Returns the
    /// process exit code so System.CommandLine can propagate it.
    /// </summary>
    private static int RunSingleMount(
        ParseResult parseResult,
        MountCliOptions cliOptions,
        Option<string?> nameOption,
        IAwsCredentialStore credentialStore)
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
            return MountHost.ExitGeneralException;
        }

        var verbose = cliOptions.GetVerbose(parseResult) ?? fileConfig?.Verbose ?? false;
        var logFormat = parseResult.GetValue(cliOptions.LogFormat) ?? fileConfig?.LogFormat ?? LogFormat.Text;

        using var loggerFactory = LogConsoleFactory.Create(verbose, logFormat);
        var logger = loggerFactory.CreateLogger("S3VFS");
        using var refreshNotifier = new WindowsBalloonTipNotifier(
            loggerFactory.CreateLogger<WindowsBalloonTipNotifier>());

        var requestedName = parseResult.GetValue(nameOption);
        var mountConfig = SelectMount(fileConfig, requestedName, logger);
        if (mountConfig is null) return MountHost.ExitGeneralException;

        ProjFsProviderOptions options;
        try
        {
            options = MountOptionsBuilder.Build(mountConfig, credentialStore, logger, refreshNotifier);
        }
        catch (S3vfsConfigException ex)
        {
            logger.LogError("{Message}", ex.Message);
            return MountHost.ExitGeneralException;
        }

        options = options with { Verbose = verbose };

        try
        {
            return MountHost.Run([new(mountConfig.Name, options)], loggerFactory, logger);
        }
        catch (BucketVersioningNotEnabledException ex)
        {
            logger.LogError("{Message}", ex.Message);
            return MountHost.ExitGeneralException;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "fatal");
            return MountHost.ExitGeneralException;
        }
    }

    /// <summary>
    /// Action body for <c>s3vfs mount-all</c>. Builds runtime options for
    /// every mount in the config and hands the list to <see cref="MountHost.Run"/>.
    /// </summary>
    private static int RunAllMounts(
        ParseResult parseResult,
        MountCliOptions cliOptions,
        IAwsCredentialStore credentialStore)
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
            return MountHost.ExitGeneralException;
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
                "'mount-all' requires at least one [[mount]] entry in s3vfs.toml or " +
                "%APPDATA%/S3VFS/config.toml.");
            return MountHost.ExitGeneralException;
        }

        var invocations = new List<MountHost.MountInvocation>(fileConfig.Mounts.Count);
        foreach (var mount in fileConfig.Mounts)
        {
            ProjFsProviderOptions options;
            try
            {
                options = MountOptionsBuilder.Build(mount, credentialStore, logger, refreshNotifier);
            }
            catch (S3vfsConfigException ex)
            {
                logger.LogError("{Message}", ex.Message);
                return MountHost.ExitGeneralException;
            }
            options = options with { Verbose = verbose };
            invocations.Add(new MountHost.MountInvocation(mount.Name, options));
        }

        try
        {
            return MountHost.Run(invocations, loggerFactory, logger);
        }
        catch (BucketVersioningNotEnabledException ex)
        {
            logger.LogError("{Message}", ex.Message);
            return MountHost.ExitGeneralException;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "fatal");
            return MountHost.ExitGeneralException;
        }
    }

    /// <summary>
    /// Resolves a mount config from <paramref name="requestedName"/>. When
    /// <paramref name="requestedName"/> is null and the config has exactly one
    /// mount, that single entry is selected silently; otherwise the operator
    /// is required to disambiguate. Errors are logged and a null return
    /// signals the caller to exit non-zero.
    /// </summary>
    private static S3vfsMountConfig? SelectMount(
        S3vfsConfigFile? fileConfig, string? requestedName, ILogger logger)
    {
        if (fileConfig is null || fileConfig.Mounts.Count == 0)
        {
            logger.LogError(
                "'mount' requires at least one [[mount]] entry in s3vfs.toml or " +
                "%APPDATA%/S3VFS/config.toml.");
            return null;
        }

        if (string.IsNullOrEmpty(requestedName))
        {
            if (fileConfig.Mounts.Count == 1) return fileConfig.Mounts[0];
            logger.LogError(
                "Config defines {Count} mounts; pass --name <one-of: {Names}>.",
                fileConfig.Mounts.Count,
                string.Join(", ", fileConfig.Mounts.Select(m => m.Name)));
            return null;
        }

        foreach (var mount in fileConfig.Mounts)
        {
            if (string.Equals(mount.Name, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                return mount;
            }
        }
        logger.LogError(
            "No mount named '{Name}' in the config. Available: {Names}.",
            requestedName,
            string.Join(", ", fileConfig.Mounts.Select(m => m.Name)));
        return null;
    }
}

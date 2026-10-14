using Microsoft.Extensions.Logging;
using S3VFS.Configuration;
using S3VFS.Logging;
using S3VFS.Notifications;
using S3VFS.ProjFs;
using System.CommandLine;

namespace S3VFS;

/// <summary>
/// Builds the <c>mount</c> sub-command. It consumes the entire mount
/// configuration from <c>s3vfs.toml</c> / <c>%APPDATA%\S3VFS\config.toml</c>;
/// the only CLI surface the subcommand accepts is <c>--name</c> (for selecting
/// a single mount) and the process-level flags shared with the root command.
/// </summary>
internal static class MountCommandFactory
{
    /// <summary>
    /// Builds <c>s3vfs mount [--name &lt;name&gt;]</c>. Per-mount settings are
    /// taken exclusively from the config file. With <c>--name</c> only the
    /// matching mount is started; without it every configured <c>[[mount]]</c>
    /// is started in declaration order inside the same process.
    /// </summary>
    public static Command BuildMountCommand(MountCliOptions cliOptions)
    {
        var nameOption = new Option<string?>("--name")
        {
            Description =
                "Selects a single mount by 'name' from s3vfs.toml. When omitted, every " +
                "[[mount]] in the config is started in declaration order.",
        };

        var command = new Command(
            "mount",
            "Start the mount selected by --name, or every mount declared in the s3vfs.toml " +
            "[[mount]] array (in declaration order) when --name is omitted. Each mount runs " +
            "its own ProjFsProvider inside a single process; the process stays alive until " +
            "Enter is pressed and then disposes every mount.")
        {
            nameOption,
        };
        cliOptions.AddTo(command);

        command.SetAction(parseResult => RunMount(parseResult, cliOptions, nameOption));

        return command;
    }

    /// <summary>
    /// Action body for <c>s3vfs mount</c>. Builds runtime options for every
    /// selected mount and hands the list to <see cref="MountHost.Run"/>.
    /// Returns the process exit code so System.CommandLine can propagate it.
    /// </summary>
    private static int RunMount(
        ParseResult parseResult,
        MountCliOptions cliOptions,
        Option<string?> nameOption)
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
        var mounts = SelectMounts(fileConfig, requestedName, logger);
        if (mounts is null) return MountHost.ExitGeneralException;

        var invocations = new List<MountHost.MountInvocation>(mounts.Count);
        foreach (var mount in mounts)
        {
            ProjFsProviderOptions options;
            try
            {
                options = MountOptionsBuilder.Build(mount, logger, refreshNotifier);
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
    /// Resolves the mounts to start. When <paramref name="requestedName"/> is
    /// null or empty every configured mount is returned in declaration order;
    /// otherwise only the mount whose name matches (case-insensitively) is
    /// returned. Errors are logged and a null return signals the caller to
    /// exit non-zero.
    /// </summary>
    private static IReadOnlyList<S3vfsMountConfig>? SelectMounts(
        S3vfsConfigFile? fileConfig, string? requestedName, ILogger logger)
    {
        if (fileConfig is null || fileConfig.Mounts.Count == 0)
        {
            logger.LogError(
                "'mount' requires at least one [[mount]] entry in s3vfs.toml or " +
                "%APPDATA%/S3VFS/config.toml.");
            return null;
        }

        if (string.IsNullOrEmpty(requestedName)) return fileConfig.Mounts;

        foreach (var mount in fileConfig.Mounts)
        {
            if (string.Equals(mount.Name, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                return [mount];
            }
        }
        logger.LogError(
            "No mount named '{Name}' in the config. Available: {Names}.",
            requestedName,
            string.Join(", ", fileConfig.Mounts.Select(m => m.Name)));
        return null;
    }
}

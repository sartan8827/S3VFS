using System.CommandLine;
using S3VFS;
using S3VFS.LostAndFound;

var cliOptions = new MountCliOptions();

var rootCommand = new RootCommand(
    "S3VFS — S3 Virtual File System for Windows: mount a cloud object-store " +
    "bucket/container as a local folder via ProjFS. Mounts are configured exclusively " +
    "through s3vfs.toml / %APPDATA%\\S3VFS\\config.toml; run 's3vfs mount' to start them.");

rootCommand.Subcommands.Add(HelpCommandFactory.Build(rootCommand));
rootCommand.Subcommands.Add(MountCommandFactory.BuildMountCommand(cliOptions));
rootCommand.Subcommands.Add(LostAndFoundCommandFactory.Build(cliOptions));

// A bare 's3vfs' behaves exactly like 's3vfs help'.
rootCommand.SetAction(_ => HelpCommandFactory.ShowHelp(rootCommand, []));

return rootCommand.Parse(args).Invoke();

using System.CommandLine;

namespace S3VFS;

/// <summary>
/// Builds the <c>help</c> sub-command and the help-printing action shared
/// with the bare root command. Both delegate to System.CommandLine's built-in
/// <c>--help</c> rendering so the output always matches the registered
/// commands and options.
/// </summary>
internal static class HelpCommandFactory
{
    /// <summary>
    /// Builds <c>s3vfs help [&lt;command&gt;...]</c>. Without arguments it prints
    /// the top-level help; with a sub-command path (for example
    /// <c>s3vfs help lost-and-found restore</c>) it prints that sub-command's help.
    /// </summary>
    public static Command Build(RootCommand rootCommand)
    {
        var commandPath = new Argument<string[]>("command")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Sub-command to show help for (for example 'mount' or 'lost-and-found restore'). Omit to show the top-level help.",
        };

        var command = new Command("help", "Show the commands and options s3vfs supports.")
        {
            commandPath,
        };

        command.SetAction(parseResult =>
            ShowHelp(rootCommand, parseResult.GetValue(commandPath) ?? []));

        return command;
    }

    /// <summary>
    /// Prints help for the command reached by <paramref name="commandPath"/>
    /// (the root command when empty) and returns the exit code. An unknown
    /// sub-command is reported on stderr with a non-zero exit code instead of
    /// silently falling back to the top-level help.
    /// </summary>
    public static int ShowHelp(RootCommand rootCommand, IReadOnlyList<string> commandPath)
    {
        // '--help' makes System.CommandLine ignore unmatched tokens, so resolve
        // the path against the command tree up front.
        Command current = rootCommand;
        foreach (var name in commandPath)
        {
            var next = current.Subcommands.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name));
            if (next is null)
            {
                Console.Error.WriteLine(
                    $"Unknown command '{string.Join(' ', commandPath)}'. Run 's3vfs help' to list the available commands.");
                return 1;
            }
            current = next;
        }

        return rootCommand.Parse([.. commandPath, "--help"]).Invoke();
    }
}

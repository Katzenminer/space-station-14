using Robust.Shared.Console;

namespace Content.Client.Administration.Commands;

internal sealed class GenTrainingDataClientCommand : LocalizedEntityCommands
{
    public override string Command => "gentrainingdata";
    public override string Description => "Generates training data for a character with random loadouts.";
    public override string Help => "Usage: gentrainingdata <filepath> [count] [classId]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteLine(Help);
            return;
        }

        var commandStr = $"gentrainingdata {argStr}";
        shell.RemoteExecuteCommand(commandStr);
    }
}

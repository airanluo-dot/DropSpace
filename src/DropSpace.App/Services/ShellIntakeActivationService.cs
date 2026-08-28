using System.Text;
using DropSpace.App.ViewModels;
using DropSpace.Core.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace DropSpace.App.Services;

public sealed class ShellIntakeActivationService(
    MainViewModel viewModel,
    OverlayViewModel overlayViewModel,
    DispatcherQueue dispatcher,
    ILogger<ShellIntakeActivationService> logger)
{
    public async Task<bool> HandleCommandLineAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = ShellIntakeCommandLineParser.Parse(arguments);
        if (!result.IsShellIntake)
        {
            return false;
        }

        if (!result.Succeeded)
        {
            logger.LogWarning("Shell intake command was rejected as {ErrorCategory}.", result.ErrorCategory);
            return true;
        }

        await AddPathsAsync(result.Request!, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> HandleActivationAsync(
        AppActivationArguments activation,
        CancellationToken cancellationToken = default)
    {
        if (activation.Kind != ExtendedActivationKind.Launch ||
            activation.Data is not LaunchActivatedEventArgs launch)
        {
            return false;
        }

        var arguments = TokenizeWindowsCommandLine(launch.Arguments);
        return await HandleCommandLineAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddPathsAsync(
        ShellIntakeRequest request,
        CancellationToken cancellationToken)
    {
        var acquisitionKind = request.Source == ShellIntakeSource.SendTo
            ? "shell-sendto"
            : "shell-explorer-context-menu";
        int accepted;
        if (dispatcher.HasThreadAccess)
        {
            accepted = await viewModel.AddPathsBatchAsync(request.Paths, null, acquisitionKind, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            accepted = await dispatcher.EnqueueAsync(() =>
                viewModel.AddPathsBatchAsync(request.Paths, null, acquisitionKind, cancellationToken));
        }

        if (accepted > 0)
        {
            await overlayViewModel.ShowShellIntakeAcknowledgementAsync(accepted, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> TokenizeWindowsCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) ||
            commandLine.Length > ShellIntakeCommandLineParser.MaximumCommandLineCharacters)
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var slashCount = 0;
        foreach (var character in commandLine)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                current.Append('\\', slashCount / 2);
                if ((slashCount & 1) != 0)
                {
                    current.Append('"');
                }
                else
                {
                    quoted = !quoted;
                }

                slashCount = 0;
                continue;
            }

            current.Append('\\', slashCount);
            slashCount = 0;
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        current.Append('\\', slashCount);
        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}

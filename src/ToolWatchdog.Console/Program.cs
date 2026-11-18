// tool-watchdog.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return ToolWatchdog.Cli.Run(args, new ToolWatchdog.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });

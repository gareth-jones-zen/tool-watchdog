// tool-watchdog-background.exe: no window, ever. What the scheduled task runs (`run`); its log is the settings' log_file.
return ToolWatchdog.Cli.Run(args, new ToolWatchdog.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });

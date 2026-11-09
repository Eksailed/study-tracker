using Microsoft.EntityFrameworkCore;
using StudyTracker.Notifications;

namespace StudyTracker;

public static class CommandLine
{
    public static readonly string[] Flags = ["--migrate", "--telegram-poll", "--notifications-tick", "--notifications-plan", "--backup", "--restore"];

    /// <summary>Выполняет команду из аргументов и возвращает <c>true</c>, если это была команда (тогда сайт не запускается).</summary>
    public static async Task<bool> TryRunAsync(string[] args, WebApplication app)
    {
        if (args.Contains("--migrate")) return true; // миграции уже применены в DatabaseSetup.PrepareAsync
        if (args.Contains("--notifications-tick"))
        {
            var telegram = app.Services.GetRequiredService<TelegramClient>();
            if (await telegram.InitializeAsync(CancellationToken.None))
            {
                var sender = app.Services.GetRequiredService<ReminderSender>();
                await sender.RecoverAsync(CancellationToken.None);
                await app.Services.GetRequiredService<ReminderPlanner>().PlanAsync(CancellationToken.None);
                await sender.SendPendingAsync(CancellationToken.None);
            }
            else { Console.Error.WriteLine(telegram.Status); Environment.ExitCode = 1; }
            return true;
        }
        if (args.Contains("--notifications-plan"))
        {
            var telegram = app.Services.GetRequiredService<TelegramClient>();
            if (await telegram.InitializeAsync(CancellationToken.None))
                await app.Services.GetRequiredService<ReminderPlanner>().PlanAsync(CancellationToken.None);
            else { Console.Error.WriteLine(telegram.Status); Environment.ExitCode = 1; }
            return true;
        }
        if (args.Contains("--telegram-poll"))
        {
            var telegram = app.Services.GetRequiredService<TelegramClient>();
            if (await telegram.InitializeAsync(CancellationToken.None))
                await app.Services.GetRequiredService<TelegramUpdateWorker>().PollAsync(CancellationToken.None, timeout: 0);
            else { Console.Error.WriteLine(telegram.Status); Environment.ExitCode = 1; }
            return true;
        }
        if (args.Contains("--backup"))
        {
            Environment.ExitCode = await BackupCommands.RunAsync(args, app.Services, Console.Out, Console.Error);
            return true;
        }
        if (args.Contains("--create-account"))
        {
            try
            {
                var index = Array.IndexOf(args, "--create-account");
                if (index + 1 >= args.Length) throw new InvalidOperationException("Укажите почту после --create-account.");
                await AccountCommands.CreateAsync(app.Services, args[index + 1]);
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine(error.Message);
                Environment.ExitCode = 1;
            }
            return true;
        }
        if (args.Contains("--make-owner"))
        {
            try
            {
                var index = Array.IndexOf(args, "--make-owner");
                if (index + 1 >= args.Length) throw new InvalidOperationException("Укажите почту после --make-owner.");
                await AccountCommands.MakeOwnerAsync(app.Services, args[index + 1]);
                Console.WriteLine("Аккаунт теперь владелец.");
            }
            catch (InvalidOperationException error)
            {
                Console.Error.WriteLine(error.Message);
                Environment.ExitCode = 1;
            }
            return true;
        }
        if (args.Contains("--import-legacy"))
        {
            try
            {
                string Option(string name)
                {
                    var index = Array.IndexOf(args, name);
                    if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--"))
                        throw new InvalidOperationException($"Укажите значение {name}.");
                    return args[index + 1];
                }
                await LegacyImport.RunAsync(app.Services, Option("--import-legacy"), Option("--owner"));
            }
            catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or FormatException or DbUpdateException)
            {
                Console.Error.WriteLine("Перенос отменён: " + error.Message);
                Environment.ExitCode = 1;
            }
            return true;
        }
        return false;
    }
}

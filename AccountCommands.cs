using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace StudyTracker;

public static class AccountCommands
{
    public static async Task CreateAsync(IServiceProvider services, string email)
    {
        email = email.Trim();
        if (email.Length > 254 || !new EmailAddressAttribute().IsValid(email))
            throw new InvalidOperationException("Укажите корректную почту.");
        string password;
        if (Console.IsInputRedirected) password = Console.ReadLine() ?? "";
        else
        {
            Console.Write("Пароль (от 12 до 128 символов, ввод скрыт): ");
            var buffer = new StringBuilder();
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) buffer.Length--;
                else if (!char.IsControl(key.KeyChar) && buffer.Length < 129) buffer.Append(key.KeyChar);
            }
            Console.WriteLine();
            password = buffer.ToString();
        }
        if (password.Length is < 12 or > 128) throw new InvalidOperationException("Пароль должен содержать от 12 до 128 символов.");
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(new ApplicationUser { UserName = email, Email = email }, password);
        if (!result.Succeeded) throw new InvalidOperationException("Аккаунт не создан: проверьте адрес и отсутствие повторной регистрации.");
        Console.WriteLine("Аккаунт создан. Регистрация через браузер остаётся в прежнем режиме.");
    }
}

using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;

namespace SteelPans.Shared.Services;

public sealed class EmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendEmailAsync(
        string to,
        string subject,
        string body)
    {
        var host = configuration["Email:SmtpHost"]
                   ?? throw new InvalidOperationException("Missing Email:SmtpHost");

        var port = int.Parse(configuration["Email:SmtpPort"] ?? "587");

        var username = configuration["Email:Username"]
                       ?? throw new InvalidOperationException("Missing Email:Username");

        var password = configuration["Email:Password"]
                       ?? throw new InvalidOperationException("Missing Email:Password");

        var from = configuration["Email:From"]
                   ?? username;

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(username, password)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        await client.SendMailAsync(message);
    }
}
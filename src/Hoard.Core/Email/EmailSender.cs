using System.Net;
using System.Net.Mail;
using Hoard.Core.Configuration;
using Hoard.Core.Security;

namespace Hoard.Core.Email;

public sealed class EmailException(string message, Exception? inner = null) : Exception(message, inner);

public interface IEmailSender
{
    /// <summary>Sends one message (HTML with a plain-text alternative) to every recipient.</summary>
    Task SendAsync(IReadOnlyCollection<string> recipients, string subject, string html, string text, CancellationToken ct = default);
}

/// <summary>
/// SMTP with STARTTLS (Gmail: smtp.gmail.com:587 and an app password from myaccount.google.com/apppasswords). The
/// password is sealed in the database under <see cref="EmailSettings.PasswordSetting"/>.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly HoardSettings _settings;
    private readonly SecretSettings _secrets;

    public SmtpEmailSender(HoardSettings settings, SecretSettings secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public Task<bool> HasPasswordAsync(CancellationToken ct = default) => _secrets.HasAsync(EmailSettings.PasswordSetting, ct);

    /// <summary>Null keeps the stored password; blank deletes it.</summary>
    public Task SetPasswordAsync(string? password, CancellationToken ct = default) =>
        password is null ? Task.CompletedTask : _secrets.SetAsync(EmailSettings.PasswordSetting, password, ct);

    public async Task SendAsync(IReadOnlyCollection<string> recipients, string subject, string html, string text, CancellationToken ct = default)
    {
        var mail = _settings.Email;
        if (!mail.IsConfigured) throw new EmailException("Set up outgoing email first (Settings › Email).");
        var valid = recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0) throw new EmailException("Add at least one recipient.");
        var password = await _secrets.GetAsync(EmailSettings.PasswordSetting, ct)
                       ?? throw new EmailException("Enter the email password (for Gmail, an app password) under Settings › Email.");

        using var message = new MailMessage
        {
            From = new MailAddress(mail.EffectiveFrom, string.IsNullOrWhiteSpace(mail.FromName) ? null : mail.FromName),
            Subject = subject,
            SubjectEncoding = System.Text.Encoding.UTF8,
            BodyEncoding = System.Text.Encoding.UTF8,
            Body = text,
            IsBodyHtml = false,
        };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, System.Text.Encoding.UTF8, "text/html"));
        foreach (var r in valid)
        {
            try { message.To.Add(new MailAddress(r)); }
            catch (FormatException) { throw new EmailException($"\"{r}\" is not a valid email address."); }
        }

        using var client = new SmtpClient(mail.SmtpHost.Trim(), mail.SmtpPort)
        {
            EnableSsl = mail.UseStartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(mail.Username!.Trim(), password),
            Timeout = 60_000,
        };
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException ex)
        {
            throw new EmailException(Explain(ex, mail), ex);
        }
    }

    private static string Explain(SmtpException ex, EmailSettings mail)
    {
        var detail = ex.InnerException?.Message ?? ex.Message;
        if (ex.StatusCode is SmtpStatusCode.MustIssueStartTlsFirst || detail.Contains("5.7.0", StringComparison.Ordinal))
            return $"The mail server refused the login. {(mail.SmtpHost.Contains("gmail", StringComparison.OrdinalIgnoreCase) ? "Gmail needs an app password (myaccount.google.com/apppasswords), not your normal password, and 2-Step Verification turned on." : "Check the username and password.")} ({detail})";
        if (detail.Contains("5.7.8", StringComparison.Ordinal) || detail.Contains("Username and Password not accepted", StringComparison.OrdinalIgnoreCase) || detail.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return $"The username or password was not accepted. {(mail.SmtpHost.Contains("gmail", StringComparison.OrdinalIgnoreCase) ? "For Gmail use an app password from myaccount.google.com/apppasswords." : "")} ({detail})";
        return $"Sending failed: {detail}";
    }
}

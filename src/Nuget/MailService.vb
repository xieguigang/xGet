Imports System.IO
Imports System.Net.Mail
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports Readership

''' <summary>
''' the smtp account configuration of the nuget server. the secret parts are
''' never stored in plain text: the whole configuration json is encrypted.
''' </summary>
Public Class MailConfig
    Public Property host As String = ""
    Public Property port As Integer = 587
    Public Property ssl As Boolean = True
    Public Property user As String = ""
    Public Property password As String = ""

    ''' <summary>the from address, for example ``noreply@example.com``.</summary>
    Public Property from As String = ""

    ''' <summary>the optional display name of the sender.</summary>
    Public Property senderName As String = ""

    Public Function IsValid() As Boolean
        Return Not String.IsNullOrWhiteSpace(host) AndAlso
               Not String.IsNullOrWhiteSpace(from) AndAlso
               port > 0 AndAlso port <= 65535
    End Function
End Class

''' <summary>
''' the mail sending service of the nuget server.
''' 
''' the smtp configuration is stored inside the ``server_settings`` database
''' table as a salted encrypted blob: a per record random 16 byte salt and a
''' machine local 32 byte key file (``mail.key`` inside the data directory)
''' are fed into PBKDF2-SHA256, and the derived key encrypts the configuration
''' json with AES-GCM. the payload format is
''' ``base64(salt | nonce | ciphertext | tag)``. as a result, the smtp
''' password can not be read from a stolen database file alone.
''' 
''' the verification mail and the verification success page are rendered from
''' the replaceable template files of the ``template`` folder (with a built-in
''' fallback), so the registration tutorial can be edited without a rebuild.
''' </summary>
Public Module MailService

    ''' <summary>the ``server_settings`` key of the encrypted smtp config.</summary>
    Public Const MailConfigSetting As String = "mail.config"

    Private Const KeyFileName As String = "mail.key"
    Private Const SaltSize As Integer = 16
    Private Const NonceSize As Integer = 12
    Private Const TagSize As Integer = 16
    Private Const KeySize As Integer = 32
    Private Const Pbkdf2Iterations As Integer = 100000

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ''' <summary>
    ''' test whether a usable smtp configuration exists.
    ''' </summary>
    Public Function IsConfigured(store As NugetStore, dataDirectory As String) As Boolean
        Dim config As MailConfig = LoadConfig(store, dataDirectory)
        Return config IsNot Nothing AndAlso config.IsValid()
    End Function

    ''' <summary>
    ''' load and decrypt the smtp configuration; returns <c>Nothing</c> when it
    ''' was never configured.
    ''' </summary>
    Public Function LoadConfig(store As NugetStore, dataDirectory As String) As MailConfig
        Dim payload As String = store.GetSetting(MailConfigSetting)

        If String.IsNullOrEmpty(payload) Then
            Return Nothing
        End If

        Try
            Dim keyFile As Byte() = getOrCreateKeyFile(dataDirectory)
            Dim blob As Byte() = Convert.FromBase64String(payload)

            If blob.Length < SaltSize + NonceSize + TagSize Then
                Return Nothing
            End If

            Dim salt(SaltSize - 1) As Byte
            Dim nonce(NonceSize - 1) As Byte
            Dim cipher(blob.Length - SaltSize - NonceSize - TagSize - 1) As Byte
            Dim tag(TagSize - 1) As Byte

            Array.Copy(blob, 0, salt, 0, SaltSize)
            Array.Copy(blob, SaltSize, nonce, 0, NonceSize)
            Array.Copy(blob, SaltSize + NonceSize, cipher, 0, cipher.Length)
            Array.Copy(blob, blob.Length - TagSize, tag, 0, TagSize)

            Using aes As New AesGcm(deriveKey(keyFile, salt), TagSize)
                Dim plain(cipher.Length - 1) As Byte
                Call aes.Decrypt(nonce, cipher, tag, plain)
                Return JsonSerializer.Deserialize(Of MailConfig)(Encoding.UTF8.GetString(plain), JsonOptions)
            End Using
        Catch ex As Exception
            Call $"the mail configuration could not be decrypted: {ex.Message}".warning()
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' encrypt and store the smtp configuration.
    ''' </summary>
    Public Sub SaveConfig(store As NugetStore, dataDirectory As String, config As MailConfig)
        Dim keyFile As Byte() = getOrCreateKeyFile(dataDirectory)
        Dim salt(SaltSize - 1) As Byte

        Call RandomNumberGenerator.Fill(salt)

        Dim plain As Byte() = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, JsonOptions))
        Dim cipher(plain.Length - 1) As Byte
        Dim tag(TagSize - 1) As Byte
        Dim nonce(NonceSize - 1) As Byte

        Call RandomNumberGenerator.Fill(nonce)

        Using aes As New AesGcm(deriveKey(keyFile, salt), TagSize)
            Call aes.Encrypt(nonce, plain, cipher, tag)
        End Using

        Dim blob(SaltSize + NonceSize + cipher.Length + TagSize - 1) As Byte
        Dim offset As Integer = 0

        Array.Copy(salt, 0, blob, offset, SaltSize) : offset += SaltSize
        Array.Copy(nonce, 0, blob, offset, NonceSize) : offset += NonceSize
        Array.Copy(cipher, 0, blob, offset, cipher.Length) : offset += cipher.Length
        Array.Copy(tag, 0, blob, offset, TagSize)

        Call store.SetSetting(MailConfigSetting, Convert.ToBase64String(blob))
    End Sub

    ''' <summary>
    ''' remove the stored smtp configuration.
    ''' </summary>
    Public Sub ClearConfig(store As NugetStore)
        Call store.DeleteSetting(MailConfigSetting)
    End Sub

    ''' <summary>
    ''' send one html mail through the configured smtp account.
    ''' </summary>
    ''' <returns><c>True</c> when the message was handed to the smtp server.</returns>
    Public Function Send(config As MailConfig, [to] As String, subject As String, htmlBody As String, Optional ByRef errorMessage As String = "") As Boolean
        If config Is Nothing OrElse Not config.IsValid() Then
            errorMessage = "the smtp server is not configured."
            Return False
        End If

        Try
            Using message As New MailMessage With {
                .Subject = subject,
                .Body = htmlBody,
                .IsBodyHtml = True,
                .BodyEncoding = Encoding.UTF8,
                .SubjectEncoding = Encoding.UTF8,
                .From = New MailAddress(config.from, If(String.IsNullOrEmpty(config.senderName), config.from, config.senderName))
            }

                Call message.To.Add(New MailAddress([to]))

                Using client As New SmtpClient(config.host, config.port) With {
                    .EnableSsl = config.ssl,
                    .DeliveryMethod = SmtpDeliveryMethod.Network,
                    .Timeout = 30000
                }
                    If Not String.IsNullOrEmpty(config.user) Then
                        client.Credentials = New System.Net.NetworkCredential(config.user, config.password)
                    End If

                    Call client.Send(message)
                End Using
            End Using

            Return True
        Catch ex As Exception
            errorMessage = ex.Message
            Return False
        End Try
    End Function

    ''' <summary>
    ''' render the verification mail html body from the replaceable template
    ''' file (``template/verify-email.html``) with the built-in fallback.
    ''' </summary>
    Public Function RenderVerifyEmail(templateDirectory As String, email As String,
                                      verifyUrl As String, serverUrl As String, ttlMinutes As Integer) As String
        Dim values As New Dictionary(Of String, String) From {
            {"email", DocHtml.Escape(email)},
            {"verify_url", DocHtml.Attr(verifyUrl)},
            {"verify_url_text", DocHtml.Escape(verifyUrl)},
            {"server", DocHtml.Escape(serverUrl)},
            {"ttl", ttlMinutes.ToString}
        }

        Return renderTemplate(templateDirectory, "verify-email.html", values, fallbackVerifyEmail)
    End Function

    ''' <summary>
    ''' render the self service totp secret reset mail html body from the
    ''' replaceable template file (``template/reset-email.html``) with the
    ''' built-in fallback.
    ''' </summary>
    Public Function RenderResetEmail(templateDirectory As String, email As String,
                                     resetUrl As String, serverUrl As String, ttlMinutes As Integer) As String
        Dim values As New Dictionary(Of String, String) From {
            {"email", DocHtml.Escape(email)},
            {"reset_url", DocHtml.Attr(resetUrl)},
            {"reset_url_text", DocHtml.Escape(resetUrl)},
            {"server", DocHtml.Escape(serverUrl)},
            {"ttl", ttlMinutes.ToString}
        }

        Return renderTemplate(templateDirectory, "reset-email.html", values, fallbackResetEmail)
    End Function

    ''' <summary>
    ''' render the verification success page from the replaceable template file
    ''' (``template/verify-success.html``) with the built-in fallback.
    ''' </summary>
    Public Function RenderVerifySuccess(templateDirectory As String, email As String,
                                        payloadBase64 As String, activateCommand As String, serverUrl As String) As String
        Dim values As New Dictionary(Of String, String) From {
            {"email", DocHtml.Escape(email)},
            {"payload", DocHtml.Escape(payloadBase64)},
            {"activate_command", DocHtml.Escape(activateCommand)},
            {"server", DocHtml.Escape(serverUrl)}
        }

        Return renderTemplate(templateDirectory, "verify-success.html", values, fallbackVerifySuccess)
    End Function

    ''' <summary>
    ''' render an error page for an invalid or expired verification link.
    ''' </summary>
    ''' <param name="templateDirectory">the template directory of the server.</param>
    ''' <param name="reason">the human readable failure reason.</param>
    ''' <param name="serverUrl">the public base url used by the re-registration hint.</param>
    Public Function RenderVerifyFailed(templateDirectory As String, reason As String, serverUrl As String) As String
        Dim values As New Dictionary(Of String, String) From {
            {"reason", DocHtml.Escape(reason)},
            {"server", DocHtml.Escape(If(serverUrl, ""))}
        }

        Return renderTemplate(templateDirectory, "verify-failed.html", values, fallbackVerifyFailed)
    End Function

    ''' <summary>
    ''' render one template file with the ``{{placeholder}}`` replacement; the
    ''' built-in fallback template is used when the file does not exist.
    ''' </summary>
    Private Function renderTemplate(templateDirectory As String, templateName As String,
                                    values As Dictionary(Of String, String), fallback As String) As String
        Dim template As String = Nothing

        Try
            If Not String.IsNullOrEmpty(templateDirectory) Then
                Dim templateFile As String = Path.Combine(templateDirectory, templateName)

                If File.Exists(templateFile) Then
                    template = File.ReadAllText(templateFile)
                End If
            End If
        Catch ex As Exception
            Call $"the mail template '{templateName}' could not be read: {ex.Message}".warning()
        End Try

        If String.IsNullOrWhiteSpace(template) Then
            template = fallback
        End If

        Return DocTemplate.Render(template, values)
    End Function

    ''' <summary>
    ''' load (or lazily create) the machine local key file which protects the
    ''' smtp configuration. the key never leaves the server data directory.
    ''' </summary>
    Private Function getOrCreateKeyFile(dataDirectory As String) As Byte()
        Dim keyFilePath As String = Path.Combine(dataDirectory, KeyFileName)

        If File.Exists(keyFilePath) Then
            Dim existing As Byte() = File.ReadAllBytes(keyFilePath)

            If existing.Length = KeySize Then
                Return existing
            End If
        End If

        Dim key(KeySize - 1) As Byte
        Call RandomNumberGenerator.Fill(key)
        Call File.WriteAllBytes(keyFilePath, key)

        Call $"a new mail encryption key file was created: {keyFilePath}".info()

        Return key
    End Function

    Private Function deriveKey(keyFile As Byte(), salt As Byte()) As Byte()
        Return Rfc2898DeriveBytes.Pbkdf2(keyFile, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize)
    End Function

    ''' <summary>
    ''' the built-in verification mail template (html mail client compatible
    ''' inline styles).
    ''' </summary>
    Private Const fallbackVerifyEmail As String =
        "<!DOCTYPE html><html lang=""en""><head><meta charset=""UTF-8"" /></head>" &
        "<body style=""margin:0;padding:24px;background:#F5F7FB;font-family:'Segoe UI',Helvetica,Arial,sans-serif;"">" &
        "<div style=""max-width:560px;margin:0 auto;background:#FFFFFF;border-radius:12px;overflow:hidden;"">" &
        "<div style=""background:linear-gradient(135deg,#062E9A,#3B82F6);padding:28px 32px;"">" &
        "<h1 style=""margin:0;color:#FFFFFF;font-size:24px;"">xDoc NuGet Server</h1>" &
        "<p style=""margin:6px 0 0;color:#DBEAFE;font-size:14px;"">email address verification</p></div>" &
        "<div style=""padding:32px;"">" &
        "<p style=""margin:0 0 16px;color:#1F2937;font-size:16px;"">Hello <b>{{email}}</b>,</p>" &
        "<p style=""margin:0 0 24px;color:#6B7280;font-size:14px;line-height:1.6;"">" &
        "please confirm your email address to finish the registration on <b>{{server}}</b>. " &
        "this verification link is valid for <b>{{ttl}} minute(s)</b>.</p>" &
        "<p style=""margin:0 0 24px;text-align:center;"">" &
        "<a href=""{{verify_url}}"" style=""display:inline-block;padding:14px 36px;background:#3B82F6;color:#FFFFFF;" & _
        "text-decoration:none;border-radius:8px;font-size:16px;font-weight:600;"">Verify my email</a></p>" &
        "<p style=""margin:0 0 8px;color:#6B7280;font-size:13px;"">if the button does not work, copy this link into your browser:</p>" &
        "<p style=""margin:0 0 28px;color:#3B82F6;font-size:13px;word-break:break-all;"">{{verify_url_text}}</p>" &
        "<div style=""border-top:1px solid #E5E7EB;padding-top:20px;"">" &
        "<h2 style=""margin:0 0 12px;color:#1F2937;font-size:16px;"">Registration tutorial</h2>" &
        "<ol style=""margin:0;padding-left:20px;color:#6B7280;font-size:13px;line-height:1.8;"">" &
        "<li>wait for this verification mail and open the link above;</li>" &
        "<li>after the verification succeeds, the page shows a base64 authorization code;</li>" &
        "<li>copy the code and save it locally with the xGet command line:" &
        "<br /><code style=""display:inline-block;margin-top:6px;padding:8px 12px;background:#1F2937;color:#D1FAE5;" & _
        "border-radius:6px;font-family:Consolas,monospace;font-size:12px;"">xGet activate --server {{server}} --email {{email}} --code &lt;the base64 code&gt;</code></li>" &
        "<li>upload your packages afterwards: <code style=""font-family:Consolas,monospace;"">xGet upload --server {{server}} --email {{email}} --file &lt;package.nupkg&gt;</code></li>" &
        "</ol></div></div>" &
        "<div style=""padding:20px 32px;background:#F9FAFB;color:#9CA3AF;font-size:12px;"">" &
        "if you did not request this registration, you can safely ignore this mail.</div></div></body></html>"

    ''' <summary>
    ''' the built-in verification success page template.
    ''' </summary>
    Private Const fallbackVerifySuccess As String =
        "<!DOCTYPE html><html lang=""en""><head><meta charset=""UTF-8"" />" &
        "<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />" &
        "<title>Email verified</title></head>" &
        "<body style=""margin:0;padding:40px 16px;background:#F5F7FB;font-family:'Segoe UI',Helvetica,Arial,sans-serif;"">" &
        "<div style=""max-width:640px;margin:0 auto;background:#FFFFFF;border-radius:12px;padding:36px;"">" &
        "<div style=""width:52px;height:52px;border-radius:50%;background:#16A34A;color:#FFFFFF;font-size:28px;line-height:52px;text-align:center;"">&#10003;</div>" &
        "<h1 style=""margin:18px 0 8px;color:#1F2937;font-size:24px;"">Email verified</h1>" &
        "<p style=""margin:0 0 20px;color:#6B7280;font-size:14px;"">" &
        "the account <b>{{email}}</b> is now active on {{server}}.</p>" &
        "<p style=""margin:0 0 8px;color:#1F2937;font-size:14px;font-weight:500;"">" &
        "copy the base64 authorization code below and save it with xGet:</p>" &
        "<pre style=""margin:0 0 16px;padding:14px;background:#1F2937;color:#D1FAE5;border-radius:8px;font-family:Consolas,monospace;font-size:12px;white-space:pre-wrap;word-break:break-all;"">{{payload}}</pre>" &
        "<p style=""margin:0 0 8px;color:#1F2937;font-size:14px;font-weight:500;"">then run:</p>" &
        "<pre style=""margin:0 0 20px;padding:14px;background:#F3F4F6;color:#062E9A;border-radius:8px;font-family:Consolas,monospace;font-size:12px;white-space:pre-wrap;word-break:break-all;"">{{activate_command}}</pre>" &
        "<p style=""margin:0;color:#9CA3AF;font-size:12px;"">" &
        "keep this code private: it carries your TOTP secret which signs every package upload.</p></div></body></html>"

    ''' <summary>
    ''' the built-in verification failure page template.
    ''' </summary>
    Private Const fallbackVerifyFailed As String =
        "<!DOCTYPE html><html lang=""en""><head><meta charset=""UTF-8"" />" &
        "<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />" &
        "<title>Verification failed</title></head>" &
        "<body style=""margin:0;padding:40px 16px;background:#F5F7FB;font-family:'Segoe UI',Helvetica,Arial,sans-serif;"">" &
        "<div style=""max-width:640px;margin:0 auto;background:#FFFFFF;border-radius:12px;padding:36px;"">" &
        "<div style=""width:52px;height:52px;border-radius:50%;background:#DC2626;color:#FFFFFF;font-size:28px;line-height:52px;text-align:center;"">!</div>" &
        "<h1 style=""margin:18px 0 8px;color:#1F2937;font-size:24px;"">Verification failed</h1>" &
        "<p style=""margin:0 0 12px;color:#6B7280;font-size:14px;"">{{reason}}</p>" &
        "<p style=""margin:0;color:#6B7280;font-size:14px;"">" &
        "please open the link from your mailbox again while it is still valid, or run " &
        "<code style=""font-family:Consolas,monospace;"">xGet reset</code> to receive a fresh link.</p>" &
        "</div></body></html>"

    ''' <summary>
    ''' the built-in totp secret reset mail template.
    ''' </summary>
    Private Const fallbackResetEmail As String =
        "<!DOCTYPE html><html lang=""en""><head><meta charset=""UTF-8"" />" &
        "<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />" &
        "<title>TOTP secret reset</title></head>" &
        "<body style=""margin:0;padding:40px 16px;background:#F5F7FB;font-family:'Segoe UI',Helvetica,Arial,sans-serif;"">" &
        "<div style=""max-width:640px;margin:0 auto;background:#FFFFFF;border-radius:12px;padding:36px;"">" &
        "<h1 style=""margin:0 0 16px;color:#1F2937;font-size:24px;"">Reset your nuget authorization</h1>" &
        "<p style=""margin:0 0 20px;color:#6B7280;font-size:14px;"">" &
        "a secret reset was requested for the account <b>{{email}}</b> on {{server}}. " &
        "open the link below to receive a new base64 authorization code: the old totp " &
        "secret stops working as soon as the reset page is opened.</p>" &
        "<p style=""margin:0 0 20px;color:#6B7280;font-size:14px;""><a href=""{{reset_url}}"">open the reset page</a> " &
        "(valid for {{ttl}} minutes)</p>" &
        "<p style=""margin:0;color:#6B7280;font-size:14px;"">" &
        "then save the new code locally with <code style=""font-family:Consolas,monospace;"">xGet activate</code>. " &
        "if you did not request this reset, simply ignore this mail.</p>" &
        "</div></body></html>"
End Module

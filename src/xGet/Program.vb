Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Text.Json

''' <summary>
''' xGet: an experimental nuget client that only implements the two operations
''' that the official client cannot do with the custom TOTP authentication:
''' registering a new email and uploading a package.
''' </summary>
Module Program

    Private Const UsageText As String =
        "xGet - experimental nuget client" & vbCrLf &
        vbCrLf &
        "usage:" & vbCrLf &
        "  xGet register --server <url> --email <email>" & vbCrLf &
        "  xGet activate --server <url> --email <email> --code <base64 authorization code>" & vbCrLf &
        "  xGet reset    --server <url> --email <email>  (mails a fresh authorization code)" & vbCrLf &
        "  xGet upload   --server <url> --email <email> --file <package.nupkg> [--timeout <minutes>]" & vbCrLf &
        "  xGet batch    --server <url> --email <email> --dir <folder> [--recursive] [--symbols] [--timeout <minutes>]" & vbCrLf &
        "  xGet obsolete --server <url> --email <email> --id <package-id> [--off]" & vbCrLf &
        "  xGet hide     --server <url> --email <email> --id <package-id> [--off]" & vbCrLf &
        vbCrLf &
        "options:" & vbCrLf &
        "  --server, -s   the nuget server base url, e.g. http://localhost:80" & vbCrLf &
        "  --email,  -e   the registered user email" & vbCrLf &
        "  --code,   -c   the base64 authorization code of the email verification success page" & vbCrLf &
        "  --file,   -f   the .nupkg file to upload" & vbCrLf &
        "  --dir,    -d   the folder to scan for batch upload" & vbCrLf &
        "  --id,     -i   the package id of the obsolete / hide command (a trailing id works too)" & vbCrLf &
        "  --off          clear the obsolete / hide flag again instead of setting it" & vbCrLf &
        "  --recursive    scan the sub directories too" & vbCrLf &
        "  --symbols      also upload the *.snupkg / *.symbols.nupkg packages" & vbCrLf &
        "  --timeout, -t  the upload timeout in minutes (default: 15, decimals allowed)" & vbCrLf &
        vbCrLf &
        "package state:" & vbCrLf &
        "  only the account which uploaded a package may change its state, and the" & vbCrLf &
        "  state is applied to the whole package id (every version of it):" & vbCrLf &
        "    obsolete  the package is still served and documented, but the web page" & vbCrLf &
        "              badges it as obsolete;" & vbCrLf &
        "    hide      the package is withdrawn from every public view: it is not" & vbCrLf &
        "              listed, not searchable, not downloadable and its api" & vbCrLf &
        "              documentation is not readable anymore." & vbCrLf &
        vbCrLf &
        "registration flow:" & vbCrLf &
        "  1. xGet register asks the server to send a verification mail to your inbox;" & vbCrLf &
        "  2. open the verification link (valid for 30 minutes);" & vbCrLf &
        "  3. copy the base64 authorization code from the success page;" & vbCrLf &
        "  4. save it locally with 'xGet activate --server <url> --email <email> --code <code>'."

    Function Main(args As String()) As Integer
        If args Is Nothing OrElse args.Length = 0 Then
            Call printUsage()
            Return 1
        End If

        Dim command As String = args(0).TrimStart("-"c, "/"c).ToLowerInvariant()
        Dim options As Dictionary(Of String, String) = parseOptions(args)

        Select Case command
            Case "register", "reg"
                Return register(options)
            Case "reset"
                Return reset(options)
            Case "activate", "active", "login"
                Return activate(options)
            Case "upload", "push"
                Return upload(options)
            Case "batch", "upload-dir"
                Return batch(options)
            Case "obsolete", "deprecate"
                Return packageFlag(options, "obsolete")
            Case "hide", "unlist", "hidden"
                Return packageFlag(options, "hidden")
            Case "help", "?", "h"
                Call printUsage()
                Return 0
            Case Else
                Call Console.WriteLine($"unknown command: {args(0)}")
                Call printUsage()
                Return 1
        End Select
    End Function

    ''' <summary>
    ''' ask the server to start the registration of the given email: the server
    ''' sends a verification mail and the registration is completed on the
    ''' verification success page (whose base64 authorization code is saved
    ''' locally through the ``activate`` command).
    ''' </summary>
    Private Function register(options As Dictionary(Of String, String)) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(email) Then
            Call Console.WriteLine("usage: xGet register --server <url> --email <email>")
            Return 1
        End If

        Dim client As New NugetApiClient(server)
        Dim result As ApiResult = client.Register(email)

        If result Is Nothing Then
            Call Console.WriteLine("registration failed: unknown error")
            Return 2
        End If

        ' the server has no smtp account configured yet: the verification mail
        ' can not be sent, the user has to remind the server administrator.
        ' the account already exists: the fresh authorization code is recovered
        ' through the self service reset flow instead of the registration.
        If result.warning = "already-registered" Then
            Call Console.WriteLine(result.message)
            Call Console.WriteLine()
            Call Console.WriteLine("next steps:")
            Call Console.WriteLine("  1. if you still have the local authorization code of this account, keep using it;")
            Call Console.WriteLine("  2. otherwise request a fresh one by mail:")
            Call Console.WriteLine("     xGet reset --server " + normalizeServer(server) + " --email " + email)
            Return 0
        End If

        If result.warning = "mail-not-configured" Then
            Call Console.WriteLine("WARNING: the mail server of the nuget server is not configured yet,")
            Call Console.WriteLine("         so the verification email can not be sent.")
            Call Console.WriteLine("         please remind the server administrator to configure the")
            Call Console.WriteLine("         mail server in the backend (xConsole: 'mail set').")
            Return 3
        End If

        If Not result.ok Then
            Call Console.WriteLine($"registration failed: {If(result.message, "unknown error")}")
            Return 2
        End If

        Call Console.WriteLine(result.message)
        Call Console.WriteLine()
        Call Console.WriteLine("next steps:")
        Call Console.WriteLine("  1. open the verification link in your mailbox (it is valid for 30 minutes);")
        Call Console.WriteLine("  2. copy the base64 authorization code from the verification success page;")
        Call Console.WriteLine("  3. save it locally with:")
        Call Console.WriteLine($"     xGet activate --server {normalizeServer(server)} --email {email} --code <the base64 code>")
        Return 0
    End Function

    ''' <summary>
    ''' ask the server to mail a fresh authorization code for the given email:
    ''' the recovery path for an account whose local TOTP secret was lost.
    ''' </summary>
    Private Function reset(options As Dictionary(Of String, String)) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(email) Then
            Call Console.WriteLine("usage: xGet reset --server <url> --email <email>")
            Return 1
        End If

        Dim client As New NugetApiClient(server)
        Dim result As ApiResult = client.RequestReset(email)

        If result Is Nothing Then
            Call Console.WriteLine("reset failed: unknown error")
            Return 2
        End If

        If Not result.ok Then
            Call Console.WriteLine($"reset failed: {If(result.message, "unknown error")}")
            Return 2
        End If

        Call Console.WriteLine(result.message)
        Call Console.WriteLine()

        If result.warning = "reset-already-pending" Then
            Call Console.WriteLine("next steps:")
            Call Console.WriteLine("  1. open the reset link which was mailed to you earlier (it is still valid);")
            Call Console.WriteLine("  2. copy the base64 authorization code from the reset page;")
            Call Console.WriteLine("  3. save it locally with:")
            Call Console.WriteLine($"     xGet activate --server {normalizeServer(server)} --email {email} --code <the base64 code>")
            Return 0
        End If

        Call Console.WriteLine("next steps:")
        Call Console.WriteLine("  1. open the reset link in your mailbox (it is valid for 30 minutes);")
        Call Console.WriteLine("  2. copy the new base64 authorization code from the reset page;")
        Call Console.WriteLine("  3. save it locally with:")
        Call Console.WriteLine($"     xGet activate --server {normalizeServer(server)} --email {email} --code <the base64 code>")
        Return 0
    End Function

    ''' <summary>
    ''' save the base64 authorization code of the email verification success
    ''' page into the local account store: the code carries the email, the
    ''' server url and the TOTP secret of the new account.
    ''' </summary>
    Private Function activate(options As Dictionary(Of String, String)) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")
        Dim code As String = getOption(options, "code", "c")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(code) Then
            Call Console.WriteLine("usage: xGet activate --server <url> --email <email> --code <base64 authorization code>")
            Return 1
        End If

        Dim payload As Dictionary(Of String, String) = decodeAuthorization(code)

        If payload Is Nothing Then
            Call Console.WriteLine("activation failed: the --code value is not a valid base64 authorization code.")
            Return 1
        End If

        Dim payloadServer As String = normalizeServer(payload("server"))
        Dim payloadEmail As String = If(payload("email"), "").Trim()
        Dim secret As String = If(payload("secret"), "")

        If String.IsNullOrEmpty(payloadEmail) OrElse String.IsNullOrEmpty(secret) Then
            Call Console.WriteLine("activation failed: the authorization code is incomplete (email or totp secret is missing).")
            Return 1
        End If

        If Not String.IsNullOrEmpty(email) AndAlso
           Not payloadEmail.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) Then
            Call Console.WriteLine($"activation failed: the code was issued for '{payloadEmail}', not for '{email}'.")
            Return 1
        End If

        If Not payloadServer.Equals(normalizeServer(server), StringComparison.OrdinalIgnoreCase) Then
            Call Console.WriteLine($"activation failed: the code was issued for the server '{payloadServer}', not for '{normalizeServer(server)}'.")
            Return 1
        End If

        Dim account As New AccountStore()
        Call account.Save(payloadServer, payloadEmail, secret)

        Call Console.WriteLine($"the account '{payloadEmail}' on {payloadServer} is now activated.")
        Call Console.WriteLine($"the TOTP secret has been saved to: {account.StoreFile}")
        Return 0
    End Function

    ''' <summary>
    ''' decode the base64 authorization payload (a json object with the keys
    ''' ``email``, ``server`` and ``secret``).
    ''' </summary>
    Private Function decodeAuthorization(code As String) As Dictionary(Of String, String)
        Try
            Dim json As String = Text.Encoding.UTF8.GetString(Convert.FromBase64String(If(code, "").Trim()))
            Return JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    Private Function normalizeServer(server As String) As String
        Dim url As String = If(server, "").Trim().TrimEnd("/"c)

        If url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
           url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            Return url
        End If

        Return If(url = "", "", "http://" & url)
    End Function

    Private Function upload(options As Dictionary(Of String, String)) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")
        Dim package As String = getOption(options, "file", "f")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(email) OrElse String.IsNullOrEmpty(package) Then
            Call Console.WriteLine("usage: xGet upload --server <url> --email <email> --file <package.nupkg>")
            Return 1
        End If

        If Not File.Exists(package) Then
            Call Console.WriteLine($"package file not found: {package}")
            Return 1
        End If

        Dim timeout As TimeSpan? = parseTimeout(options)

        If timeout Is Nothing Then
            Call Console.WriteLine("invalid --timeout value, please provide a positive number of minutes")
            Return 1
        End If

        Dim account As New AccountStore()
        Dim secret As String = account.GetSecret(server, email)

        If String.IsNullOrEmpty(secret) Then
            Call Console.WriteLine($"no TOTP secret was found for '{email}' on {server}.")
            Call Console.WriteLine("please register this email first: xGet register --server <url> --email <email>")
            Return 1
        End If

        Dim code As String = Nuget.TotpModule.GenerateTotp(secret)
        Dim client As New NugetApiClient(server)
        Dim result As ApiResult = client.Upload(email, code, package, timeout)

        If result Is Nothing OrElse Not result.ok Then
            Call Console.WriteLine($"upload failed: {If(result?.message, "unknown error")}")
            Return 2
        End If

        Dim name As String = If(String.IsNullOrEmpty(result.id), Path.GetFileNameWithoutExtension(package), result.id)
        Dim version As String = If(result.version, "")

        Call Console.WriteLine($"uploaded {name} {version}".Trim())
        Return 0
    End Function

    ''' <summary>
    ''' set (or clear with ``--off``) one of the two public package flags of a
    ''' package id: the ``obsolete`` marker and the ``hidden`` marker. the
    ''' operation is authenticated with the TOTP secret of the local account
    ''' store, and the server accepts it from the uploader account of the
    ''' package only.
    ''' </summary>
    ''' <param name="options">the parsed command line options.</param>
    ''' <param name="flagName">either ``obsolete`` or ``hidden``.</param>
    Private Function packageFlag(options As Dictionary(Of String, String), flagName As String) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")
        Dim id As String = getOption(options, "id", "package", "p", "name", "_1")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(email) OrElse String.IsNullOrEmpty(id) Then
            Call Console.WriteLine($"usage: xGet {flagName} --server <url> --email <email> --id <package-id> [--off]")
            Return 1
        End If

        ' the flag is set by default; --off clears it again
        Dim flag As Boolean = Not hasFlag(options, "off", "unset", "clear", "disable")

        Dim account As New AccountStore()
        Dim secret As String = account.GetSecret(server, email)

        If String.IsNullOrEmpty(secret) Then
            Call Console.WriteLine($"no TOTP secret was found for '{email}' on {server}.")
            Call Console.WriteLine("please register this email first: xGet register --server <url> --email <email>")
            Return 1
        End If

        Dim code As String = Nuget.TotpModule.GenerateTotp(secret)
        Dim client As New NugetApiClient(server)
        Dim result As ApiResult = client.SetPackageFlag(email, code, id, flagName, flag)

        If result Is Nothing OrElse Not result.ok Then
            Call Console.WriteLine($"{flagName} failed: {If(result?.message, "unknown error")}")
            Return 2
        End If

        Call Console.WriteLine(result.message)
        Return 0
    End Function

    ''' <summary>
    ''' scan a folder for nuget packages and upload them one by one, reusing the
    ''' stored TOTP secret of the given email.
    ''' </summary>
    Private Function batch(options As Dictionary(Of String, String)) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")
        Dim folder As String = getOption(options, "dir", "d", "directory", "folder")
        Dim recursive As Boolean = hasFlag(options, "recursive", "r")
        Dim symbols As Boolean = hasFlag(options, "symbols")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(email) OrElse String.IsNullOrEmpty(folder) Then
            Call Console.WriteLine("usage: xGet batch --server <url> --email <email> --dir <folder> [--recursive] [--symbols] [--timeout <minutes>]")
            Return 1
        End If

        If Not Directory.Exists(folder) Then
            Call Console.WriteLine($"folder not found: {folder}")
            Return 1
        End If

        Dim timeout As TimeSpan? = parseTimeout(options)

        If timeout Is Nothing Then
            Call Console.WriteLine("invalid --timeout value, please provide a positive number of minutes")
            Return 1
        End If

        Dim account As New AccountStore()
        Dim secret As String = account.GetSecret(server, email)

        If String.IsNullOrEmpty(secret) Then
            Call Console.WriteLine($"no TOTP secret was found for '{email}' on {server}.")
            Call Console.WriteLine("please register this email first: xGet register --server <url> --email <email>")
            Return 1
        End If

        Dim files As List(Of String) = findPackages(folder, recursive, symbols)

        If files.Count = 0 Then
            Call Console.WriteLine($"no nuget packages were found in '{folder}'")
            Return 1
        Else
            files = New List(Of String)(files.OrderBy(Function(f) f.FileLength))
        End If

        Call Console.WriteLine($"found {files.Count} package(s) in '{folder}'" &
                               If(symbols, " (including symbols)", "") &
                               If(recursive, " (recursive)", ""))

        Dim client As New NugetApiClient(server)
        Dim uploaded As Integer = 0
        Dim skipped As Integer = 0
        Dim failed As Integer = 0
        Dim totalWatch As Stopwatch = Stopwatch.StartNew()

        For i As Integer = 0 To files.Count - 1
            Dim package As String = files(i)
            Dim name As String = Path.GetFileName(package)

            ' regenerate a fresh TOTP code for every package, otherwise a long
            ' batch upload could outlive the 30 seconds time step of one code.
            Dim code As String = Nuget.TotpModule.GenerateTotp(secret)
            Dim watch As Stopwatch = Stopwatch.StartNew()
            Dim result As ApiResult = client.Upload(email, code, package, timeout)
            watch.Stop()

            Dim elapsed As String = formatElapsed(watch.Elapsed)
            Dim message As String = If(result?.message, "")

            If result IsNot Nothing AndAlso result.ok Then
                uploaded += 1
                Call Console.WriteLine($"[{i + 1}/{files.Count}] ok      {name} ({elapsed})")
            ElseIf message.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0 Then
                skipped += 1
                Call Console.WriteLine($"[{i + 1}/{files.Count}] skip    {name} (already exists) ({elapsed})")
            ElseIf message.IndexOf("TOTP", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   message.IndexOf("invalid email", StringComparison.OrdinalIgnoreCase) >= 0 Then
                failed += 1
                Call Console.WriteLine($"[{i + 1}/{files.Count}] FAILED  {name}: {message} ({elapsed})")
                Call Console.WriteLine($"authentication failed, aborting the batch upload (total {formatElapsed(totalWatch.Elapsed)}).")
                Return 3
            Else
                failed += 1
                Call Console.WriteLine($"[{i + 1}/{files.Count}] failed  {name}: {message} ({elapsed})")
            End If
        Next

        totalWatch.Stop()
        Call Console.WriteLine($"batch upload finished: {uploaded} uploaded, {skipped} skipped, {failed} failed (total {files.Count}) in {formatElapsed(totalWatch.Elapsed)}")
        Return 0
    End Function

    ''' <summary>
    ''' scan a folder for the nuget packages to upload. by default only the top
    ''' level ``*.nupkg`` files are collected; the symbol packages are skipped
    ''' unless <paramref name="symbols"/> is set, and the sub directories are
    ''' skipped unless <paramref name="recursive"/> is set.
    ''' </summary>
    Private Function findPackages(folder As String, recursive As Boolean, symbols As Boolean) As List(Of String)
        Dim search As SearchOption = If(recursive, SearchOption.AllDirectories, SearchOption.TopDirectoryOnly)
        Dim result As New List(Of String)

        For Each file As String In Directory.GetFiles(folder, "*.nupkg", search)
            Dim name As String = Path.GetFileName(file).ToLowerInvariant()

            If Not symbols AndAlso (name.EndsWith(".snupkg") OrElse name.Contains(".symbols.")) Then
                Continue For
            End If

            Call result.Add(file)
        Next

        Call result.Sort(StringComparer.OrdinalIgnoreCase)
        Return result
    End Function

    Private Function hasFlag(options As Dictionary(Of String, String), ParamArray names As String()) As Boolean
        For Each name As String In names
            If options.ContainsKey(name) Then
                Return True
            End If
        Next
        Return False
    End Function

    ''' <summary>
    ''' parse the command line options. every ``--name value`` (or ``--name=value``)
    ''' token is stored under its name; a bare token is stored as the numbered
    ''' positional argument ``_1``, ``_2``, ... so that a package id can also be
    ''' given as a trailing word of the command.
    ''' </summary>
    Private Function parseOptions(args As String()) As Dictionary(Of String, String)
        Dim options As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim i As Integer = 1
        Dim positionals As Integer = 0

        While i < args.Length
            Dim token As String = args(i)

            If token.StartsWith("-"c) OrElse token.StartsWith("/"c) Then
                Dim name As String = token.TrimStart("-"c, "/"c)
                Dim equals As Integer = name.IndexOf("="c)

                If equals >= 0 Then
                    options(name.Substring(0, equals)) = name.Substring(equals + 1)
                    i += 1
                Else
                    Dim value As String = ""
                    If i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("-"c) Then
                        value = args(i + 1)
                        i += 2
                    Else
                        i += 1
                    End If
                    options(name) = value
                End If
            Else
                positionals += 1
                options("_" & positionals.ToString(CultureInfo.InvariantCulture)) = token
                i += 1
            End If
        End While

        Return options
    End Function

    Private Function getOption(options As Dictionary(Of String, String), ParamArray names As String()) As String
        For Each name As String In names
            Dim value As String = Nothing
            If options.TryGetValue(name, value) AndAlso Not String.IsNullOrEmpty(value) Then
                Return value.Trim()
            End If
        Next
        Return ""
    End Function

    ''' <summary>
    ''' read the "--timeout" option as a number of minutes. an absent option falls
    ''' back to the client default, while an explicit but invalid value returns
    ''' Nothing so that the caller can report the error.
    ''' </summary>
    Private Function parseTimeout(options As Dictionary(Of String, String)) As TimeSpan?
        Dim raw As String = getOption(options, "timeout", "t")

        If String.IsNullOrEmpty(raw) Then
            Return NugetApiClient.DefaultTimeout
        End If

        Dim minutes As Double

        If Not Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, minutes) OrElse minutes <= 0 Then
            Return Nothing
        End If

        Return TimeSpan.FromMinutes(minutes)
    End Function

    ''' <summary>
    ''' format a duration for the console report: seconds for the short uploads
    ''' and minutes plus seconds for the longer ones.
    ''' </summary>
    Private Function formatElapsed(value As TimeSpan) As String
        If value.TotalSeconds >= 60 Then
            Return $"{CInt(Math.Floor(value.TotalMinutes))}m{value.Seconds:00}.{value.Milliseconds \ 10:00}s"
        End If

        Return $"{value.TotalSeconds:0.00}s"
    End Function

    Private Sub printUsage()
        Call Console.WriteLine(UsageText)
    End Sub
End Module

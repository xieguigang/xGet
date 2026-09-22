Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO

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
        "  xGet upload   --server <url> --email <email> --file <package.nupkg> [--timeout <minutes>]" & vbCrLf &
        "  xGet batch    --server <url> --email <email> --dir <folder> [--recursive] [--symbols] [--timeout <minutes>]" & vbCrLf &
        vbCrLf &
        "options:" & vbCrLf &
        "  --server, -s   the nuget server base url, e.g. http://localhost:80" & vbCrLf &
        "  --email,  -e   the registered user email" & vbCrLf &
        "  --file,   -f   the .nupkg file to upload" & vbCrLf &
        "  --dir,    -d   the folder to scan for batch upload" & vbCrLf &
        "  --recursive    scan the sub directories too" & vbCrLf &
        "  --symbols      also upload the *.snupkg / *.symbols.nupkg packages" & vbCrLf &
        "  --timeout, -t  the upload timeout in minutes (default: 15, decimals allowed)"

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
            Case "upload", "push"
                Return upload(options)
            Case "batch", "upload-dir"
                Return batch(options)
            Case "help", "?", "h"
                Call printUsage()
                Return 0
            Case Else
                Call Console.WriteLine($"unknown command: {args(0)}")
                Call printUsage()
                Return 1
        End Select
    End Function

    Private Function register(options As Dictionary(Of String, String)) As Integer
        Dim server As String = getOption(options, "server", "s")
        Dim email As String = getOption(options, "email", "e")

        If String.IsNullOrEmpty(server) OrElse String.IsNullOrEmpty(email) Then
            Call Console.WriteLine("usage: xGet register --server <url> --email <email>")
            Return 1
        End If

        Dim client As New NugetApiClient(server)
        Dim result As ApiResult = client.Register(email)

        If result Is Nothing OrElse Not result.ok OrElse String.IsNullOrEmpty(result.secret) Then
            Call Console.WriteLine($"registration failed: {If(result?.message, "unknown error")}")
            Return 2
        End If

        Dim account As New AccountStore()
        Call account.Save(server, email, result.secret)

        Call Console.WriteLine($"registered '{email}' on {server}")
        Call Console.WriteLine($"the TOTP secret has been saved to: {account.StoreFile}")
        Return 0
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

    Private Function parseOptions(args As String()) As Dictionary(Of String, String)
        Dim options As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim i As Integer = 1

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

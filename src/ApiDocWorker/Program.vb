Imports System.IO
Imports System.Text.Json
Imports Nuget
Imports Readership

''' <summary>
''' the api comment document worker of the nuget server.
''' 
''' two modes are supported:
''' 
''' + ``--daemon``: the long living queue worker which the server starts at its
'''   own startup. it reads one json <see cref="ApiDocWorkerJob"/> per line from
'''   the stdin, extracts the xml comment documents of the uploaded package,
'''   stores them into the database, renders the static html pages below the web
'''   root and answers with one json <see cref="ApiDocWorkerResponse"/> per line
'''   on the stdout. the stdout is the protocol channel, so every other console
'''   output is redirected to the stderr;
''' + ``--nupkg &lt;file&gt; --output &lt;result.json&gt;``: the original one shot
'''   mode which only extracts the document model of one package. it is kept for
'''   the server side fall back and for the self checks.
''' </summary>
Module Program

    Function Main(args As String()) As Integer
        If hasFlag(args, "--daemon") Then
            Return runDaemon(args)
        Else
            Return runOnce(args)
        End If
    End Function

#Region "daemon mode"

    ''' <summary>
    ''' run the long living api document queue worker.
    ''' </summary>
    ''' <param name="args"></param>
    ''' <returns></returns>
    Private Function runDaemon(args As String()) As Integer
        ' the protocol channel must be the original stdout; redirect everything
        ' else (including the logging helpers of the server libraries, which use
        ' Console.WriteLine) to the stderr so that a log line can never corrupt a
        ' response line.
        Dim protocol As System.IO.TextWriter = Console.Out
        Call Console.SetOut(Console.Error)

        Dim options As New WorkerDaemonOptions With {
            .database = getOption(args, "--db"),
            .packages = getOption(args, "--packages"),
            .wwwroot = getOption(args, "--wwwroot"),
            .template = getOption(args, "--template"),
            .docsStatic = getOption(args, "--docs-static"),
            .docPixel = Not hasFlag(args, "--no-pixel"),
            .parentPid = parseInt(getOption(args, "--parent-pid"), 0),
            .reconcileSeconds = parseInt(getOption(args, "--reconcile-seconds"), 300),
            .maxAttempts = parseInt(getOption(args, "--max-attempts"), 3)
        }

        If String.IsNullOrEmpty(options.database) Then
            Call Console.Error.WriteLine(
                "usage: ApiDocWorker --daemon --db <dir> --wwwroot <dir> --template <dir> " &
                "[--packages <dir>] [--docs-static <dir>] [--parent-pid <pid>] " &
                "[--reconcile-seconds <n>] [--max-attempts <n>] [--no-pixel]")
            Return 1
        End If

        If options.reconcileSeconds < 30 Then
            options.reconcileSeconds = 30
        End If

        If options.maxAttempts < 1 Then
            options.maxAttempts = 1
        End If

        Dim daemon As New WorkerDaemon(options, protocol)

        Return daemon.Run()
    End Function

#End Region

#Region "one shot mode"

    ''' <summary>
    ''' extract the document model of one package into a json result file: the
    ''' server side fall back and the self checks use this mode.
    ''' </summary>
    ''' <param name="args"></param>
    ''' <returns></returns>
    Private Function runOnce(args As String()) As Integer
        Dim nupkg As String = getOption(args, "--nupkg")
        Dim output As String = getOption(args, "--output")

        If String.IsNullOrEmpty(nupkg) OrElse String.IsNullOrEmpty(output) Then
            Call Console.Error.WriteLine("usage: ApiDocWorker --nupkg <package.nupkg> --output <result.json>")
            Call Console.Error.WriteLine(
                "       ApiDocWorker --daemon --db <dir> --wwwroot <dir> --template <dir> [--parent-pid <pid>]")
            Return 1
        End If

        ' the protocol channel is unused in this mode, the result is written
        ' into a file; keeping the stdout empty avoids a pipe dead lock of the
        ' parent process which only drains the stderr.
        Call Console.SetOut(Console.Error)

        Dim result As New ApiDocWorkerResult With {.ok = True}

        If Not File.Exists(nupkg) Then
            result.ok = False
            result.message = $"the package file was not found: {nupkg}"
            Call writeResult(output, result)
            Return 2
        End If

        Dim warnings As New List(Of String)
        Dim document As ApiDocDocument = PackageApiDocs.ExtractInProcess(nupkg, warnings)

        If warnings.Count > 0 Then
            Call result.warnings.AddRange(warnings)
        End If

        If document Is Nothing Then
            result.ok = False
            result.message = "the api comment document extraction failed"
        Else
            result.document = document
        End If

        Call writeResult(output, result)

        Return If(result.ok, 0, 3)
    End Function

    Private Sub writeResult(path As String, result As ApiDocWorkerResult)
        Dim options As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True
        }
        Dim json As String = JsonSerializer.Serialize(result, options)

        Call File.WriteAllText(path, json, New Text.UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
    End Sub

#End Region

#Region "command line helpers"

    Private Function getOption(args As String(), name As String) As String
        If args Is Nothing Then
            Return ""
        End If

        For i As Integer = 0 To args.Length - 2
            If args(i).Equals(name, StringComparison.OrdinalIgnoreCase) Then
                Return args(i + 1)
            End If
        Next

        Return ""
    End Function

    Private Function hasFlag(args As String(), name As String) As Boolean
        If args Is Nothing Then
            Return False
        End If

        Return args.Any(Function(arg) arg.Equals(name, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Function parseInt(text As String, fallback As Integer) As Integer
        Dim value As Integer

        If Integer.TryParse(If(text, "").Trim(), value) Then
            Return value
        End If

        Return fallback
    End Function

#End Region
End Module

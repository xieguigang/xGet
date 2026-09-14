Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks
Imports Nuget
Imports JSql.Storage
Imports Microsoft.VisualBasic.Data.Repository

''' <summary>
''' entry point of the remote vb script language server. it parses the command
''' line (--data / --port), opens the nuget document database through
''' <see cref="NugetStore"/>, builds the read only <see cref="ApiIndex"/> and
''' starts the tcp listener which speaks the json-rpc 2.0 framing described in
''' src/LSP/jsonrpc.md.
''' </summary>
Module Program

    Private server As TcpListener
    Private index As ApiIndex
    Private ReadOnly shutdown As New CancellationTokenSource()

    ''' <summary>
    ''' the default listen port, matches the value configured in src/LSP/lsp.ts.
    ''' </summary>
    Private Const DefaultPort As Integer = 8080

    Sub Main(args As String())
        Dim dataDir As String = Nothing
        Dim port As Integer = DefaultPort

        If Not ParseArguments(args, dataDir, port) Then
            Environment.Exit(2)
        End If

        If String.IsNullOrWhiteSpace(dataDir) Then
            Console.Error.WriteLine("error: --data <path> is required (path to the nuget database directory).")
            Environment.Exit(2)
        End If

        Dim dbDir As String = ResolveDatabaseDirectory(dataDir)
        Console.WriteLine($"opening nuget database at: {dbDir}")

        Dim store As NugetStore = Nothing
        Try
            ' open the database as a read only reader: SharedRead lets several
            ' such processes hold the lock at once, and MultiProcessAccess makes
            ' the lock released between statements so the running nuget server
            ' (the exclusive writer) can interleave its writes with our reads.
            Dim readerOptions As New StorageOptions With {
                .MultiProcessAccess = True,
                .LockMode = TextStoreLockMode.SharedRead
            }
            store = New NugetStore(dbDir, readerOptions)
        Catch ex As Exception
            Console.Error.WriteLine($"error: failed to open nuget database: {ex.Message}")
            Environment.Exit(1)
        End Try

        index = New ApiIndex(store)
        index.Build()
        Console.WriteLine($"loaded {index.TypeCount} types across {index.NamespaceCount} namespaces from the nuget database.")

        server = New TcpListener(IPAddress.Loopback, port)
        server.Start()
        Console.WriteLine($"LSP server listening on port {port} (press Ctrl+C to stop)...")

        AddHandler Console.CancelKeyPress, AddressOf OnCancel

        Try
            While Not shutdown.IsCancellationRequested
                Dim client As TcpClient = server.AcceptTcpClient()
                Console.WriteLine("client connected")
                Dim session As New ClientSession(client, index)
                Task.Run(Function() session.RunAsync())
            End While
        Catch ex As ObjectDisposedException
            ' the listener was stopped during shutdown, this is expected
        Catch ex As Exception
            If Not shutdown.IsCancellationRequested Then
                Console.Error.WriteLine($"listener error: {ex.Message}")
            End If
        End Try
    End Sub

    ''' <summary>
    ''' parse the supported command line switches: --data &lt;path&gt; and --port
    ''' &lt;number&gt;. returns false when an unknown switch is given.
    ''' </summary>
    Private Function ParseArguments(args As String(), ByRef dataDir As String, ByRef port As Integer) As Boolean
        For i As Integer = 0 To args.Length - 1
            Dim arg As String = args(i)

            Select Case arg
                Case "--data", "-d"
                    If i + 1 >= args.Length Then
                        Console.Error.WriteLine("error: --data requires a path argument.")
                        Return False
                    End If
                    dataDir = args(i + 1)
                    i += 1
                Case "--port", "-p"
                    If i + 1 >= args.Length OrElse Not Integer.TryParse(args(i + 1), port) Then
                        Console.Error.WriteLine("error: --port requires a numeric argument.")
                        Return False
                    End If
                    i += 1
                Case "--help", "-h"
                    PrintUsage()
                    Environment.Exit(0)
                Case Else
                    Console.Error.WriteLine($"error: unknown argument '{arg}'.")
                    PrintUsage()
                    Return False
            End Select
        Next

        Return True
    End Function

    Private Sub PrintUsage()
        Console.WriteLine("usage: languageserver.exe --data <path> [--port <number>]")
        Console.WriteLine("  --data, -d   path to the nuget data directory (the folder that contains the 'nuget' database)")
        Console.WriteLine("  --port, -p   tcp listen port (default 8080)")
    End Sub

    ''' <summary>
    ''' locate the actual JSql database directory from the user supplied --data
    ''' path. the nuget server stores its tables under ``&lt;data&gt;/db/nuget``
    ''' (see NugetConfiguration.DatabaseDirectory), but a user may also pass the
    ''' directory that directly contains the ``nuget`` folder, so we probe a few
    ''' common layouts before falling back to the raw value.
    ''' </summary>
    Private Function ResolveDatabaseDirectory(data As String) As String
        data = Path.GetFullPath(data)

        If Directory.Exists(Path.Combine(data, "nuget")) Then
            Return data
        End If

        Dim db As String = Path.Combine(data, "db")
        If Directory.Exists(Path.Combine(db, "nuget")) Then
            Return db
        End If
        If Directory.Exists(db) Then
            Return db
        End If

        Return data
    End Function

    Private Sub OnCancel(sender As Object, e As ConsoleCancelEventArgs)
        e.Cancel = True
        Call shutdown.Cancel()

        Try
            server.Stop()
        Catch
        End Try

        Console.WriteLine("shutting down...")
    End Sub
End Module

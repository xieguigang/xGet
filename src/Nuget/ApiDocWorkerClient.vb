Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Readership

''' <summary>
''' the server side handle of the api document daemon.
''' 
''' the client starts the ``ApiDocWorker`` process at the server startup, feeds
''' the json job lines into its stdin and consumes the json response lines from
''' its stdout. The extraction, the database write and the html rendering all
''' happen inside the worker process, so the request threads of the server only
''' pay for one enqueue and one json serialization.
''' 
''' the worker is restarted with an exponential back off when it exits
''' unexpectedly, and the jobs which it had not answered yet are re-queued.
''' </summary>
Public Class ApiDocWorkerClient

    Private ReadOnly config As NugetConfiguration

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ''' <summary>the jobs waiting to be written into the stdin of the worker.</summary>
    Private ReadOnly queue As New ConcurrentQueue(Of ApiDocWorkerJob)

    ''' <summary>the de-duplication keys of the jobs which are still queued.</summary>
    Private ReadOnly queuedKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>the jobs which were written to the worker but are not answered yet, keyed by job id.</summary>
    Private ReadOnly inflight As New Dictionary(Of String, ApiDocWorkerJob)(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly queueGate As New Object
    Private ReadOnly inflightGate As New Object
    Private ReadOnly restartGate As New Object
    Private ReadOnly signal As New AutoResetEvent(False)

    Private process As Process
    Private reader As Thread
    Private writer As Thread
    Private stderrPump As Thread

    Private running As Boolean
    Private disposed As Boolean
    Private restartScheduled As Boolean
    Private restartDelaySeconds As Integer = 1

    ''' <summary>serializes <see cref="Start"/> so that two restart paths can not spawn two workers.</summary>
    Private ReadOnly startGate As New Object

    ''' <summary>the watchdog which restarts a worker whose exit event was missed.</summary>
    Private watchdog As Threading.Timer

    ''' <summary>
    ''' invoked on the reader thread for every answered job. the server uses it
    ''' to refresh the site map and to log the outcome.
    ''' </summary>
    Public Property OnCompleted As Action(Of ApiDocWorkerResponse)

    Public Sub New(config As NugetConfiguration)
        Me.config = config
    End Sub

    ''' <summary>
    ''' whether the worker process is currently alive.
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Available As Boolean
        Get
            Dim proc As Process = Me.process

            Try
                Return proc IsNot Nothing AndAlso Not proc.HasExited
            Catch
                Return False
            End Try
        End Get
    End Property

    ''' <summary>
    ''' the number of the jobs which are waiting or in flight.
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property PendingCount As Integer
        Get
            SyncLock queueGate
                SyncLock inflightGate
                    Return queue.Count + inflight.Count
                End SyncLock
            End SyncLock
        End Get
    End Property

#Region "lifecycle"

    ''' <summary>
    ''' locate and start the worker process. Returns <c>False</c> when the worker
    ''' executable is not deployed or can not be started, in which case the caller
    ''' falls back to the in-process extraction.
    ''' </summary>
    ''' <returns></returns>
    Public Function Start() As Boolean
        SyncLock startGate
            If disposed Then
                Return False
            End If

            If Available Then
                Return True
            End If

            Dim psi As ProcessStartInfo = startInfo()

            If psi Is Nothing Then
                Return False
            End If

            Try
                Me.process = Process.Start(psi)
            Catch ex As Exception
                Call $"[apidoc-worker] the worker process could not be started: {ex.Message}".warning()
                Me.process = Nothing
                Return False
            End Try

            If Me.process Is Nothing Then
                Call "[apidoc-worker] the worker process could not be started".warning()
                Return False
            End If

            Me.running = True

            ' the exit event of a process which was started after the event
            ' handler was attached is only raised when the watching is enabled.
            Me.process.EnableRaisingEvents = True
            AddHandler Me.process.Exited, AddressOf onProcessExited

            Me.reader = NewThread(AddressOf readResponses, "apidoc-worker-out")
            Me.stderrPump = NewThread(AddressOf pumpErrors, "apidoc-worker-err")
            Me.writer = NewThread(AddressOf writeJobs, "apidoc-worker-in")

            Call Me.reader.Start()
            Call Me.stderrPump.Start()
            Call Me.writer.Start()

            ' the watchdog covers the cases which the exit event can not: a
            ' process which hangs, and a restart attempt which failed itself.
            If Me.watchdog Is Nothing Then
                Me.watchdog = New Threading.Timer(AddressOf watchdogTick, Nothing,
                    dueTime:=TimeSpan.FromSeconds(15),
                    period:=TimeSpan.FromSeconds(15))
            End If

            Call $"[apidoc-worker] document daemon is running (pid={Me.process.Id})".info()

            Return True
        End SyncLock
    End Function

    ''' <summary>
    ''' the watchdog: start the worker again when it is not running and no restart
    ''' was scheduled by the exit event.
    ''' </summary>
    ''' <param name="state"></param>
    Private Sub watchdogTick(state As Object)
        If disposed OrElse Available Then
            Return
        End If

        SyncLock restartGate
            If restartScheduled Then
                Return
            End If

            restartScheduled = True
        End SyncLock

        Call ThreadPool.QueueUserWorkItem(
            Sub(item)
                Try
                    If Not disposed AndAlso Not Available Then
                        Call "[apidoc-worker] the worker is not running, restarting it".warning()
                        Call Start()
                    End If
                Finally
                    SyncLock restartGate
                        restartScheduled = False
                    End SyncLock
                End Try
            End Sub)
    End Sub

    Private Function NewThread(body As ThreadStart, name As String) As Thread
        Return New Thread(body) With {
            .IsBackground = True,
            .Name = name
        }
    End Function

    ''' <summary>
    ''' build the process start info of the worker: the executable next to the
    ''' server assembly is preferred, a ``dotnet ApiDocWorker.dll`` launch is the
    ''' fall back for an environment without the native app host.
    ''' </summary>
    ''' <returns></returns>
    Private Function startInfo() As ProcessStartInfo
        Dim custom As String = config.ApiDocWorkerPath
        Dim exe As String = Nothing
        Dim dll As String = Nothing

        If Not String.IsNullOrEmpty(custom) Then
            If custom.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) Then
                dll = custom
            Else
                exe = custom
            End If
        Else
            exe = Path.Combine(AppContext.BaseDirectory, "ApiDocWorker.exe")
            dll = Path.Combine(AppContext.BaseDirectory, "ApiDocWorker.dll")
        End If

        Dim psi As ProcessStartInfo
        Dim arguments As String

        ' the windows app host (*.exe) can not be executed on linux, while the
        ' build output is usually copied as is from a windows machine into the
        ' container: on every other platform the portable "dotnet ApiDocWorker.dll"
        ' launch is therefore preferred over the native app host.
        Dim preferExe As Boolean = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        Dim hasExe As Boolean = Not String.IsNullOrEmpty(exe) AndAlso File.Exists(exe)
        Dim hasDll As Boolean = Not String.IsNullOrEmpty(dll) AndAlso File.Exists(dll)

        If preferExe AndAlso hasExe Then
            psi = New ProcessStartInfo(exe)
            arguments = ""
        ElseIf hasDll Then
            psi = New ProcessStartInfo("dotnet")
            arguments = $"""{dll}"""
        ElseIf hasExe Then
            ' last resort: this is not the expected platform of that host, but a
            ' custom path could still point at a usable native executable
            psi = New ProcessStartInfo(exe)
            arguments = ""
        Else
            Dim message As String = "[apidoc-worker] the ApiDocWorker executable was not found next to the server, " &
                "the api documents are extracted inside the server process"

            Call message.warning()
            Return Nothing
        End If

        Dim parentPid As Integer = Process.GetCurrentProcess().Id

        arguments &= " --daemon" &
            $" --db ""{config.DatabaseDirectory}""" &
            $" --packages ""{config.PackageDirectory}""" &
            $" --wwwroot ""{config.Wwwroot}""" &
            $" --template ""{config.TemplateDirectory}""" &
            $" --docs-static ""{config.DocsStaticDirectory}""" &
            $" --reconcile-seconds {config.ApiDocWorkerReconcileSeconds}" &
            $" --max-attempts {config.ApiDocWorkerMaxAttempts}" &
            $" --parent-pid {parentPid}"

        If Not config.DocViewPixelEnabled Then
            arguments &= " --no-pixel"
        End If

        psi.Arguments = arguments
        psi.UseShellExecute = False
        psi.CreateNoWindow = True
        psi.RedirectStandardInput = True
        psi.RedirectStandardOutput = True
        psi.RedirectStandardError = True
        psi.StandardInputEncoding = New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False)
        psi.StandardOutputEncoding = New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False)
        psi.StandardErrorEncoding = New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False)

        Return psi
    End Function

    ''' <summary>
    ''' stop the worker: closing its stdin makes the daemon flush the pending
    ''' work and exit on its own; a process which ignores it is killed.
    ''' </summary>
    Public Sub [Stop]()
        Me.disposed = True
        Me.running = False

        Call signal.Set()

        Dim proc As Process = Me.process

        If proc Is Nothing Then
            Return
        End If

        Try
            proc.StandardInput.Close()
        Catch
        End Try

        Try
            If Not proc.WaitForExit(15000) Then
                Call $"[apidoc-worker] the worker did not stop in time, it is killed".warning()
                Call proc.Kill(entireProcessTree:=True)
            End If
        Catch ex As Exception
            Call $"[apidoc-worker] the worker could not be stopped: {ex.Message}".warning()
        End Try

        Call "[apidoc-worker] document daemon stopped".info()
    End Sub

#End Region

#Region "job queue"

    ''' <summary>
    ''' queue the extraction of one uploaded package. The method never blocks on
    ''' the worker and never throws: a failure is only a warning, the package
    ''' publication itself is never affected.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="nupkgPath"></param>
    ''' <returns>whether the job was accepted.</returns>
    Public Function EnqueueIndex(packageId As String, version As String, nupkgPath As String) As Boolean
        If Not Available Then
            Return False
        End If

        Return enqueue(New ApiDocWorkerJob With {
            .id = Guid.NewGuid().ToString("N"),
            .action = ApiDocWorkerJob.ActionIndex,
            .packageId = packageId,
            .version = version,
            .nupkg = nupkgPath,
            .attempt = 1
        })
    End Function

    Private Function enqueue(job As ApiDocWorkerJob) As Boolean
        If job Is Nothing OrElse disposed Then
            Return False
        End If

        SyncLock queueGate
            If Not queuedKeys.Add(jobKey(job)) Then
                ' an identical job is already waiting; the worker reads the
                ' package file when it starts the job, so the newest upload wins
                Return True
            End If

            Call queue.Enqueue(job)
        End SyncLock

        Call signal.Set()

        Return True
    End Function

    Private Shared Function jobKey(job As ApiDocWorkerJob) As String
        Return If(job.packageId, "").ToLowerInvariant() & "|" & If(job.version, "").ToLowerInvariant()
    End Function

    ''' <summary>
    ''' the writer thread: move the queued jobs into the stdin of the worker.
    ''' </summary>
    Private Sub writeJobs()
        Dim proc As Process = Me.process

        While Me.running AndAlso proc IsNot Nothing

            Dim alive As Boolean

            Try
                alive = Not proc.HasExited
            Catch
                alive = False
            End Try

            If Not alive Then
                Exit While
            End If

            If Not signal.WaitOne(500) Then
                Continue While
            End If

            Dim job As ApiDocWorkerJob = Nothing

            While queue.TryDequeue(job)
                SyncLock queueGate
                    Call queuedKeys.Remove(jobKey(job))
                End SyncLock

                SyncLock inflightGate
                    inflight(job.id) = job
                End SyncLock

                Try
                    proc.StandardInput.WriteLine(JsonSerializer.Serialize(job, JsonOptions))
                    proc.StandardInput.Flush()
                Catch ex As Exception
                    Call $"[apidoc-worker] the job {job.packageId} {job.version} could not be written: {ex.Message}".warning()

                    SyncLock inflightGate
                        Call inflight.Remove(job.id)
                    End SyncLock

                    Exit While
                End Try
            End While
        End While
    End Sub

#End Region

#Region "worker output"

    ''' <summary>
    ''' the reader thread: one json response per line.
    ''' </summary>
    Private Sub readResponses()
        Dim proc As Process = Me.process

        If proc Is Nothing Then
            Return
        End If

        Try
            While True
                Dim line As String = proc.StandardOutput.ReadLine()

                If line Is Nothing Then
                    Exit While
                End If

                If line.Trim().Length = 0 Then
                    Continue While
                End If

                Dim response As ApiDocWorkerResponse = Nothing

                Try
                    response = JsonSerializer.Deserialize(Of ApiDocWorkerResponse)(line, JsonOptions)
                Catch ex As Exception
                    Call $"[apidoc-worker] a malformed response line was ignored: {ex.Message}".warning()
                    Continue While
                End Try

                Call handleResponse(response)
            End While
        Catch ex As Exception
            Call $"[apidoc-worker] the response stream failed: {ex.Message}".warning()
        End Try
    End Sub

    ''' <summary>
    ''' the stderr pump: forward every log line of the worker into the server log.
    ''' </summary>
    Private Sub pumpErrors()
        Dim proc As Process = Me.process

        If proc Is Nothing Then
            Return
        End If

        Try
            While True
                Dim line As String = proc.StandardError.ReadLine()

                If line Is Nothing Then
                    Exit While
                End If

                ' keep the diagnostics of the worker visible without turning the
                ' server log into a debug dump
                If line.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Call $"[apidoc-worker] {line}".warning()
                Else
                    Call $"[apidoc-worker] {line}".debug()
                End If
            End While
        Catch
        End Try
    End Sub

    Private Sub handleResponse(response As ApiDocWorkerResponse)
        If response Is Nothing Then
            Return
        End If

        Dim job As ApiDocWorkerJob = Nothing

        SyncLock inflightGate
            Call inflight.TryGetValue(If(response.id, ""), job)
            Call inflight.Remove(If(response.id, ""))
        End SyncLock

        ' a valid answer proves that the worker is healthy again
        Me.restartDelaySeconds = 1

        Dim name As String = $"{job?.packageId} {job?.version}".Trim()

        If response.ok Then
            Call $"[apidoc-worker] {name}: {response.message} ({response.elapsedMs}ms)".debug()
        Else
            Call $"[apidoc-worker] {name}: {response.message}".warning()
        End If

        For Each message As String In If(response.warnings, New List(Of String))
            Call $"[apidoc-worker] {name}: {message}".warning()
        Next

        Dim handler As Action(Of ApiDocWorkerResponse) = Me.OnCompleted

        If handler IsNot Nothing Then
            Try
                Call handler(response)
            Catch ex As Exception
                Call App.LogException(ex)
            End Try
        End If
    End Sub

#End Region

#Region "crash recovery"

    ''' <summary>
    ''' the worker exited: re-queue the unanswered jobs and restart it with a
    ''' back off, unless the client is being disposed.
    ''' </summary>
    Private Sub onProcessExited(sender As Object, e As EventArgs)
        Me.running = False

        If Me.disposed Then
            Return
        End If

        Dim code As Integer = -1

        Try
            code = Me.process.ExitCode
        Catch
        End Try

        Dim lost As New List(Of ApiDocWorkerJob)

        SyncLock inflightGate
            lost.AddRange(Me.inflight.Values)
            Call Me.inflight.Clear()
        End SyncLock

        For Each job As ApiDocWorkerJob In lost
            Call enqueue(job)
        Next

        SyncLock restartGate
            If Me.restartScheduled Then
                Return
            End If

            Me.restartScheduled = True
        End SyncLock

        Dim delay As Integer = Math.Min(60, Math.Max(1, Me.restartDelaySeconds))
        Me.restartDelaySeconds = Math.Min(60, delay * 2)

        Dim requeued As String = If(lost.Count > 0, $", {lost.Count} job(s) re-queued", "")
        Dim restartMessage As String = $"[apidoc-worker] the worker exited (code={code}), restarting in {delay}s{requeued}"

        Call restartMessage.warning()

        Call ThreadPool.QueueUserWorkItem(
            Sub(item)
                Try
                    Call Thread.Sleep(TimeSpan.FromSeconds(delay))

                    If Not Me.disposed Then
                        Call Start()
                    End If
                Finally
                    ' when the start failed as well, the watchdog retries later
                    SyncLock restartGate
                        Me.restartScheduled = False
                    End SyncLock
                End Try
            End Sub)
    End Sub

#End Region

    Private Function pidText() As String
        Try
            Return $"pid={Me.process.Id}"
        Catch
            Return Nothing
        End Try
    End Function
End Class

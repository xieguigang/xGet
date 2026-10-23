Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports JSql.Storage
Imports Nuget
Imports Readership

''' <summary>
''' the settings of the api document daemon, parsed from the command line by
''' <see cref="Program"/>.
''' </summary>
Public Class WorkerDaemonOptions

    ''' <summary>the physical jsql database folder (the ``--db`` argument of the server).</summary>
    Public Property database As String

    ''' <summary>the physical package folder (``{data}/packages``).</summary>
    Public Property packages As String

    ''' <summary>the physical web root (``--wwwroot``).</summary>
    Public Property wwwroot As String

    ''' <summary>the replaceable html template folder.</summary>
    Public Property template As String

    ''' <summary>
    ''' the explicit output root of the generated pages; empty means
    ''' ``{wwwroot}/docs``.
    ''' </summary>
    Public Property docsStatic As String

    ''' <summary>whether the generated pages carry the 1x1 view counter pixel.</summary>
    Public Property docPixel As Boolean = True

    ''' <summary>the process id of the server, the daemon exits when it disappears.</summary>
    Public Property parentPid As Integer

    ''' <summary>the interval in seconds of the reconciliation pass.</summary>
    Public Property reconcileSeconds As Integer = 300

    ''' <summary>the maximum number of the attempts of one job.</summary>
    Public Property maxAttempts As Integer = 3
End Class

''' <summary>
''' the long living api document worker of the nuget server.
''' 
''' the daemon consumes one json job per line from its stdin and answers with one
''' json response per line on the stdout it received at the startup (every other
''' console output is redirected to stderr, so that the log lines of the server
''' libraries can never corrupt the protocol).
''' 
''' every job is executed on a single consumer thread: the extraction of the xml
''' comment documents, the reflection supplement, the database write and the
''' html rendering of one package happen one after another, which keeps the cpu
''' peak low on a small server.
''' </summary>
Public Class WorkerDaemon

    ''' <summary>the minimum interval between two global index rebuilds.</summary>
    Private Const GlobalIndexDebounceSeconds As Integer = 5

    Private ReadOnly options As WorkerDaemonOptions
    Private ReadOnly config As NugetConfiguration
    Private ReadOnly store As NugetStore
    Private ReadOnly protocol As TextWriter

    Private ReadOnly queue As New BlockingCollection(Of ApiDocWorkerJob)

    ''' <summary>the ids of the jobs which are queued or running, for the de-duplication.</summary>
    Private ReadOnly pending As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>serializes the job execution and the reconciliation (both write the same database).</summary>
    Private ReadOnly gate As New Object

    ''' <summary>serializes the writes of the protocol lines.</summary>
    Private ReadOnly protocolGate As New Object

    ''' <summary>the state of the debounced global index rebuild.</summary>
    Private ReadOnly globalIndexGate As New Object
    Private globalIndexDirty As Boolean
    Private lastGlobalIndex As Date = Date.MinValue

    Private consumer As Thread
    Private reconcileTimer As Threading.Timer
    Private indexTimer As Threading.Timer
    Private parentTimer As Threading.Timer

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    Public Sub New(options As WorkerDaemonOptions, protocol As TextWriter)
        Me.options = options
        Me.protocol = protocol
        Me.config = configuration(options)
        Me.store = New NugetStore(config.DatabaseDirectory, storageOptions())
    End Sub

    ''' <summary>
    ''' build the server configuration view of the worker from the command line
    ''' arguments, so that the static page layout (template folder, output folder,
    ''' counter pixel) is identical to the server side renderer.
    ''' </summary>
    Private Shared Function configuration(options As WorkerDaemonOptions) As NugetConfiguration
        Dim values As New Dictionary(Of String, String) From {
            {"db", options.database},
            {"packages", options.packages},
            {"wwwroot", options.wwwroot},
            {"template", options.template},
            {"apidoc-worker-enabled", "true"},
            {"sitemap-enabled", "false"},
            {"cluster-enabled", "false"},
            {"doc-view-pixel", If(options.docPixel, "true", "false")}
        }

        If Not String.IsNullOrEmpty(options.docsStatic) Then
            values("docs-static-output") = options.docsStatic
        End If

        Return NugetConfiguration.FromConfig(values)
    End Function

    ''' <summary>
    ''' the storage options of the worker: the database is shared with the running
    ''' server, so the table level file lock has to be released after every
    ''' statement and a conflicting statement has to wait for its turn.
    ''' </summary>
    Private Shared Function storageOptions() As StorageOptions
        Return New StorageOptions With {
            .MergeIdleSeconds = 30,
            .MergeAfterOperations = 2000,
            .FsyncEachWrite = False,
            .MultiProcessAccess = True,
            .LockConflictPolicy = LockConflictPolicy.Wait,
            .LockWaitTimeoutMs = 15000,
            .LockRetryIntervalMs = 100
        }
    End Function

#Region "main loop"

    ''' <summary>
    ''' run the daemon until the stdin of the process is closed (the server exited
    ''' or closed the pipe) or the parent process disappeared.
    ''' </summary>
    ''' <returns>the process exit code.</returns>
    Public Function Run() As Integer
        Call "api document worker started".info()
        Call $"  db       : {config.DatabaseDirectory}".info()
        Call $"  docs     : {config.DocsStaticDirectory}".info()
        Call $"  template : {config.TemplateDirectory}".info()
        Call $"  reconcile: every {options.reconcileSeconds}s, max attempts={options.maxAttempts}".info()

        Me.consumer = New Thread(AddressOf consume)
        Me.consumer.IsBackground = True
        Me.consumer.Name = "apidoc-jobs"
        Me.consumer.Start()

        Me.indexTimer = New Threading.Timer(AddressOf indexTick, Nothing,
            dueTime:=TimeSpan.FromSeconds(GlobalIndexDebounceSeconds),
            period:=TimeSpan.FromSeconds(GlobalIndexDebounceSeconds))

        ' the first reconciliation is delayed so that it does not compete with
        ' the server startup, then it repeats on the configured interval.
        Me.reconcileTimer = New Threading.Timer(AddressOf reconcileTick, Nothing,
            dueTime:=TimeSpan.FromSeconds(10),
            period:=TimeSpan.FromSeconds(options.reconcileSeconds))

        If options.parentPid > 0 Then
            Me.parentTimer = New Threading.Timer(AddressOf parentTick, Nothing,
                dueTime:=TimeSpan.FromSeconds(30),
                period:=TimeSpan.FromSeconds(30))
        End If

        Call readJobs()

        Return shutdown()
    End Function

    ''' <summary>
    ''' read the job lines from the stdin until the pipe is closed.
    ''' </summary>
    Private Sub readJobs()
        While True
            Dim line As String

            Try
                line = Console.In.ReadLine()
            Catch ex As Exception
                Call $"the job stream could not be read: {ex.Message}".warning()
                Exit While
            End Try

            If line Is Nothing Then
                ' the server closed the pipe: it is shutting down (or it was
                ' killed), so the daemon stops as well.
                Call "the job stream was closed".info()
                Exit While
            End If

            ' tolerate a byte order mark which some shells prepend when a text
            ' line is piped into a native process
            line = line.TrimStart(ChrW(&HFEFF))

            If line.Trim().Length = 0 Then
                Continue While
            End If

            Dim job As ApiDocWorkerJob = Nothing

            Try
                job = JsonSerializer.Deserialize(Of ApiDocWorkerJob)(line, JsonOptions)
            Catch ex As Exception
                Call $"the job line could not be decoded: {ex.Message}".warning()
                Continue While
            End Try

            If job Is Nothing OrElse String.IsNullOrEmpty(job.packageId) OrElse String.IsNullOrEmpty(job.version) Then
                Call "a job line without a package id / version was ignored".warning()
                Continue While
            End If

            If String.IsNullOrEmpty(job.action) Then
                job.action = ApiDocWorkerJob.ActionIndex
            End If

            If job.attempt <= 0 Then
                job.attempt = 1
            End If

            Call queue.Add(job)
        End While
    End Sub

    ''' <summary>
    ''' stop the consumer, flush the pending global index and answer the exit code.
    ''' </summary>
    Private Function shutdown() As Integer
        Call queue.CompleteAdding()

        Try
            Call consumer.Join(TimeSpan.FromSeconds(60))
        Catch ex As Exception
            Call $"the consumer did not stop cleanly: {ex.Message}".warning()
        End Try

        Try
            Call flushGlobalIndex()
        Catch ex As Exception
            Call $"the global document index could not be flushed: {ex.Message}".warning()
        End Try

        Call "api document worker stopped".info()
        Return 0
    End Function

#End Region

#Region "job execution"

    ''' <summary>
    ''' the consumer: one job at a time, so that the cpu load stays flat.
    ''' </summary>
    Private Sub consume()
        Try
            For Each job As ApiDocWorkerJob In queue.GetConsumingEnumerable()
                Dim response As ApiDocWorkerResponse = Nothing
                Dim attempt As Integer = job.attempt

                While True
                    Try
                        response = execute(job)
                    Catch ex As Exception
                        response = New ApiDocWorkerResponse With {
                            .id = job.id,
                            .ok = False,
                            .message = ex.Message
                        }
                    End Try

                    If response.ok OrElse attempt >= options.maxAttempts Then
                        Exit While
                    End If

                    attempt += 1

                    Dim retryMessage As String = $"job {job.packageId} {job.version} failed " &
                        $"(attempt {attempt - 1}/{options.maxAttempts}), retrying in {attempt}s: {response.message}"

                    Call retryMessage.warning()

                    ' the retry is inline: the queue is processed strictly one
                    ' job at a time anyway, and a short pause gives the server
                    ' process the chance to finish its own database writes.
                    Call Thread.Sleep(TimeSpan.FromSeconds(attempt))
                End While

                Call untrack(job)
                Call respond(response)
            Next
        Catch ex As Exception
            Call App.LogException(ex)
        End Try
    End Sub

    ''' <summary>
    ''' execute one job: either an ``index`` job (extract the package and rebuild
    ''' its pages) or a ``rebuild`` job (render the pages from the stored rows).
    ''' </summary>
    Private Function execute(job As ApiDocWorkerJob) As ApiDocWorkerResponse
        Dim watch As Stopwatch = Stopwatch.StartNew()
        Dim response As New ApiDocWorkerResponse With {.id = job.id, .ok = True}
        Dim warnings As New List(Of String)
        Dim rows As List(Of PackageApiDocRecord)

        SyncLock gate
            If String.Equals(job.action, ApiDocWorkerJob.ActionRebuild, StringComparison.OrdinalIgnoreCase) Then
                ' the reconciliation job: the document rows are already stored
                rows = store.ReadPackageApiDocs(job.packageId, job.version)

                If rows.Count = 0 Then
                    Call DocPageCache.RemoveVersion(config, job.packageId, job.version)
                    Call markGlobalIndexDirty()

                    response.message = "no document row was found, the stale pages were removed"
                    response.elapsedMs = watch.ElapsedMilliseconds
                    Return response
                End If
            Else
                If String.IsNullOrEmpty(job.nupkg) OrElse Not File.Exists(job.nupkg) Then
                    Throw New FileNotFoundException($"the package file was not found: {job.nupkg}")
                End If

                Dim document As ApiDocDocument = PackageApiDocs.ExtractInProcess(job.nupkg, warnings)

                If document Is Nothing Then
                    Throw New InvalidOperationException("the api comment document extraction failed")
                End If

                rows = PackageApiDocs.ToRecords(document, job.packageId, job.version)

                ' an empty list is a valid state: the package ships no xml
                ' comment document, so any previously stored rows are dropped.
                Call store.ReplacePackageApiDocs(job.packageId, job.version, rows)

                response.documents = True
            End If

            response.types = rows.Count

            If rows.Count > 0 Then
                Dim versions As List(Of String) = store.GetPackageApiDocVersions(job.packageId)

                response.pages = DocPageCache.WritePackageVersion(
                    config, job.packageId, job.version, rows, versions)
            Else
                Call DocPageCache.RemoveVersion(config, job.packageId, job.version)
            End If

            Call markGlobalIndexDirty()
        End SyncLock

        If warnings.Count > 0 Then
            Call response.warnings.AddRange(warnings)
        End If

        response.elapsedMs = watch.ElapsedMilliseconds
        response.message = $"{response.types} type(s), {response.pages} page(s)"

        Return response
    End Function

    ''' <summary>
    ''' write one response line on the original stdout of the process.
    ''' </summary>
    ''' <param name="response"></param>
    Private Sub respond(response As ApiDocWorkerResponse)
        If response Is Nothing Then
            Return
        End If

        Dim json As String = JsonSerializer.Serialize(response, JsonOptions)

        SyncLock protocolGate
            Try
                protocol.WriteLine(json)
                protocol.Flush()
            Catch ex As Exception
                Call $"the response could not be written to the server: {ex.Message}".warning()
            End Try
        End SyncLock
    End Sub

#End Region

#Region "reconciliation"

    ''' <summary>
    ''' the periodic reconciliation: rebuild the pages which are missing or
    ''' incomplete, and remove the pages of the hidden packages and of the
    ''' versions which no longer exist in the database.
    ''' </summary>
    Public Sub reconcile()
        Dim rows As List(Of PackageApiDocRecord)
        Dim hidden As HashSet(Of String)
        Dim cached As List(Of DocCacheEntry)

        SyncLock gate
            rows = store.ReadApiDocIndex()
            hidden = store.GetHiddenPackageIds()
            cached = DocPageCache.EnumerateCache(config)
        End SyncLock

        Dim groups As Dictionary(Of String, DocGroup) = groupDocuments(rows)
        Dim removed As Integer = 0
        Dim queued As Integer = 0

        ' 1. the hidden packages must not stay reachable as static files
        For Each packageId As String In hidden
            Dim count As Integer = DocPageCache.RemovePackage(config, packageId)

            If count > 0 Then
                removed += count
                Call $"the static document pages of the hidden package '{packageId}' were removed ({count} version(s))".info()
            End If
        Next

        ' 2. the cache folders of the versions which are not in the database
        For Each entry As DocCacheEntry In cached
            If hidden.Contains(entry.package_id) Then
                Continue For
            End If

            If Not groups.ContainsKey(groupKey(entry.package_id, entry.version)) Then
                If DocPageCache.RemoveVersion(config, entry.package_id, entry.version) Then
                    removed += 1
                    Call $"the stale document pages of '{entry.package_id} {entry.version}' were removed".debug()
                End If
            End If
        Next

        ' 3. the versions which are stored but not (completely) cached
        For Each group As DocGroup In groups.Values
            If hidden.Contains(group.package_id) Then
                Continue For
            End If

            If DocPageCache.IsUpToDate(config, group.package_id, group.version, group.types) Then
                Continue For
            End If

            If enqueueRebuild(group) Then
                queued += 1
            End If
        Next

        If queued > 0 OrElse removed > 0 Then
            Call $"document cache reconciled: {queued} version(s) queued, {removed} folder(s) removed, {groups.Count} version(s) in total".info()
        Else
            Call $"document cache is up to date: {groups.Count} version(s)".debug()
        End If
    End Sub

    Private Function enqueueRebuild(group As DocGroup) As Boolean
        Dim key As String = groupKey(group.package_id, group.version)

        SyncLock gate
            If pending.Contains(key) Then
                Return False
            End If

            Call pending.Add(key)
        End SyncLock

        Try
            Call queue.Add(New ApiDocWorkerJob With {
                .id = Guid.NewGuid().ToString("N"),
                .action = ApiDocWorkerJob.ActionRebuild,
                .packageId = group.package_id,
                .version = group.version,
                .attempt = 1
            })

            Call $"the static document pages of '{group.package_id} {group.version}' are missing, a rebuild job was queued".debug()
            Return True
        Catch ex As Exception
            Call untrack(group.package_id, group.version)
            Call $"the rebuild job of '{group.package_id} {group.version}' could not be queued: {ex.Message}".warning()
            Return False
        End Try
    End Function

    Private Function groupDocuments(rows As IEnumerable(Of PackageApiDocRecord)) As Dictionary(Of String, DocGroup)
        Dim groups As New Dictionary(Of String, DocGroup)(StringComparer.OrdinalIgnoreCase)

        For Each row As PackageApiDocRecord In If(rows, Enumerable.Empty(Of PackageApiDocRecord)())
            If row Is Nothing OrElse String.IsNullOrEmpty(row.package_id) OrElse String.IsNullOrEmpty(row.version) Then
                Continue For
            End If

            Dim key As String = groupKey(row.package_id, row.version)
            Dim group As DocGroup = Nothing

            If Not groups.TryGetValue(key, group) Then
                group = New DocGroup With {
                    .package_id = row.package_id,
                    .version = row.version
                }

                groups(key) = group
            End If

            If Not String.IsNullOrEmpty(row.type_fullname) Then
                Call group.types.Add(row.type_fullname)
            End If
        Next

        Return groups
    End Function

    Private Shared Function groupKey(packageId As String, version As String) As String
        Return If(packageId, "").ToLowerInvariant() & "|" & If(version, "").ToLowerInvariant()
    End Function

    Private Sub untrack(job As ApiDocWorkerJob)
        Call untrack(job.packageId, job.version)
    End Sub

    Private Sub untrack(packageId As String, version As String)
        SyncLock gate
            Call pending.Remove(groupKey(packageId, version))
        End SyncLock
    End Sub

    Private Sub reconcileTick(state As Object)
        Try
            Call reconcile()
        Catch ex As Exception
            Call $"the document cache reconciliation failed: {ex.Message}".warning()
            Call App.LogException(ex)
        End Try
    End Sub

#End Region

#Region "global index"

    Private Sub markGlobalIndexDirty()
        SyncLock globalIndexGate
            globalIndexDirty = True
        End SyncLock
    End Sub

    ''' <summary>
    ''' rebuild the cross package document index page when at least one package
    ''' changed and the debounce window has passed; the multiple jobs of a batch
    ''' upload are therefore merged into a single rebuild.
    ''' </summary>
    Private Sub flushGlobalIndex(Optional force As Boolean = False)
        SyncLock globalIndexGate
            If Not globalIndexDirty Then
                Return
            End If

            If Not force AndAlso (Date.UtcNow - lastGlobalIndex).TotalSeconds < GlobalIndexDebounceSeconds Then
                Return
            End If

            Dim rows As List(Of PackageApiDocRecord)

            SyncLock gate
                rows = store.ReadApiDocIndex()
            End SyncLock

            If DocPageCache.WriteGlobalIndex(config, rows) Then
                Call "the global document index page was rebuilt".debug()
            End If

            lastGlobalIndex = Date.UtcNow
            globalIndexDirty = False
        End SyncLock
    End Sub

    Private Sub indexTick(state As Object)
        Try
            Call flushGlobalIndex()
        Catch ex As Exception
            Call $"the global document index could not be rebuilt: {ex.Message}".warning()
        End Try
    End Sub

#End Region

#Region "parent watchdog"

    ''' <summary>
    ''' exit when the server process disappeared: the stdin pipe normally closes
    ''' on its own, this watchdog also covers a killed parent on windows.
    ''' </summary>
    Private Sub parentTick(state As Object)
        Try
            Dim parent As Process = Process.GetProcessById(options.parentPid)

            If parent.HasExited Then
                Throw New InvalidOperationException("the parent process has exited")
            End If
        Catch ex As Exception
            Call $"the parent process {options.parentPid} is gone, the worker stops: {ex.Message}".warning()
            Call Environment.Exit(0)
        End Try
    End Sub

#End Region

    ''' <summary>
    ''' one package version of the reconciliation: the stored type full names.
    ''' </summary>
    Private Class DocGroup
        Public Property package_id As String
        Public Property version As String
        Public Property types As New List(Of String)
    End Class
End Class

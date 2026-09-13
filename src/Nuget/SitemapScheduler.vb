Imports System.IO
Imports System.Threading

''' <summary>
''' the schedule of the site map generation: the site map is rebuilt only when
''' the api document database has changed **and** the server is idle.
''' </summary>
''' <remarks>
''' the http router of fluteway has no request middleware which could be hooked
''' by an application module, so the activity clock is fed by the controller
''' itself: every response which is written by a route handler of this module
''' calls <see cref="TouchRequest"/>. the static files of the ``wwwroot`` folder
''' are served by the file system listener **before** the clr routes are
''' consulted, hence they are not part of the activity clock and the idle test
''' is an approximation based on the controller traffic only.
''' </remarks>
Public Class SitemapScheduler

    ''' <summary>
    ''' the ticks of the last controller request, shared by all scheduler
    ''' instances; it is exchanged atomically so that the response path never
    ''' blocks on a lock.
    ''' </summary>
    Private Shared lastRequestTicks As Long = Date.UtcNow.Ticks

    Private ReadOnly config As NugetConfiguration
    Private ReadOnly store As NugetStore

    ''' <summary>
    ''' the lock of the mutable schedule state (the dirty flag, the recorded
    ''' fingerprint and the diagnostics).
    ''' </summary>
    Private ReadOnly state As New Object

    ''' <summary>
    ''' the lock which serializes the generation itself; it is held for the whole
    ''' build so that two background / on demand generations can never race.
    ''' </summary>
    Private ReadOnly gate As New Object

    ''' <summary>
    ''' the timer which periodically tests whether the site map should be
    ''' rebuilt. the instance is kept in a field because an unreferenced timer
    ''' would be garbage collected and the periodic task would silently stop.
    ''' </summary>
    Private timer As Threading.Timer

    ''' <summary>
    ''' whether the document database changed since the latest generation.
    ''' </summary>
    Private dirty As Boolean = True

    ''' <summary>
    ''' the fingerprint recorded by the latest generation (or loaded from the
    ''' state file at the startup).
    ''' </summary>
    Private lastFingerprint As String

    ''' <summary>
    ''' the timestamp and the url count of the latest generation, for diagnostics.
    ''' </summary>
    Private generatedAt As Date? = Nothing
    Private urlCount As Integer = 0

    Public Sub New(config As NugetConfiguration, store As NugetStore)
        Me.config = config
        Me.store = store
        Me.lastFingerprint = SitemapGenerator.ReadRecordedFingerprint(config)
    End Sub

#Region "activity clock"

    ''' <summary>
    ''' record that a controller request was served right now. the method is
    ''' static because it is called from the response helpers of the controller.
    ''' </summary>
    Public Shared Sub TouchRequest()
        Call Interlocked.Exchange(lastRequestTicks, Date.UtcNow.Ticks)
    End Sub

    ''' <summary>
    ''' the timestamp of the latest controller request.
    ''' </summary>
    ''' <returns></returns>
    Public Shared ReadOnly Property LastRequestUtc As Date
        Get
            Return New Date(Interlocked.Read(lastRequestTicks), DateTimeKind.Utc)
        End Get
    End Property

    ''' <summary>
    ''' whether the server has been free of controller requests for at least the
    ''' configured idle seconds.
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property IsIdle As Boolean
        Get
            Return Date.UtcNow - LastRequestUtc >= TimeSpan.FromSeconds(config.SitemapIdleSeconds)
        End Get
    End Property

#End Region

#Region "schedule"

    ''' <summary>
    ''' mark the api document database as changed; called by the upload flow
    ''' after the documents were stored (or removed) so that the site map is
    ''' rebuilt on the next idle test.
    ''' </summary>
    Public Sub NotifyDocsChanged()
        SyncLock state
            dirty = True
        End SyncLock
    End Sub

    ''' <summary>
    ''' start the periodic idle test. it is a no op when the site map feature is
    ''' disabled, or when no public base url is configured and the links could
    ''' therefore not be generated.
    ''' </summary>
    Public Sub Start()
        If Not config.SitemapEnabled Then
            Call "the sitemap generation is disabled by the configuration".info()
            Return
        End If

        If String.IsNullOrEmpty(config.ResolveSitemapBaseUrl()) Then
            Call "the sitemap generation is disabled: neither 'sitemap-base-url' nor 'base-url' is configured".warning()
            Return
        End If

        Me.timer = New Threading.Timer(
            AddressOf tick,
            Nothing,
            dueTime:=TimeSpan.FromSeconds(config.SitemapIntervalSeconds),
            period:=TimeSpan.FromSeconds(config.SitemapIntervalSeconds))

        Call $"sitemap generation scheduled: every {config.SitemapIntervalSeconds} second(s), idle after {config.SitemapIdleSeconds}s, base url={config.ResolveSitemapBaseUrl()}".info()
        Call $"sitemap output directory: {config.TempDirectory}".info()
    End Sub

    ''' <summary>
    ''' the periodic callback: rebuild the site map when the document database
    ''' changed and the server is idle. every exception is swallowed (and logged)
    ''' so that the background task can never take the server down.
    ''' </summary>
    Private Sub tick(timerState As Object)
        Try
            Dim reason As String = ""

            If Not shouldGenerate(reason) Then
                Call $"sitemap generation skipped: {reason}".debug()
                Return
            End If

            SyncLock gate
                Dim build As SitemapBuild = SitemapGenerator.Generate(config, store, Date.UtcNow)

                Call markGenerated(build)

                Call $"sitemap generated: {build.urlCount} url(s), {build.docCount} api document url(s)".info()
            End SyncLock
        Catch ex As Exception
            Call $"the sitemap generation failed: {ex.Message}".warning()
            Call App.LogException(ex)
        End Try
    End Sub

    ''' <summary>
    ''' test whether the site map has to be rebuilt right now: the feature has to
    ''' be enabled, the server has to be idle and the document database (or the
    ''' package feed) has to have changed since the latest generation.
    ''' </summary>
    ''' <param name="reason">the reason of the negative answer, for the log.</param>
    Private Function shouldGenerate(ByRef reason As String) As Boolean
        If Not config.SitemapEnabled Then
            reason = "the feature is disabled"
            Return False
        End If

        If String.IsNullOrEmpty(config.ResolveSitemapBaseUrl()) Then
            reason = "no public base url was configured"
            Return False
        End If

        If Not IsIdle Then
            reason = $"the server is busy (idle for {(Date.UtcNow - LastRequestUtc).TotalSeconds.ToString("F0")}s of {config.SitemapIdleSeconds}s)"
            Return False
        End If

        Dim changed As Boolean

        SyncLock state
            changed = dirty
        End SyncLock

        If Not File.Exists(SitemapGenerator.SitemapFilePath(config)) Then
            reason = "the site map file is missing"
            Return True
        End If

        If changed Then
            reason = "the document database was updated"
            Return True
        End If

        ' the dirty flag is lost when the server restarts, so the database
        ' fingerprint is compared as well; it also detects a change which was
        ' not reported through NotifyDocsChanged.
        Dim current As String = SitemapGenerator.Fingerprint(store)
        Dim recorded As String

        SyncLock state
            recorded = lastFingerprint
        End SyncLock

        If current <> recorded Then
            reason = $"the document database fingerprint changed ({current})"
            Return True
        End If

        reason = "the document database is unchanged"
        Return False
    End Function

    ''' <summary>
    ''' remember the fingerprint of a generation and clear the dirty flag.
    ''' </summary>
    ''' <param name="build"></param>
    Private Sub markGenerated(build As SitemapBuild)
        SyncLock state
            dirty = False
            lastFingerprint = build.fingerprint
            generatedAt = Date.UtcNow
            urlCount = build.urlCount
        End SyncLock
    End Sub

#End Region

#Region "on demand access"

    ''' <summary>
    ''' the generated site map text. when the file is missing (for example
    ''' because the server did not stay idle long enough, or because the scratch
    ''' directory was cleared) it is built synchronously once, so that a search
    ''' engine crawling the site never sees a 404.
    ''' </summary>
    ''' <returns>the xml text, or nothing when it can not be produced.</returns>
    Public Function TryReadXml() As String
        Dim xml As String = SitemapGenerator.ReadXml(config)

        If Not String.IsNullOrEmpty(xml) Then
            Return xml
        End If

        If String.IsNullOrEmpty(config.ResolveSitemapBaseUrl()) Then
            Return Nothing
        End If

        If Not Monitor.TryEnter(gate) Then
            ' a background generation is in flight: give it a moment, then read
            ' the file it is about to publish.
            Call Thread.Sleep(500)
            Return SitemapGenerator.ReadXml(config)
        End If

        Try
            Dim build As SitemapBuild = SitemapGenerator.Generate(config, store, Date.UtcNow)

            Call markGenerated(build)
            Call $"sitemap generated on demand: {build.urlCount} url(s)".debug()

            Return build.xml
        Catch ex As Exception
            Call $"the on demand sitemap generation failed: {ex.Message}".warning()
            Return Nothing
        Finally
            Call Monitor.Exit(gate)
        End Try
    End Function

    ''' <summary>
    ''' the diagnostics of the latest generation: the url count and the
    ''' timestamp, used by the log of the controller.
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property LastGeneratedUtc As Date?
        Get
            SyncLock state
                Return generatedAt
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property LastUrlCount As Integer
        Get
            SyncLock state
                Return urlCount
            End SyncLock
        End Get
    End Property

#End Region
End Class

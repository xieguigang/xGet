Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports JSql.Storage

''' <summary>
''' runtime configuration of the experimental nuget server module.
''' </summary>
''' <remarks>
''' the data directory is intentionally configurable because the server is
''' usually hosted inside a linux docker container where the program directory
''' is read only. use the ``--data`` command line argument of the Fluteway
''' ``/run`` command, or a key of the ``--config`` ini file, to point it to a
''' writable volume.
''' </remarks>
Public Class NugetConfiguration

    ''' <summary>
    ''' the root data directory that holds both the JSql database and the
    ''' physical package files.
    ''' </summary>
    Public ReadOnly Property DataDirectory As String

    ''' <summary>
    ''' the directory holding the physical ``.nupkg``/``.nuspec`` files.
    ''' </summary>
    Public ReadOnly Property PackageDirectory As String

    ''' <summary>
    ''' the JSql database root directory.
    ''' </summary>
    Public ReadOnly Property DatabaseDirectory As String

    ''' <summary>
    ''' the physical static web root directory.
    ''' </summary>
    Public ReadOnly Property Wwwroot As String

    ''' <summary>
    ''' the directory that holds the replaceable html template files which are
    ''' used by the server side rendered api document pages. configuration key
    ''' ``template``, default is the ``template`` folder next to the ``wwwroot``
    ''' folder.
    ''' </summary>
    Public ReadOnly Property TemplateDirectory As String

    ''' <summary>
    ''' an optional public base url that overrides the request host, for example
    ''' ``http://pkg.example.com`` when the server runs behind a reverse proxy.
    ''' </summary>
    Public ReadOnly Property BaseUrl As String

    ''' <summary>
    ''' the scratch directory of the server: the generated ``sitemap.xml`` is
    ''' written into it and then served through the ``/sitemap.xml`` route.
    ''' configuration key ``tmp``, default is the ``tmp`` folder next to the
    ''' ``wwwroot`` folder.
    ''' </summary>
    Public ReadOnly Property TempDirectory As String

    ''' <summary>
    ''' whether the periodic sitemap generation is enabled. configuration key
    ''' ``sitemap-enabled``, default True.
    ''' </summary>
    Public ReadOnly Property SitemapEnabled As Boolean

    ''' <summary>
    ''' an optional public base url used by the sitemap links only. it is
    ''' required because the sitemap is built in the background where no http
    ''' request is available to infer the host. configuration key
    ''' ``sitemap-base-url``; when it is empty the ``base-url`` value is used
    ''' instead, and when both are empty the generation is skipped with a
    ''' warning.
    ''' </summary>
    Public ReadOnly Property SitemapBaseUrl As String

    ''' <summary>
    ''' the server has to stay without any controller request for at least this
    ''' many seconds before the sitemap is regenerated. configuration key
    ''' ``sitemap-idle-seconds``, default 60.
    ''' </summary>
    Public ReadOnly Property SitemapIdleSeconds As Integer

    ''' <summary>
    ''' the interval in seconds of the sitemap schedule which tests whether the
    ''' document database has changed and the server is idle. configuration key
    ''' ``sitemap-interval-seconds``, default 300.
    ''' </summary>
    Public ReadOnly Property SitemapIntervalSeconds As Integer

    ''' <summary>
    ''' whether the periodic package clustering analysis (tag matrix -> umap ->
    ''' kmeans) is enabled. configuration key ``cluster-enabled``.
    ''' </summary>
    Public ReadOnly Property ClusterEnabled As Boolean

    ''' <summary>
    ''' the number of kmeans clusters requested by the user. configuration key
    ''' ``cluster-k``, default 6.
    ''' </summary>
    Public ReadOnly Property ClusterK As Integer

    ''' <summary>
    ''' the interval in minutes of the periodic clustering analysis.
    ''' configuration key ``cluster-interval``, default 30.
    ''' </summary>
    Public ReadOnly Property ClusterIntervalMinutes As Integer

    ''' <summary>
    ''' the minimum number of tagged packages required to run the analysis.
    ''' configuration key ``cluster-min-samples``, default 3.
    ''' </summary>
    Public ReadOnly Property ClusterMinSamples As Integer

    ''' <summary>
    ''' the umap number of neighbors. configuration key ``cluster-neighbors``,
    ''' default 15.
    ''' </summary>
    Public ReadOnly Property ClusterNeighbors As Integer

    ''' <summary>
    ''' how many seconds the JSql engine has to stay idle before its background
    ''' checkpoint merges the pending write ahead log into the data files.
    ''' configuration key ``db-merge-idle-seconds``, default 30.
    ''' </summary>
    Public ReadOnly Property DbMergeIdleSeconds As Integer

    ''' <summary>
    ''' the number of the pending write ahead log operations which force a merge
    ''' even when the engine is not idle. configuration key
    ''' ``db-merge-operations``, default 2000.
    ''' </summary>
    Public ReadOnly Property DbMergeOperations As Integer

    ''' <summary>
    ''' the interval in seconds of the explicit ``CHECKPOINT`` job which merges
    ''' the write ahead logs as a fall back of the background checkpoint.
    ''' configuration key ``db-checkpoint-seconds``, default 300.
    ''' </summary>
    Public ReadOnly Property DbCheckpointSeconds As Integer

    Public Const DefaultClusterK As Integer = 6
    Public Const DefaultClusterIntervalMinutes As Integer = 30
    Public Const DefaultClusterMinSamples As Integer = 3
    Public Const DefaultClusterNeighbors As Integer = 15

    Public Const DefaultDbMergeIdleSeconds As Integer = 30
    Public Const DefaultDbMergeOperations As Integer = 2000
    Public Const DefaultDbCheckpointSeconds As Integer = 300

    Public Const DefaultSitemapIdleSeconds As Integer = 60
    Public Const DefaultSitemapIntervalSeconds As Integer = 300

    Private Sub New(data As String, packages As String, database As String, wwwroot As String, template As String, baseUrl As String,
                    tmp As String, sitemapEnabled As Boolean, sitemapBaseUrl As String,
                    sitemapIdleSeconds As Integer, sitemapIntervalSeconds As Integer,
                    clusterEnabled As Boolean, clusterK As Integer, clusterIntervalMinutes As Integer,
                    clusterMinSamples As Integer, clusterNeighbors As Integer,
                    dbMergeIdleSeconds As Integer, dbMergeOperations As Integer, dbCheckpointSeconds As Integer)

        Me.DataDirectory = data
        Me.PackageDirectory = packages
        Me.DatabaseDirectory = database
        Me.Wwwroot = wwwroot
        Me.TemplateDirectory = template
        Me.BaseUrl = baseUrl
        Me.TempDirectory = tmp
        Me.SitemapEnabled = sitemapEnabled
        Me.SitemapBaseUrl = sitemapBaseUrl
        Me.SitemapIdleSeconds = sitemapIdleSeconds
        Me.SitemapIntervalSeconds = sitemapIntervalSeconds
        Me.ClusterEnabled = clusterEnabled
        Me.ClusterK = clusterK
        Me.ClusterIntervalMinutes = clusterIntervalMinutes
        Me.ClusterMinSamples = clusterMinSamples
        Me.ClusterNeighbors = clusterNeighbors
        Me.DbMergeIdleSeconds = dbMergeIdleSeconds
        Me.DbMergeOperations = dbMergeOperations
        Me.DbCheckpointSeconds = dbCheckpointSeconds
    End Sub

    ''' <summary>
    ''' the effective public base url of the sitemap links: the dedicated
    ''' ``sitemap-base-url`` wins, then the generic ``base-url``, and an empty
    ''' string means that the sitemap can not be generated because no public
    ''' host is known.
    ''' </summary>
    ''' <returns>the base url without a trailing slash, or an empty string.</returns>
    Public Function ResolveSitemapBaseUrl() As String
        Dim url As String = If(String.IsNullOrWhiteSpace(SitemapBaseUrl), BaseUrl, SitemapBaseUrl)

        If String.IsNullOrWhiteSpace(url) Then
            Return ""
        End If

        Return url.Trim().TrimEnd("/"c)
    End Function

    ''' <summary>
    ''' build the JSql storage options of this server instance, so that the
    ''' checkpoint behaviour of the write ahead log could be tuned without a
    ''' rebuild.
    ''' </summary>
    ''' <returns></returns>
    Public Function CreateStorageOptions() As StorageOptions
        ' allow the remote LSP (a read only reader) to access the same database
        ' files concurrently; the lock is released between statements so the
        ' reader can grab a SharedRead lock in between.
        Return New StorageOptions With {
            .MergeIdleSeconds = DbMergeIdleSeconds,
            .MergeAfterOperations = DbMergeOperations,
            .FsyncEachWrite = False,
            .MultiProcessAccess = True
        }
    End Function

    ''' <summary>
    ''' build the configuration from the host supplied configuration dictionary.
    ''' accepted keys: ``data``, ``packages``, ``db``, ``wwwroot``, ``template``,
    ''' ``base-url``, ``tmp``, ``sitemap-enabled``, ``sitemap-base-url``,
    ''' ``sitemap-idle-seconds``, ``sitemap-interval-seconds``,
    ''' ``cluster-enabled``, ``cluster-k``, ``cluster-interval``,
    ''' ``cluster-min-samples`` and ``cluster-neighbors``.
    ''' </summary>
    ''' <param name="config">the host configuration dictionary (may be <c>Nothing</c>).</param>
    ''' <returns>the resolved configuration instance.</returns>
    Public Shared Function FromConfig(config As IReadOnlyDictionary(Of String, String)) As NugetConfiguration
        Dim data As String = getValue(config, "data")
        If String.IsNullOrEmpty(data) Then
            data = Path.Combine(Directory.GetCurrentDirectory(), "data")
        End If
        data = Path.GetFullPath(data)

        Dim packages As String = getValue(config, "packages")
        If String.IsNullOrEmpty(packages) Then
            packages = Path.Combine(data, "packages")
        End If

        Dim database As String = getValue(config, "db")
        If String.IsNullOrEmpty(database) Then
            database = Path.Combine(data, "db")
        End If

        Dim wwwroot As String = getValue(config, "wwwroot")
        Dim baseUrl As String = getValue(config, "base-url")
        Dim template As String = getValue(config, "template")
        Dim tmp As String = getValue(config, "tmp")

        If String.IsNullOrEmpty(template) Then
            If Not String.IsNullOrEmpty(wwwroot) Then
                template = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(wwwroot)), "template")
            Else
                template = Path.Combine(Directory.GetCurrentDirectory(), "template")
            End If
        End If

        If String.IsNullOrEmpty(tmp) Then
            If Not String.IsNullOrEmpty(wwwroot) Then
                tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(wwwroot)), "tmp")
            Else
                tmp = Path.Combine(Directory.GetCurrentDirectory(), "tmp")
            End If
        End If

        Return New NugetConfiguration(
            data, packages, database, wwwroot, template, baseUrl, tmp,
            boolValue(config, "sitemap-enabled", True),
            getValue(config, "sitemap-base-url"),
            intValue(config, "sitemap-idle-seconds", DefaultSitemapIdleSeconds, 5, 86400),
            intValue(config, "sitemap-interval-seconds", DefaultSitemapIntervalSeconds, 30, 86400),
            boolValue(config, "cluster-enabled", True),
            intValue(config, "cluster-k", DefaultClusterK, 2, 64),
            intValue(config, "cluster-interval", DefaultClusterIntervalMinutes, 1, 1440),
            intValue(config, "cluster-min-samples", DefaultClusterMinSamples, 2, 10000),
            intValue(config, "cluster-neighbors", DefaultClusterNeighbors, 2, 256),
            intValue(config, "db-merge-idle-seconds", DefaultDbMergeIdleSeconds, 5, 3600),
            intValue(config, "db-merge-operations", DefaultDbMergeOperations, 1, 1000000),
            intValue(config, "db-checkpoint-seconds", DefaultDbCheckpointSeconds, 30, 86400))
    End Function

    Private Shared Function getValue(config As IReadOnlyDictionary(Of String, String), name As String) As String
        Dim value As String = Nothing
        If config IsNot Nothing AndAlso config.TryGetValue(name, value) Then
            Return value
        End If
        Return Nothing
    End Function

    ''' <summary>
    ''' read an integer configuration value, clamped to the given inclusive
    ''' range so that an invalid value can never break the runtime.
    ''' </summary>
    Private Shared Function intValue(config As IReadOnlyDictionary(Of String, String), name As String,
                                     fallback As Integer, min As Integer, max As Integer) As Integer
        Dim value As String = getValue(config, name)
        Dim parsed As Integer

        If String.IsNullOrEmpty(value) OrElse
           Not Integer.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) Then
            Return fallback
        End If

        If parsed < min Then
            Return min
        ElseIf parsed > max Then
            Return max
        Else
            Return parsed
        End If
    End Function

    ''' <summary>
    ''' read a boolean configuration value; ``false``/``0``/``no``/``off``
    ''' disable a flag, everything else keeps the fallback.
    ''' </summary>
    Private Shared Function boolValue(config As IReadOnlyDictionary(Of String, String), name As String, fallback As Boolean) As Boolean
        Dim value As String = getValue(config, name)

        If String.IsNullOrEmpty(value) Then
            Return fallback
        End If

        Select Case value.Trim().ToLowerInvariant()
            Case "false", "0", "no", "off", "disabled" : Return False
            Case "true", "1", "yes", "on", "enabled" : Return True
            Case Else : Return fallback
        End Select
    End Function
End Class

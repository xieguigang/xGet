Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO

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

    Public Const DefaultClusterK As Integer = 6
    Public Const DefaultClusterIntervalMinutes As Integer = 30
    Public Const DefaultClusterMinSamples As Integer = 3
    Public Const DefaultClusterNeighbors As Integer = 15

    Private Sub New(data As String, packages As String, database As String, wwwroot As String, template As String, baseUrl As String,
                    clusterEnabled As Boolean, clusterK As Integer, clusterIntervalMinutes As Integer,
                    clusterMinSamples As Integer, clusterNeighbors As Integer)

        Me.DataDirectory = data
        Me.PackageDirectory = packages
        Me.DatabaseDirectory = database
        Me.Wwwroot = wwwroot
        Me.TemplateDirectory = template
        Me.BaseUrl = baseUrl
        Me.ClusterEnabled = clusterEnabled
        Me.ClusterK = clusterK
        Me.ClusterIntervalMinutes = clusterIntervalMinutes
        Me.ClusterMinSamples = clusterMinSamples
        Me.ClusterNeighbors = clusterNeighbors
    End Sub

    ''' <summary>
    ''' build the configuration from the host supplied configuration dictionary.
    ''' accepted keys: ``data``, ``packages``, ``db``, ``wwwroot``, ``base-url``,
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

        If String.IsNullOrEmpty(template) Then
            If Not String.IsNullOrEmpty(wwwroot) Then
                template = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(wwwroot)), "template")
            Else
                template = Path.Combine(Directory.GetCurrentDirectory(), "template")
            End If
        End If

        Return New NugetConfiguration(
            data, packages, database, wwwroot, template, baseUrl,
            boolValue(config, "cluster-enabled", True),
            intValue(config, "cluster-k", DefaultClusterK, 2, 64),
            intValue(config, "cluster-interval", DefaultClusterIntervalMinutes, 1, 1440),
            intValue(config, "cluster-min-samples", DefaultClusterMinSamples, 2, 10000),
            intValue(config, "cluster-neighbors", DefaultClusterNeighbors, 2, 256))
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

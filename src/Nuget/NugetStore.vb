Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports JSql.Engine
Imports JSql.Storage

''' <summary>
''' a registered nuget server user. the <see cref="salt"/> is a 128 characters
''' random string unique per user, and <see cref="secretKey"/> is the base32
''' encoded TOTP secret derived from the email and the salt.
''' </summary>
Public Class UserRecord
    Public Property id As Long
    Public Property email As String
    Public Property salt As String
    Public Property secretKey As String
    Public Property created As Date
End Class

''' <summary>
''' one published package version record.
''' </summary>
Public Class PackageRecord
    Public Property id As Long
    Public Property package_id As String
    Public Property version As String
    Public Property description As String
    Public Property authors As String
    Public Property tags As String
    Public Property project_url As String
    Public Property license As String
    Public Property dependencies As String
    Public Property downloads As Long
    Public Property size As Long
    Public Property sha256 As String
    Public Property published As Date
    Public Property listed As Boolean
End Class

''' <summary>
''' a package group summary used by the web front end: one row per package id.
''' </summary>
Public Class PackageSummary
    Public Property package_id As String
    Public Property latest_version As String
    Public Property description As String
    Public Property authors As String
    Public Property tags As String
    Public Property license As String
    Public Property project_url As String
    Public Property total_downloads As Long
    Public Property versions As Integer
    Public Property published As Date
End Class

''' <summary>
''' a package id group with its full version list, used by the nuget search
''' protocol.
''' </summary>
Public Class PackageSearchResult
    Public Property package_id As String
    Public Property versions As List(Of PackageRecord)
    Public Property latest As PackageRecord
    Public Property total_downloads As Long
End Class

''' <summary>
''' the database statistics shown on the web front end.
''' </summary>
Public Class NugetStats
    Public Property packages As Long
    Public Property versions As Long
    Public Property downloads As Long
    Public Property users As Long

    ''' <summary>the accumulated number of package detail page views.</summary>
    Public Property views As Long

    ''' <summary>the accumulated number of api documentation page views.</summary>
    Public Property docViews As Long
End Class

''' <summary>
''' one daily activity record of a package: how many package files were
''' downloaded, how many package detail pages were viewed and how many api
''' documentation pages were viewed on a utc day.
''' </summary>
Public Class DailyActivity
    ''' <summary>the package id, always stored in its lower-case form.</summary>
    Public Property package_id As String

    ''' <summary>the utc day key, formatted as ``yyyy-MM-dd``.</summary>
    Public Property day As String

    Public Property downloads As Long
    Public Property views As Long

    ''' <summary>the api documentation page views of the day.</summary>
    Public Property docViews As Long
End Class

''' <summary>
''' the umap 3d embedding and the kmeans cluster label of one package, as
''' produced by the periodic tag matrix analysis.
''' </summary>
Public Class PackageClusterRecord
    Public Property package_id As String
    Public Property x As Double
    Public Property y As Double
    Public Property z As Double
    Public Property cluster As Integer
    Public Property updated As Date
End Class

''' <summary>
''' one api comment document row of a package version. the <see cref="payload"/>
''' is the json document data of one type, and the scalar columns are used to
''' build the document index pages without decoding the payload.
''' </summary>
Public Class PackageApiDocRecord
    Public Property id As Long
    Public Property package_id As String
    Public Property version As String

    ''' <summary>the full name of the namespace that the type belongs to.</summary>
    Public Property namespace_name As String

    ''' <summary>the markdown comment document of the namespace.</summary>
    Public Property namespace_summary As String

    ''' <summary>the full name of the type, it is the lookup key of the type page.</summary>
    Public Property type_fullname As String

    Public Property type_name As String

    ''' <summary>the markdown comment document of the type.</summary>
    Public Property summary As String

    Public Property member_count As Integer

    ''' <summary>the json document data of the <c>ApiDocType</c>.</summary>
    Public Property payload As String
End Class

''' <summary>
''' a thin data access layer over the <see cref="SqlEngine"/> JSql engine.
''' </summary>
''' <remarks>
''' JSql has no parameter binding, no transactions, no auto increment and is
''' not thread safe, so every access is serialized through a monitor and every
''' value is escaped manually. package ids are compared case insensitively in
''' memory because the JSql string comparison is case sensitive.
''' </remarks>
Public Class NugetStore

    Private Const DatabaseName As String = "nuget"

    Private ReadOnly engine As SqlEngine
    Private ReadOnly sync As New Object

    Public Sub New(databaseDirectory As String, Optional options As StorageOptions = Nothing)
        Me.engine = New SqlEngine(databaseDirectory, options)
        Call initialize()
    End Sub

    ''' <summary>
    ''' merge the pending write ahead logs of every open table back into their
    ''' data files. It is the explicit fall back of the background checkpoint of
    ''' the engine: the host calls it from a low frequency timer so that the wal
    ''' files are merged even when the engine never becomes idle.
    ''' </summary>
    ''' <returns>the number of the merged tables.</returns>
    Public Function Checkpoint() As Integer
        SyncLock sync
            Return engine.MergeAll(force:=False)
        End SyncLock
    End Function

    Private Sub initialize()
        SyncLock sync
            Call engine.Execute($"CREATE DATABASE IF NOT EXISTS {DatabaseName}")
            Call engine.Execute($"USE {DatabaseName}")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS users (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  email VARCHAR(320) NOT NULL," &
                "  salt VARCHAR(256) NOT NULL," &
                "  secret VARCHAR(128) NOT NULL," &
                "  created DATETIME" &
                ") COMMENT='nuget server users'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS packages (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  version VARCHAR(100) NOT NULL," &
                "  description VARCHAR(4000)," &
                "  authors VARCHAR(500)," &
                "  tags VARCHAR(500)," &
                "  project_url VARCHAR(500)," &
                "  license VARCHAR(300)," &
                "  dependencies VARCHAR(4000)," &
                "  downloads INT DEFAULT 0," &
                "  size INT DEFAULT 0," &
                "  sha256 VARCHAR(128)," &
                "  published DATETIME," &
                "  listed BOOLEAN DEFAULT TRUE" &
                ") COMMENT='nuget package metadata'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS statistics (" &
                "  name VARCHAR(100) NOT NULL PRIMARY KEY," &
                "  payload LONGTEXT," &
                "  updated DATETIME" &
                ") COMMENT='precomputed nuget statistics'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_tags (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  tag VARCHAR(200) NOT NULL" &
                ") COMMENT='package tag index'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_dependencies (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  version VARCHAR(100)," &
                "  dependency_id VARCHAR(200) NOT NULL," &
                "  version_range VARCHAR(100)," &
                "  target_framework VARCHAR(100)" &
                ") COMMENT='package dependency index'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_metadata (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  version VARCHAR(100) NOT NULL," &
                "  name VARCHAR(100) NOT NULL," &
                "  value LONGTEXT" &
                ") COMMENT='full nuspec metadata'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_activity (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  day VARCHAR(20) NOT NULL," &
                "  downloads INT DEFAULT 0," &
                "  views INT DEFAULT 0" &
                ") COMMENT='daily download and page view counters'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_doc_activity (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  day VARCHAR(20) NOT NULL," &
                "  visits INT DEFAULT 0" &
                ") COMMENT='daily api documentation page view counters'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_clusters (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  x DOUBLE," &
                "  y DOUBLE," &
                "  z DOUBLE," &
                "  cluster INT," &
                "  updated DATETIME" &
                ") COMMENT='umap 3d embedding and kmeans cluster label'")
            Call engine.Execute(
                "CREATE TABLE IF NOT EXISTS package_api_docs (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  package_id VARCHAR(200) NOT NULL," &
                "  version VARCHAR(100) NOT NULL," &
                "  namespace VARCHAR(300)," &
                "  namespace_summary VARCHAR(2000)," &
                "  type_fullname VARCHAR(400) NOT NULL," &
                "  type_name VARCHAR(200)," &
                "  summary VARCHAR(2000)," &
                "  member_count INT DEFAULT 0," &
                "  payload LONGTEXT" &
                ") COMMENT='per package version api comment documents'")
        End SyncLock
    End Sub

#Region "sql helpers"

    Private Function query(sql As String) As ResultSet
        Call engine.Execute($"USE {DatabaseName}")
        Return engine.Execute(sql)
    End Function

    Private Sub exec(sql As String)
        Call engine.Execute($"USE {DatabaseName}")
        Call engine.Execute(sql)
    End Sub

    ''' <summary>
    ''' escape a string literal for the JSql tokenizer: a backslash is the
    ''' escape character and a single quote is escaped by doubling it. line
    ''' breaks are flattened to spaces to keep the literal on a single line.
    ''' </summary>
    Private Shared Function esc(value As String) As String
        If value Is Nothing Then
            Return ""
        End If
        Return value _
            .Replace("\", "\\") _
            .Replace("'", "''") _
            .Replace(vbCrLf, " ") _
            .Replace(vbCr, " ") _
            .Replace(vbLf, " ")
    End Function

    Private Shared Function dateLiteral(value As Date) As String
        Return "'" & value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & "'"
    End Function

    ''' <summary>
    ''' format a floating point value as a plain invariant sql literal. the
    ''' value is rounded to 10 decimals and never written in scientific
    ''' notation, which the JSql tokenizer would not understand.
    ''' </summary>
    Private Shared Function number(value As Double) As String
        If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then
            Return "0"
        End If

        Return value.ToString("0.##########", CultureInfo.InvariantCulture)
    End Function

    Private Function nextId(table As String) As Long
        Dim rs As ResultSet = query($"SELECT MAX(id) AS max_id FROM {table}")
        If rs Is Nothing OrElse rs.Rows.Count = 0 Then
            Return 1
        End If
        Dim value As Object = rs.Rows(0)(0)
        If value Is Nothing Then
            Return 1
        End If
        Return Convert.ToInt64(value, CultureInfo.InvariantCulture) + 1
    End Function

    Private Shared Function toStr(value As Object) As String
        If value Is Nothing Then Return ""
        Return value.ToString()
    End Function

    Private Shared Function toLong(value As Object) As Long
        If value Is Nothing Then Return 0
        Return Convert.ToInt64(value, CultureInfo.InvariantCulture)
    End Function

    Private Shared Function toDouble(value As Object) As Double
        If value Is Nothing Then Return 0

        Dim parsed As Double
        If Double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, parsed) Then
            Return parsed
        End If

        Return 0
    End Function

    Private Shared Function toDate(value As Object) As Date
        If value Is Nothing Then Return Date.MinValue
        Dim text As String = value.ToString()
        Dim result As Date
        If Date.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, result) Then
            Return result
        End If
        Return Date.MinValue
    End Function

    Private Shared Function toBool(value As Object) As Boolean
        If value Is Nothing Then Return False
        If TypeOf value Is Boolean Then Return CBool(value)
        Dim text As String = value.ToString()
        Return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) OrElse text = "1"
    End Function

#End Region

#Region "users"

    Public Function GetUser(email As String) As UserRecord
        If String.IsNullOrEmpty(email) Then Return Nothing

        SyncLock sync
            Dim rs As ResultSet = query($"SELECT id, email, salt, secret, created FROM users")
            For Each row As Object() In rs.Rows
                Dim record = readUser(rs.Columns, row)
                If record.email IsNot Nothing AndAlso record.email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) Then
                    Return record
                End If
            Next
            Return Nothing
        End SyncLock
    End Function

    Public Function CreateUser(email As String, salt As String, secret As String) As UserRecord
        SyncLock sync
            If GetUser(email) IsNot Nothing Then
                Return Nothing
            End If

            Dim id As Long = nextId("users")
            Dim now As Date = Date.UtcNow

            Call exec(
                $"INSERT INTO users (id, email, salt, secret, created) VALUES (" &
                $"{id}, '{esc(email)}', '{esc(salt)}', '{esc(secret)}', {dateLiteral(now)})")

            Return New UserRecord With {
                .id = id,
                .email = email,
                .salt = salt,
                .secretKey = secret,
                .created = now
            }
        End SyncLock
    End Function

    Public Function ReadAllUsers() As List(Of UserRecord)
        SyncLock sync
            Dim rs As ResultSet = query("SELECT id, email, salt, secret, created FROM users")
            Dim list As New List(Of UserRecord)

            For Each row As Object() In rs.Rows
                list.Add(readUser(rs.Columns, row))
            Next

            Return list
        End SyncLock
    End Function

    Private Shared Function readUser(columns As List(Of String), row As Object()) As UserRecord
        Dim record As New UserRecord

        For i As Integer = 0 To columns.Count - 1
            Select Case columns(i).ToLowerInvariant()
                Case "id" : record.id = toLong(row(i))
                Case "email" : record.email = toStr(row(i))
                Case "salt" : record.salt = toStr(row(i))
                Case "secret" : record.secretKey = toStr(row(i))
                Case "created" : record.created = toDate(row(i))
            End Select
        Next

        Return record
    End Function

#End Region

#Region "packages"

    Public Function ReadAllPackages() As List(Of PackageRecord)
        SyncLock sync
            Dim rs As ResultSet = query("SELECT * FROM packages")
            Return readPackages(rs)
        End SyncLock
    End Function

    Public Function GetVersions(packageId As String) As List(Of PackageRecord)
        Return ReadAllPackages() _
            .Where(Function(p) p.package_id.Equals(packageId, StringComparison.OrdinalIgnoreCase)) _
            .OrderBy(Function(p) VersionKey(p.version)) _
            .ToList()
    End Function

    Public Function GetPackage(packageId As String, version As String) As PackageRecord
        Return ReadAllPackages() _
            .Where(Function(p) p.package_id.Equals(packageId, StringComparison.OrdinalIgnoreCase)) _
            .Where(Function(p) p.version.Equals(version, StringComparison.OrdinalIgnoreCase)) _
            .FirstOrDefault()
    End Function

    Public Function PackageExists(packageId As String, version As String) As Boolean
        Return GetPackage(packageId, version) IsNot Nothing
    End Function

    Public Function AddPackage(pkg As PackageRecord) As PackageRecord
        SyncLock sync
            pkg.id = nextId("packages")

            Call exec(
                "INSERT INTO packages (id, package_id, version, description, authors, tags, project_url, license, dependencies, downloads, size, sha256, published, listed) VALUES (" &
                $"{pkg.id}, '{esc(pkg.package_id)}', '{esc(pkg.version)}', '{esc(pkg.description)}', '{esc(pkg.authors)}', '{esc(pkg.tags)}', '{esc(pkg.project_url)}', '{esc(pkg.license)}', '{esc(pkg.dependencies)}', {pkg.downloads}, {pkg.size}, '{esc(pkg.sha256)}', {dateLiteral(pkg.published)}, {If(pkg.listed, "TRUE", "FALSE")})")

            Return pkg
        End SyncLock
    End Function

    Public Sub IncrementDownload(packageId As String, version As String)
        SyncLock sync
            Dim pkg As PackageRecord = GetPackage(packageId, version)
            If pkg IsNot Nothing Then
                Call exec($"UPDATE packages SET downloads = downloads + 1 WHERE id = {pkg.id}")
            End If
        End SyncLock
    End Sub

    ''' <summary>
    ''' group all listed packages by their (case insensitive) package id,
    ''' optionally filtered by a keyword over the id and tags.
    ''' </summary>
    Public Function GroupPackages(keyword As String, Optional includeUnlisted As Boolean = False) As List(Of PackageSearchResult)
        Dim text As String = If(keyword, "").Trim()
        Dim all As List(Of PackageRecord) = ReadAllPackages()

        Dim groups = all _
            .Where(Function(p) includeUnlisted OrElse p.listed) _
            .Where(Function(p) text = "" OrElse
                (p.package_id IsNot Nothing AndAlso p.package_id.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) OrElse
                (p.tags IsNot Nothing AndAlso p.tags.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)) _
            .GroupBy(Function(p) p.package_id.ToLowerInvariant())

        Dim list As New List(Of PackageSearchResult)

        For Each group In groups
            Dim versions As List(Of PackageRecord) = group.OrderBy(Function(p) VersionKey(p.version)).ToList()

            list.Add(New PackageSearchResult With {
                .package_id = versions.Last().package_id,
                .versions = versions,
                .latest = versions.Last(),
                .total_downloads = versions.Sum(Function(p) p.downloads)
            })
        Next

        Return list
    End Function

    Public Function ListPackages(keyword As String) As List(Of PackageSummary)
        Return GroupPackages(keyword) _
            .OrderByDescending(Function(g) g.total_downloads) _
            .ThenBy(Function(g) g.package_id, StringComparer.OrdinalIgnoreCase) _
            .Select(Function(g) New PackageSummary With {
                .package_id = g.package_id,
                .latest_version = g.latest.version,
                .description = g.latest.description,
                .authors = g.latest.authors,
                .tags = g.latest.tags,
                .license = g.latest.license,
                .project_url = g.latest.project_url,
                .total_downloads = g.total_downloads,
                .versions = g.versions.Count,
                .published = g.latest.published
            }) _
            .ToList()
    End Function

    Public Function Stats() As NugetStats
        Dim all As List(Of PackageRecord) = ReadAllPackages()

        Return New NugetStats With {
            .packages = all.Select(Function(p) p.package_id.ToLowerInvariant()).Distinct().Count(),
            .versions = all.Count,
            .downloads = all.Sum(Function(p) p.downloads),
            .users = ReadAllUsers().Count,
            .views = ReadActivityRows().Sum(Function(a) a.views),
            .docViews = ReadDocActivityRows().Sum(Function(a) a.docViews)
        }
    End Function

    Private Shared Function readPackages(rs As ResultSet) As List(Of PackageRecord)
        Dim list As New List(Of PackageRecord)

        If rs Is Nothing OrElse Not rs.IsQuery Then
            Return list
        End If

        For Each row As Object() In rs.Rows
            list.Add(readPackage(rs.Columns, row))
        Next

        Return list
    End Function

    Private Shared Function readPackage(columns As List(Of String), row As Object()) As PackageRecord
        Dim record As New PackageRecord

        For i As Integer = 0 To columns.Count - 1
            Select Case columns(i).ToLowerInvariant()
                Case "id" : record.id = toLong(row(i))
                Case "package_id" : record.package_id = toStr(row(i))
                Case "version" : record.version = toStr(row(i))
                Case "description" : record.description = toStr(row(i))
                Case "authors" : record.authors = toStr(row(i))
                Case "tags" : record.tags = toStr(row(i))
                Case "project_url" : record.project_url = toStr(row(i))
                Case "license" : record.license = toStr(row(i))
                Case "dependencies" : record.dependencies = toStr(row(i))
                Case "downloads" : record.downloads = toLong(row(i))
                Case "size" : record.size = toLong(row(i))
                Case "sha256" : record.sha256 = toStr(row(i))
                Case "published" : record.published = toDate(row(i))
                Case "listed" : record.listed = toBool(row(i))
            End Select
        Next

        Return record
    End Function

    ''' <summary>
    ''' build a monotonically sortable key for a nuget version string so that
    ''' the version list can be ordered without a full semver parser.
    ''' </summary>
    Public Shared Function VersionKey(version As String) As String
        If String.IsNullOrEmpty(version) Then
            Return ""
        End If

        Dim parts As String() = version.Split("-"c)(0).Split("."c)
        Dim sb As New StringBuilder

        For i As Integer = 0 To 3
            Dim number As Integer = 0
            If i < parts.Length Then
                Integer.TryParse(parts(i), number)
            End If
            sb.Append(number.ToString("D6"))
        Next

        Dim release As String = If(version.Contains("-"), "0", "1")
        Return sb.ToString() & release & version
    End Function

#End Region

#Region "daily activity"

    ''' <summary>
    ''' the utc day key (``yyyy-MM-dd``) of the given moment, or of the current
    ''' moment when no value is given.
    ''' </summary>
    ''' <param name="value">the moment to convert; defaults to <see cref="Date.UtcNow"/>.</param>
    Public Shared Function DayKey(Optional value As Date? = Nothing) As String
        Dim moment As Date = If(value.HasValue, value.Value, Date.UtcNow)
        Return moment.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    End Function

    ''' <summary>
    ''' record a successful package file download: increments both the total
    ''' download counter of the package version and the download counter of the
    ''' current utc day.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="version">the package version.</param>
    Public Sub RecordDownload(packageId As String, version As String)
        SyncLock sync
            Dim pkg As PackageRecord = GetPackage(packageId, version)
            If pkg IsNot Nothing Then
                Call exec($"UPDATE packages SET downloads = downloads + 1 WHERE id = {pkg.id}")
            End If

            Call incrementActivity(packageId, DayKey(), "downloads")
        End SyncLock
    End Sub

    ''' <summary>
    ''' record one package detail page view of the current utc day.
    ''' </summary>
    ''' <param name="packageId">the viewed package id.</param>
    Public Sub RecordView(packageId As String)
        SyncLock sync
            Call incrementActivity(packageId, DayKey(), "views")
        End SyncLock
    End Sub

    ''' <summary>
    ''' record one api documentation page view of the current utc day. It is
    ''' called for the per package documentation pages only: the package index
    ''' page (``/docs/{id}/{version}/index.html``) and the type content page
    ''' (``/docs/{id}/{version}/{type}.html``). The global documentation index is
    ''' not a part of any single package, so it is not counted.
    ''' </summary>
    ''' <param name="packageId">the viewed package id.</param>
    Public Sub RecordDocView(packageId As String)
        SyncLock sync
            Call incrementDocActivity(packageId, DayKey())
        End SyncLock
    End Sub

    ''' <summary>
    ''' read the daily activity of one package. the missing days are not filled
    ''' here; the controller expands the series before returning it to the web
    ''' client.
    ''' </summary>
    ''' <param name="packageId">the package id (compared case insensitively).</param>
    ''' <param name="days">the number of trailing days to read.</param>
    ''' <returns>the recorded days, ordered from the oldest to the newest.</returns>
    Public Function GetPackageActivity(packageId As String, days As Integer) As List(Of DailyActivity)
        Dim key As String = If(packageId, "").Trim().ToLowerInvariant()
        Dim from As String = DayKey(Date.UtcNow.AddDays(-(Math.Max(1, days) - 1)))
        Dim aggregated As New Dictionary(Of String, DailyActivity)(StringComparer.Ordinal)

        SyncLock sync
            For Each row As DailyActivity In ReadActivityRows()
                If row.day < from OrElse Not String.Equals(row.package_id, key, StringComparison.Ordinal) Then
                    Continue For
                End If

                Dim item As DailyActivity = Nothing
                If Not aggregated.TryGetValue(row.day, item) Then
                    item = New DailyActivity With {.package_id = key, .day = row.day}
                    aggregated(row.day) = item
                End If

                item.downloads += row.downloads
                item.views += row.views
            Next

            ' merge the api documentation page views into the very same day series
            For Each row As DailyActivity In ReadDocActivityRows()
                If row.day < from OrElse Not String.Equals(row.package_id, key, StringComparison.Ordinal) Then
                    Continue For
                End If

                Dim item As DailyActivity = Nothing
                If Not aggregated.TryGetValue(row.day, item) Then
                    item = New DailyActivity With {.package_id = key, .day = row.day}
                    aggregated(row.day) = item
                End If

                item.docViews += row.docViews
            Next
        End SyncLock

        Return aggregated.Values.OrderBy(Function(a) a.day, StringComparer.Ordinal).ToList()
    End Function

    ''' <summary>
    ''' read the daily activity summed over all of the packages of the feed.
    ''' </summary>
    ''' <param name="days">the number of trailing days to read.</param>
    ''' <returns>the recorded days, ordered from the oldest to the newest.</returns>
    Public Function GetFeedActivity(days As Integer) As List(Of DailyActivity)
        Dim from As String = DayKey(Date.UtcNow.AddDays(-(Math.Max(1, days) - 1)))
        Dim aggregated As New Dictionary(Of String, DailyActivity)(StringComparer.Ordinal)

        SyncLock sync
            For Each row As DailyActivity In ReadActivityRows()
                If row.day < from Then
                    Continue For
                End If

                Dim item As DailyActivity = Nothing
                If Not aggregated.TryGetValue(row.day, item) Then
                    item = New DailyActivity With {.day = row.day}
                    aggregated(row.day) = item
                End If

                item.downloads += row.downloads
                item.views += row.views
            Next

            ' merge the api documentation page views into the very same day series
            For Each row As DailyActivity In ReadDocActivityRows()
                If row.day < from Then
                    Continue For
                End If

                Dim item As DailyActivity = Nothing
                If Not aggregated.TryGetValue(row.day, item) Then
                    item = New DailyActivity With {.day = row.day}
                    aggregated(row.day) = item
                End If

                item.docViews += row.docViews
            Next
        End SyncLock

        Return aggregated.Values.OrderBy(Function(a) a.day, StringComparer.Ordinal).ToList()
    End Function

    ''' <summary>
    ''' read every daily api documentation page view row of the database.
    ''' </summary>
    Private Function ReadDocActivityRows() As List(Of DailyActivity)
        Dim list As New List(Of DailyActivity)

        SyncLock sync
            Dim rs As ResultSet = query("SELECT package_id, day, visits FROM package_doc_activity")
            If rs Is Nothing OrElse Not rs.IsQuery Then
                Return list
            End If

            For Each row As Object() In rs.Rows
                list.Add(New DailyActivity With {
                    .package_id = toStr(row(0)).Trim().ToLowerInvariant(),
                    .day = toStr(row(1)),
                    .docViews = toLong(row(2))
                })
            Next
        End SyncLock

        Return list
    End Function

    ''' <summary>
    ''' read every daily activity row of the database.
    ''' </summary>
    Private Function ReadActivityRows() As List(Of DailyActivity)
        Dim list As New List(Of DailyActivity)

        SyncLock sync
            Dim rs As ResultSet = query("SELECT package_id, day, downloads, views FROM package_activity")
            If rs Is Nothing OrElse Not rs.IsQuery Then
                Return list
            End If

            For Each row As Object() In rs.Rows
                list.Add(New DailyActivity With {
                    .package_id = toStr(row(0)).Trim().ToLowerInvariant(),
                    .day = toStr(row(1)),
                    .downloads = toLong(row(2)),
                    .views = toLong(row(3))
                })
            Next
        End SyncLock

        Return list
    End Function

    ''' <summary>
    ''' increment one counter of a (package, day) activity row, creating the row
    ''' when the package has no activity recorded yet on that day.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="day">the utc day key.</param>
    ''' <param name="field">either ``downloads`` or ``views``.</param>
    Private Sub incrementActivity(packageId As String, day As String, field As String)
        Dim key As String = If(packageId, "").Trim().ToLowerInvariant()

        If key.StringEmpty OrElse day.StringEmpty Then
            Return
        End If

        Dim id As Long = -1
        Dim rs As ResultSet = query($"SELECT id FROM package_activity WHERE package_id = '{esc(key)}' AND day = '{esc(day)}'")

        If rs IsNot Nothing AndAlso rs.IsQuery AndAlso rs.Rows.Count > 0 Then
            id = toLong(rs.Rows(0)(0))
        End If

        If id >= 0 Then
            Call exec($"UPDATE package_activity SET {field} = {field} + 1 WHERE id = {id}")
        Else
            Dim downloads As Integer = If(field = "downloads", 1, 0)
            Dim views As Integer = If(field = "views", 1, 0)
            Dim newId As Long = nextId("package_activity")

            Call exec(
                "INSERT INTO package_activity (id, package_id, day, downloads, views) VALUES (" &
                $"{newId}, '{esc(key)}', '{esc(day)}', {downloads}, {views})")
        End If
    End Sub

    ''' <summary>
    ''' increment the api documentation page view counter of a (package, day) row,
    ''' creating the row when the package has no documentation view recorded yet
    ''' on that day.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="day">the utc day key.</param>
    Private Sub incrementDocActivity(packageId As String, day As String)
        Dim key As String = If(packageId, "").Trim().ToLowerInvariant()

        If key.StringEmpty OrElse day.StringEmpty Then
            Return
        End If

        Dim id As Long = -1
        Dim rs As ResultSet = query($"SELECT id FROM package_doc_activity WHERE package_id = '{esc(key)}' AND day = '{esc(day)}'")

        If rs IsNot Nothing AndAlso rs.IsQuery AndAlso rs.Rows.Count > 0 Then
            id = toLong(rs.Rows(0)(0))
        End If

        If id >= 0 Then
            Call exec($"UPDATE package_doc_activity SET visits = visits + 1 WHERE id = {id}")
        Else
            Dim newId As Long = nextId("package_doc_activity")

            Call exec(
                "INSERT INTO package_doc_activity (id, package_id, day, visits) VALUES (" &
                $"{newId}, '{esc(key)}', '{esc(day)}', 1)")
        End If
    End Sub

#End Region

#Region "package clusters (umap + kmeans)"

    ''' <summary>
    ''' replace the whole package cluster table with one analysis result. the
    ''' rows are deleted and reinserted inside a single monitor lock so that the
    ''' web front end never observes a half written cluster table.
    ''' </summary>
    ''' <param name="records">the per package embedding records.</param>
    Public Sub ReplacePackageClusters(records As IEnumerable(Of PackageClusterRecord))
        SyncLock sync
            Call exec("DELETE FROM package_clusters")

            Dim id As Long = 1

            If records IsNot Nothing Then
                For Each record As PackageClusterRecord In records
                    If record Is Nothing OrElse record.package_id.StringEmpty() Then
                        Continue For
                    End If

                    Call exec(
                        "INSERT INTO package_clusters (id, package_id, x, y, z, cluster, updated) VALUES (" &
                        $"{id}, '{esc(record.package_id)}', {number(record.x)}, {number(record.y)}, {number(record.z)}, {record.cluster}, {dateLiteral(record.updated)})")

                    id += 1
                Next
            End If
        End SyncLock
    End Sub

    ''' <summary>
    ''' read every package cluster row of the database.
    ''' </summary>
    ''' <returns>the per package embedding records.</returns>
    Public Function ReadPackageClusters() As List(Of PackageClusterRecord)
        Dim list As New List(Of PackageClusterRecord)

        SyncLock sync
            Dim rs As ResultSet = query("SELECT package_id, x, y, z, cluster, updated FROM package_clusters")
            If rs Is Nothing OrElse Not rs.IsQuery Then
                Return list
            End If

            For Each row As Object() In rs.Rows
                list.Add(New PackageClusterRecord With {
                    .package_id = toStr(row(0)),
                    .x = toDouble(row(1)),
                    .y = toDouble(row(2)),
                    .z = toDouble(row(3)),
                    .cluster = CInt(toLong(row(4))),
                    .updated = toDate(row(5))
                })
            Next
        End SyncLock

        Return list
    End Function

    ''' <summary>
    ''' read the cluster assignment of one package; returns <c>Nothing</c> when
    ''' the package was not part of the last analysis (for example a package
    ''' without any tag).
    ''' </summary>
    ''' <param name="packageId">the package id, compared case insensitively.</param>
    Public Function GetPackageCluster(packageId As String) As PackageClusterRecord
        If String.IsNullOrEmpty(packageId) Then
            Return Nothing
        End If

        Return ReadPackageClusters() _
            .FirstOrDefault(Function(item) item.package_id.Equals(packageId.Trim(), StringComparison.OrdinalIgnoreCase))
    End Function

#End Region

#Region "statistics"

    ''' <summary>
    ''' insert or update a precomputed statistic json document.
    ''' </summary>
    ''' <param name="name">the statistic key, for example ``tags``.</param>
    ''' <param name="payload">the precomputed json document.</param>
    Public Sub SaveStatistic(name As String, payload As String)
        SyncLock sync
            Dim rs As ResultSet = query($"SELECT name FROM statistics WHERE name = '{esc(name)}'")
            Dim now As Date = Date.UtcNow

            If rs IsNot Nothing AndAlso rs.IsQuery AndAlso rs.Rows.Count > 0 Then
                Call exec($"UPDATE statistics SET payload = '{esc(payload)}', updated = {dateLiteral(now)} WHERE name = '{esc(name)}'")
            Else
                Call exec($"INSERT INTO statistics (name, payload, updated) VALUES ('{esc(name)}', '{esc(payload)}', {dateLiteral(now)})")
            End If
        End SyncLock
    End Sub

    ''' <summary>
    ''' read a precomputed statistic json document; returns <c>Nothing</c> when
    ''' the statistic has never been computed.
    ''' </summary>
    ''' <param name="name">the statistic key, for example ``tags``.</param>
    Public Function GetStatistic(name As String) As String
        SyncLock sync
            Dim rs As ResultSet = query($"SELECT name, payload FROM statistics WHERE name = '{esc(name)}'")

            If rs Is Nothing OrElse Not rs.IsQuery OrElse rs.Rows.Count = 0 Then
                Return Nothing
            End If

            Dim index As Integer = rs.Columns.FindIndex(Function(c) c.Equals("payload", StringComparison.OrdinalIgnoreCase))
            If index < 0 Then
                Return Nothing
            End If

            Return toStr(rs.Rows(0)(index))
        End SyncLock
    End Function

    ''' <summary>
    ''' test whether a statistic document already exists.
    ''' </summary>
    ''' <param name="name">the statistic key, for example ``tags``.</param>
    Public Function HasStatistic(name As String) As Boolean
        Return Not String.IsNullOrEmpty(GetStatistic(name))
    End Function

#End Region

#Region "package index (tags / dependencies / metadata)"

    ''' <summary>
    ''' replace the tag index rows of the given package.
    ''' </summary>
    Public Sub ReplacePackageTags(packageId As String, tags As IEnumerable(Of String))
        SyncLock sync
            Call exec($"DELETE FROM package_tags WHERE package_id = '{esc(packageId)}'")

            For Each tag As String In tags.Distinct(StringComparer.OrdinalIgnoreCase)
                If tag.StringEmpty() Then
                    Continue For
                End If
                Dim id As Long = nextId("package_tags")
                Call exec($"INSERT INTO package_tags (id, package_id, tag) VALUES ({id}, '{esc(packageId)}', '{esc(tag.ToLowerInvariant())}')")
            Next
        End SyncLock
    End Sub

    ''' <summary>
    ''' replace the dependency index rows of the given package version.
    ''' </summary>
    Public Sub ReplacePackageDependencies(packageId As String, version As String, dependencies As List(Of NuspecDependency))
        SyncLock sync
            Call exec($"DELETE FROM package_dependencies WHERE package_id = '{esc(packageId)}'")

            If dependencies Is Nothing Then
                Return
            End If

            For Each dependency As NuspecDependency In dependencies
                If dependency.id.StringEmpty() Then
                    Continue For
                End If
                Dim id As Long = nextId("package_dependencies")
                Call exec(
                    "INSERT INTO package_dependencies (id, package_id, version, dependency_id, version_range, target_framework) VALUES (" &
                    $"{id}, '{esc(packageId)}', '{esc(version)}', '{esc(dependency.id)}', '{esc(dependency.range)}', '{esc(dependency.targetFramework)}')")
            Next
        End SyncLock
    End Sub

    ''' <summary>
    ''' replace the full nuspec metadata rows of the given package version.
    ''' </summary>
    Public Sub ReplacePackageMetadata(packageId As String, version As String, values As Dictionary(Of String, String))
        SyncLock sync
            Call exec($"DELETE FROM package_metadata WHERE package_id = '{esc(packageId)}'")

            If values Is Nothing Then
                Return
            End If

            For Each item In values
                Dim id As Long = nextId("package_metadata")
                Call exec($"INSERT INTO package_metadata (id, package_id, version, name, value) VALUES ({id}, '{esc(packageId)}', '{esc(version)}', '{esc(item.Key)}', '{esc(item.Value)}')")
            Next
        End SyncLock
    End Sub

    ''' <summary>
    ''' read the stored nuspec metadata of a package id (latest version first).
    ''' </summary>
    Public Function GetPackageMetadata(packageId As String) As Dictionary(Of String, String)
        Return GetPackageMetadata(packageId, Nothing)
    End Function

    ''' <summary>
    ''' read the stored nuspec metadata of a package id, optionally restricted
    ''' to one package version.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="version">the exact version to read; all versions when empty.</param>
    ''' <returns>the metadata name/value pairs (the first row wins per name).</returns>
    Public Function GetPackageMetadata(packageId As String, version As String) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        SyncLock sync
            Dim rs As ResultSet = query($"SELECT package_id, version, name, value FROM package_metadata WHERE package_id = '{esc(packageId)}'")
            If rs Is Nothing OrElse Not rs.IsQuery Then
                Return result
            End If

            For Each row As Object() In rs.Rows
                If Not String.IsNullOrEmpty(version) AndAlso
                   Not String.Equals(toStr(row(1)), version, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Dim name As String = toStr(row(2))
                If Not result.ContainsKey(name) Then
                    result(name) = toStr(row(3))
                End If
            Next
        End SyncLock

        Return result
    End Function

    ''' <summary>
    ''' read the indexed dependency list of the latest version of a package.
    ''' </summary>
    Public Function GetPackageDependencies(packageId As String) As List(Of NuspecDependency)
        Dim list As New List(Of NuspecDependency)

        SyncLock sync
            Dim rs As ResultSet = query($"SELECT dependency_id, version_range, target_framework, version FROM package_dependencies WHERE package_id = '{esc(packageId)}'")
            If rs Is Nothing OrElse Not rs.IsQuery Then
                Return list
            End If

            Dim version As String = latestVersionOf(packageId)

            For Each row As Object() In rs.Rows
                If Not String.Equals(toStr(row(3)), version, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If
                Call list.Add(New NuspecDependency With {
                    .id = toStr(row(0)),
                    .range = toStr(row(1)),
                    .targetFramework = toStr(row(2))
                })
            Next
        End SyncLock

        Return list
    End Function

    ''' <summary>
    ''' test whether the package id exists in the feed (any version).
    ''' </summary>
    Public Function PackageExists(packageId As String) As Boolean
        Dim rs As ResultSet = query($"SELECT package_id FROM packages WHERE package_id = '{esc(packageId)}'")
        If rs IsNot Nothing AndAlso rs.IsQuery Then
            For Each row As Object() In rs.Rows
                If String.Equals(toStr(row(0)), packageId, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            Next
        End If
        Return False
    End Function

    ''' <summary>
    ''' page through the packages that carry the given tag.
    ''' </summary>
    Public Function GetPackagesByTag(tag As String, skip As Integer, take As Integer, ByRef total As Integer) As List(Of PackageSummary)
        Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        SyncLock sync
            Dim rs As ResultSet = query($"SELECT package_id FROM package_tags WHERE tag = '{esc(If(tag, "").ToLowerInvariant())}'")
            If rs IsNot Nothing AndAlso rs.IsQuery Then
                For Each row As Object() In rs.Rows
                    Call ids.Add(toStr(row(0)))
                Next
            End If
        End SyncLock

        Dim all As List(Of PackageSummary) = ListPackages("") _
            .Where(Function(p) ids.Contains(p.package_id)) _
            .ToList()

        total = all.Count
        Return all.Skip(skip).Take(take).ToList()
    End Function

    Private Function latestVersionOf(packageId As String) As String
        Dim versions As List(Of PackageRecord) = ReadAllPackages() _
            .Where(Function(p) p.package_id.Equals(packageId, StringComparison.OrdinalIgnoreCase)) _
            .OrderBy(Function(p) VersionKey(p.version)) _
            .ToList()

        Return If(versions.Count = 0, "", versions.Last().version)
    End Function

#End Region

#Region "package api documents"

    ''' <summary>
    ''' replace the api comment documents of one package version.
    ''' </summary>
    ''' <param name="packageId">the package id.</param>
    ''' <param name="version">the package version.</param>
    ''' <param name="records">the per type document rows.</param>
    Public Sub ReplacePackageApiDocs(packageId As String, version As String, records As IEnumerable(Of PackageApiDocRecord))
        SyncLock sync
            Call exec($"DELETE FROM package_api_docs WHERE package_id = '{esc(packageId)}' AND version = '{esc(version)}'")

            If records Is Nothing Then
                Return
            End If

            Dim id As Long = nextId("package_api_docs")

            For Each record As PackageApiDocRecord In records
                If record Is Nothing OrElse String.IsNullOrEmpty(record.type_fullname) Then
                    Continue For
                End If

                Call exec(
                    "INSERT INTO package_api_docs (id, package_id, version, namespace, namespace_summary, type_fullname, type_name, summary, member_count, payload) VALUES (" &
                    $"{id}, '{esc(record.package_id)}', '{esc(record.version)}', '{esc(record.namespace_name)}', '{esc(record.namespace_summary)}', '{esc(record.type_fullname)}', '{esc(record.type_name)}', '{esc(record.summary)}', {record.member_count}, '{esc(record.payload)}')")

                id += 1
            Next
        End SyncLock
    End Sub

    ''' <summary>
    ''' delete the api comment documents of one package version.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    Public Sub DeletePackageApiDocs(packageId As String, version As String)
        SyncLock sync
            Call exec($"DELETE FROM package_api_docs WHERE package_id = '{esc(packageId)}' AND version = '{esc(version)}'")
        End SyncLock
    End Sub

    ''' <summary>
    ''' read the type document rows of one package version, including the payload.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function ReadPackageApiDocs(packageId As String, version As String) As List(Of PackageApiDocRecord)
        Return queryApiDocs(includePayload:=True, packageId:=packageId, version:=version)
    End Function

    ''' <summary>
    ''' read the type index rows (without the payload) of one package version.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function ReadPackageApiDocIndex(packageId As String, version As String) As List(Of PackageApiDocRecord)
        Return queryApiDocs(includePayload:=False, packageId:=packageId, version:=version)
    End Function

    ''' <summary>
    ''' read the type index rows of every package version (without the payload).
    ''' </summary>
    ''' <returns></returns>
    Public Function ReadApiDocIndex() As List(Of PackageApiDocRecord)
        Return queryApiDocs(includePayload:=False)
    End Function

    ''' <summary>
    ''' read the document row of one type (including the payload).
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="typeFullName"></param>
    ''' <returns></returns>
    Public Function GetPackageApiDoc(packageId As String, version As String, typeFullName As String) As PackageApiDocRecord
        If String.IsNullOrEmpty(typeFullName) Then
            Return Nothing
        End If

        Return ReadPackageApiDocs(packageId, version) _
            .FirstOrDefault(Function(r) String.Equals(r.type_fullname, typeFullName, StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>
    ''' test whether a package version has any api comment document.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function HasPackageApiDocs(packageId As String, version As String) As Boolean
        Return ReadPackageApiDocIndex(packageId, version).Count > 0
    End Function

    ''' <summary>
    ''' the versions of a package which have api comment documents, ordered by the
    ''' nuget version order.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <returns></returns>
    Public Function GetPackageApiDocVersions(packageId As String) As List(Of String)
        Return queryApiDocs(includePayload:=False, packageId:=packageId) _
            .Select(Function(r) r.version) _
            .Where(Function(v) Not String.IsNullOrEmpty(v)) _
            .Distinct(StringComparer.OrdinalIgnoreCase) _
            .OrderBy(Function(v) VersionKey(v)) _
            .ToList()
    End Function

    Private Function queryApiDocs(includePayload As Boolean, Optional packageId As String = "", Optional version As String = "") As List(Of PackageApiDocRecord)
        Dim columns As String = "id, package_id, version, namespace, namespace_summary, type_fullname, type_name, summary, member_count" &
            If(includePayload, ", payload", "")
        Dim where As String = ""

        If Not String.IsNullOrEmpty(packageId) Then
            where = $" WHERE package_id = '{esc(packageId)}'"
        End If

        Dim list As New List(Of PackageApiDocRecord)

        SyncLock sync
            Dim rs As ResultSet = query($"SELECT {columns} FROM package_api_docs{where}")

            If rs Is Nothing OrElse Not rs.IsQuery Then
                Return list
            End If

            For Each row As Object() In rs.Rows
                Dim record As PackageApiDocRecord = readApiDoc(rs.Columns, row)

                If Not String.IsNullOrEmpty(version) AndAlso
                   Not String.Equals(record.version, version, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Call list.Add(record)
            Next
        End SyncLock

        Return list
    End Function

    Private Shared Function readApiDoc(columns As List(Of String), row As Object()) As PackageApiDocRecord
        Dim record As New PackageApiDocRecord

        For i As Integer = 0 To columns.Count - 1
            Select Case columns(i).ToLowerInvariant()
                Case "id" : record.id = toLong(row(i))
                Case "package_id" : record.package_id = toStr(row(i))
                Case "version" : record.version = toStr(row(i))
                Case "namespace" : record.namespace_name = toStr(row(i))
                Case "namespace_summary" : record.namespace_summary = toStr(row(i))
                Case "type_fullname" : record.type_fullname = toStr(row(i))
                Case "type_name" : record.type_name = toStr(row(i))
                Case "summary" : record.summary = toStr(row(i))
                Case "member_count" : record.member_count = CInt(toLong(row(i)))
                Case "payload" : record.payload = toStr(row(i))
            End Select
        Next

        Return record
    End Function

#End Region
End Class

Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports Readership

''' <summary>
''' one url entry of the generated ``sitemap.xml``.
''' </summary>
Public Class SitemapEntry

    ''' <summary>
    ''' the absolute public url of the entry.
    ''' </summary>
    Public Property url As String

    ''' <summary>
    ''' the last modification time of the entry; nothing when it is unknown.
    ''' </summary>
    Public Property lastmod As Date?

    ''' <summary>
    ''' a diagnostic kind of the entry: ``page``, ``package`` or ``docs``. it is
    ''' not written into the xml (the sitemap schema has no such element), the
    ''' xsl style sheet derives the badge from the url itself.
    ''' </summary>
    Public Property kind As String
End Class

''' <summary>
''' the result of one sitemap build: the xml text together with the database
''' fingerprint it was built from, so that the caller could persist the
''' fingerprint without querying the database twice.
''' </summary>
Public Class SitemapBuild

    ''' <summary>
    ''' the complete ``sitemap.xml`` text.
    ''' </summary>
    Public Property xml As String

    ''' <summary>
    ''' the fingerprint of the document database at the build time.
    ''' </summary>
    Public Property fingerprint As String

    ''' <summary>
    ''' the number of the url entries.
    ''' </summary>
    Public Property urlCount As Integer

    ''' <summary>
    ''' the number of the api document urls.
    ''' </summary>
    Public Property docCount As Integer
End Class

''' <summary>
''' the generator of the site map: it collects the urls of the static pages of
''' the ``wwwroot`` folder, of every package detail page and of every api
''' document page (the global document index, the per package version document
''' index and every type page), then writes the sitemaps.org 0.9 xml document
''' into the scratch directory of the server.
''' 
''' the module is intentionally side effect free except for the two file writes
''' at the very end, so that the url collection could be inspected and tested
''' independently from the schema.
''' </summary>
Public Module SitemapGenerator

    ''' <summary>
    ''' the file name of the generated site map inside the scratch directory.
    ''' </summary>
    Public Const SitemapFileName As String = "sitemap.xml"

    ''' <summary>
    ''' the file name of the small state file which records the fingerprint of
    ''' the database that the current site map was built from.
    ''' </summary>
    Public Const StateFileName As String = "sitemap.state"

    ''' <summary>
    ''' the public url of the replaceable style sheet; it is referenced by the
    ''' ``xml-stylesheet`` processing instruction of the generated xml.
    ''' </summary>
    Public Const StyleSheetUrl As String = "/assets/sitemap.xsl"

    ''' <summary>
    ''' the physical path of the generated site map.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function SitemapFilePath(config As NugetConfiguration) As String
        Return Path.Combine(config.TempDirectory, SitemapFileName)
    End Function

    ''' <summary>
    ''' the physical path of the site map state file.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function StateFilePath(config As NugetConfiguration) As String
        Return Path.Combine(config.TempDirectory, StateFileName)
    End Function

    ''' <summary>
    ''' build the site map xml text from the current database content. the
    ''' packages and the document index are read exactly once and are shared by
    ''' the url collection and the fingerprint.
    ''' </summary>
    ''' <param name="config">the server configuration.</param>
    ''' <param name="store">the data access layer.</param>
    ''' <param name="now">the timestamp written into the static page entries.</param>
    ''' <returns></returns>
    Public Function Build(config As NugetConfiguration, store As NugetStore, Optional now As Date? = Nothing) As SitemapBuild
        Dim baseUrl As String = config.ResolveSitemapBaseUrl()
        Dim timestamp As Date = If(now, Date.UtcNow)

        If String.IsNullOrEmpty(baseUrl) Then
            Throw New InvalidOperationException(
                "the sitemap can not be generated because neither 'sitemap-base-url' nor 'base-url' is configured.")
        End If

        Dim packages As List(Of PackageRecord) = store.ReadAllPackages()
        Dim docs As List(Of PackageApiDocRecord) = store.ReadApiDocIndex()
        Dim entries As List(Of SitemapEntry) = Collect(config, baseUrl, packages, docs, timestamp)

        Return New SitemapBuild With {
            .xml = render(baseUrl, entries),
            .fingerprint = fingerprint(packages, docs),
            .urlCount = entries.Count,
            .docCount = entries.Where(Function(e) String.Equals(e.kind, "docs", StringComparison.Ordinal)).Count()
        }
    End Function

    ''' <summary>
    ''' collect every url which should be exposed by the site map:
    ''' the static pages of the ``wwwroot`` folder, the api document global
    ''' index, the detail page of every package and every api document page of
    ''' the database.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="baseUrl">the public base url without a trailing slash.</param>
    ''' <param name="packages">all published package versions.</param>
    ''' <param name="docs">all api document index rows (without the payload).</param>
    ''' <param name="now">the timestamp of the static page entries.</param>
    ''' <returns>the url entries, sorted by their kind.</returns>
    Public Function Collect(config As NugetConfiguration, baseUrl As String,
                            packages As List(Of PackageRecord),
                            docs As List(Of PackageApiDocRecord),
                            now As Date) As List(Of SitemapEntry)

        Dim entries As New List(Of SitemapEntry)
        Dim published As Dictionary(Of String, Date) = publishedTimes(packages)

        ' 1. the static pages of the wwwroot folder. package.html is skipped
        '    because it is parameterized and is listed once per package below.
        Call entries.AddRange(staticPages(config, baseUrl, now))

        ' 2. the global api document index page which is rendered by the server.
        Call entries.Add(New SitemapEntry With {
            .url = baseUrl & ApiDocPages.GlobalIndexPath,
            .lastmod = now,
            .kind = "docs"
        })

        ' 3. the detail page of every package (the latest version is rendered).
        Dim ids As List(Of String) = packages _
            .Select(Function(p) p.package_id) _
            .Where(Function(id) Not String.IsNullOrEmpty(id)) _
            .Distinct(StringComparer.OrdinalIgnoreCase) _
            .OrderBy(Function(id) id, StringComparer.OrdinalIgnoreCase) _
            .ToList()

        For Each id As String In ids
            Dim lastmod As Date? = Nothing
            Dim time As Date

            If published.TryGetValue(id.ToLowerInvariant(), time) Then
                lastmod = time
            End If

            Call entries.Add(New SitemapEntry With {
                .url = baseUrl & "/package.html?id=" & Uri.EscapeDataString(id),
                .lastmod = lastmod,
                .kind = "package"
            })
        Next

        ' 4. every api document page of the database: the per package version
        '    document index and every type content page.
        Dim groups = docs _
            .Where(Function(r) r IsNot Nothing AndAlso Not String.IsNullOrEmpty(r.type_fullname)) _
            .GroupBy(Function(r) docGroupKey(r))

        For Each group In groups
            Dim first As PackageApiDocRecord = group.First()
            Dim lastmod As Date? = Nothing
            Dim time As Date

            If published.TryGetValue(first.package_id.ToLowerInvariant() & "|" & If(first.version, "").ToLowerInvariant(), time) Then
                lastmod = time
            Else
                lastmod = now
            End If

            Call entries.Add(New SitemapEntry With {
                .url = baseUrl & ApiDocPages.PackageIndexUrl(first.package_id, first.version),
                .lastmod = lastmod,
                .kind = "docs"
            })

            For Each record In group.OrderBy(Function(r) r.type_fullname, StringComparer.OrdinalIgnoreCase)
                Call entries.Add(New SitemapEntry With {
                    .url = baseUrl & DocUrls.NugetTypeUrl("", record.package_id, record.version, record.type_fullname),
                    .lastmod = lastmod,
                    .kind = "docs"
                })
            Next
        Next

        Return entries
    End Function

    ''' <summary>
    ''' the fingerprint of the document database: it changes whenever a package
    ''' is published or removed or an api document is stored, so that the site
    ''' map can be rebuilt after a restart.
    ''' </summary>
    ''' <param name="store"></param>
    ''' <returns></returns>
    Public Function Fingerprint(store As NugetStore) As String
        Return fingerprint(store.ReadAllPackages(), store.ReadApiDocIndex())
    End Function

    ''' <summary>
    ''' the fingerprint recorded by the latest successful generation, or an empty
    ''' string when the state file is missing or unreadable.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function ReadRecordedFingerprint(config As NugetConfiguration) As String
        Try
            Dim path As String = StateFilePath(config)

            If Not File.Exists(path) Then
                Return ""
            End If

            For Each line As String In File.ReadAllLines(path)
                Dim text As String = If(line, "").Trim()

                If text.StartsWith("fingerprint=", StringComparison.OrdinalIgnoreCase) Then
                    Return text.Substring("fingerprint=".Length).Trim()
                End If
            Next
        Catch ex As Exception
            Call $"the sitemap state file could not be read: {ex.Message}".debug()
        End Try

        Return ""
    End Function

    ''' <summary>
    ''' generate the site map and write it into the scratch directory together
    ''' with its fingerprint. both files are replaced atomically, so that a
    ''' concurrent reader could never observe a half written document.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="store"></param>
    ''' <param name="now"></param>
    ''' <returns>the build result which was written.</returns>
    Public Function Generate(config As NugetConfiguration, store As NugetStore, Optional now As Date? = Nothing) As SitemapBuild
        ' note: the local variable must not be named after the Build function,
        ' visual basic is case insensitive and the name would shadow it.
        Dim result As SitemapBuild = Build(config, store, now)
        Dim timestamp As Date = If(now, Date.UtcNow)

        Call Directory.CreateDirectory(config.TempDirectory)
        Call writeAtomic(SitemapFilePath(config), result.xml)
        Call writeAtomic(StateFilePath(config),
            "fingerprint=" & result.fingerprint & vbCrLf &
            "generated=" & formatTime(timestamp) & vbCrLf &
            "urls=" & result.urlCount.ToString(CultureInfo.InvariantCulture) & vbCrLf)

        Return result
    End Function

    ''' <summary>
    ''' read the generated site map, or nothing when it does not exist yet.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function ReadXml(config As NugetConfiguration) As String
        Try
            Dim path As String = SitemapFilePath(config)

            If Not File.Exists(path) Then
                Return Nothing
            End If

            Return File.ReadAllText(path, Encoding.UTF8)
        Catch ex As Exception
            Call $"the sitemap file could not be read: {ex.Message}".debug()
            Return Nothing
        End Try
    End Function

#Region "url collection helpers"

    ''' <summary>
    ''' the ``*.html`` files of the ``wwwroot`` folder; ``index.html`` becomes the
    ''' site root and ``package.html`` is excluded because it is listed once per
    ''' package.
    ''' </summary>
    Private Function staticPages(config As NugetConfiguration, baseUrl As String, now As Date) As List(Of SitemapEntry)
        Dim entries As New List(Of SitemapEntry)

        If String.IsNullOrEmpty(config.Wwwroot) OrElse Not Directory.Exists(config.Wwwroot) Then
            Return entries
        End If

        Dim pages = Directory.EnumerateFiles(config.Wwwroot, "*.html", SearchOption.TopDirectoryOnly) _
            .Where(Function(file) Not Path.GetFileName(file).Equals("package.html", StringComparison.OrdinalIgnoreCase)) _
            .OrderBy(Function(file) If(Path.GetFileName(file).Equals("index.html", StringComparison.OrdinalIgnoreCase), 0, 1)) _
            .ThenBy(Function(file) Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) _
            .ToList()

        ' note: the loop variable must not be named 'file', it would shadow the
        ' System.IO.File class of the body below.
        For Each page As String In pages
            Dim name As String = Path.GetFileName(page)
            Dim relative As String = If(name.Equals("index.html", StringComparison.OrdinalIgnoreCase), "", name)
            Dim lastmod As Date? = now

            Try
                lastmod = File.GetLastWriteTimeUtc(page)
            Catch ex As Exception
                Call $"the last write time of '{page}' could not be read: {ex.Message}".debug()
            End Try

            Call entries.Add(New SitemapEntry With {
                .url = baseUrl & "/" & relative,
                .lastmod = lastmod,
                .kind = "page"
            })
        Next

        Return entries
    End Function

    ''' <summary>
    ''' the latest published time of every package id, keyed by the lower-case
    ''' package id, and the published time of every package version, keyed by
    ''' ``id|version``.
    ''' </summary>
    Private Function publishedTimes(packages As List(Of PackageRecord)) As Dictionary(Of String, Date)
        Dim times As New Dictionary(Of String, Date)(StringComparer.OrdinalIgnoreCase)

        For Each p As PackageRecord In If(packages, New List(Of PackageRecord))
            If p Is Nothing OrElse String.IsNullOrEmpty(p.package_id) Then
                Continue For
            End If

            Dim key As String = p.package_id.ToLowerInvariant()
            Dim time As Date

            If Not times.TryGetValue(key, time) OrElse p.published > time Then
                times(key) = p.published
            End If

            key = key & "|" & If(p.version, "").ToLowerInvariant()

            If Not times.TryGetValue(key, time) OrElse p.published > time Then
                times(key) = p.published
            End If
        Next

        Return times
    End Function

    ''' <summary>
    ''' the grouping key of one api document row: the package id and the version.
    ''' </summary>
    Private Function docGroupKey(record As PackageApiDocRecord) As String
        Return If(record.package_id, "").ToLowerInvariant() & "|" & If(record.version, "").ToLowerInvariant()
    End Function

    ''' <summary>
    ''' the fingerprint of the given package / document data.
    ''' </summary>
    Private Function fingerprint(packages As List(Of PackageRecord), docs As List(Of PackageApiDocRecord)) As String
        packages = If(packages, New List(Of PackageRecord))
        docs = If(docs, New List(Of PackageApiDocRecord))

        Dim maxPackage As Long = If(packages.Count = 0, 0, packages.Max(Function(p) p.id))
        Dim maxDoc As Long = If(docs.Count = 0, 0, docs.Max(Function(d) d.id))
        Dim published As String = ""

        If packages.Count > 0 Then
            published = formatTime(packages.Max(Function(p) p.published))
        End If

        Return $"docs={docs.Count};max-doc={maxDoc};packages={packages.Count};max-package={maxPackage};published={published}"
    End Function

#End Region

#Region "xml rendering"

    ''' <summary>
    ''' render the sitemaps.org 0.9 xml document of the given entries.
    ''' </summary>
    Private Function render(baseUrl As String, entries As List(Of SitemapEntry)) As String
        Dim sb As New StringBuilder(entries.Count * 96 + 256)

        Call sb.AppendLine("<?xml version=""1.0"" encoding=""utf-8""?>")
        Call sb.AppendLine($"<?xml-stylesheet type=""text/xsl"" href=""{escapeXml(StyleSheetUrl)}""?>")
        Call sb.AppendLine("<urlset xmlns=""http://www.sitemaps.org/schemas/sitemap/0.9"">")

        For Each entry As SitemapEntry In entries
            Call sb.AppendLine("  <url>")
            Call sb.AppendLine($"    <loc>{escapeXml(entry.url)}</loc>")

            If entry.lastmod.HasValue Then
                Call sb.AppendLine($"    <lastmod>{formatTime(entry.lastmod.Value)}</lastmod>")
            End If

            Call sb.AppendLine("  </url>")
        Next

        Call sb.AppendLine("</urlset>")

        Return sb.ToString()
    End Function

    ''' <summary>
    ''' format a timestamp as a w3c datetime in utc.
    ''' </summary>
    Private Function formatTime(value As Date) As String
        If value = Date.MinValue Then
            Return ""
        End If

        Return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
    End Function

    ''' <summary>
    ''' escape a text for an xml text node / attribute value.
    ''' </summary>
    Private Function escapeXml(text As String) As String
        If String.IsNullOrEmpty(text) Then
            Return ""
        End If

        Dim sb As New StringBuilder(text.Length + 16)

        For Each c As Char In text
            Select Case c
                Case "&"c : Call sb.Append("&amp;")
                Case "<"c : Call sb.Append("&lt;")
                Case ">"c : Call sb.Append("&gt;")
                Case """"c : Call sb.Append("&quot;")
                Case "'"c : Call sb.Append("&apos;")
                Case Else : Call sb.Append(c)
            End Select
        Next

        Return sb.ToString()
    End Function

#End Region

#Region "file helpers"

    ''' <summary>
    ''' write a text file by replacing it atomically: the content is first
    ''' written into a temporary sibling file and is then moved over the target.
    ''' </summary>
    ''' <param name="path"></param>
    ''' <param name="content"></param>
    Private Sub writeAtomic(path As String, content As String)
        Dim temp As String = path & ".tmp"

        Call File.WriteAllText(temp, content, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
        Call File.Move(temp, path, overwrite:=True)
    End Sub

#End Region
End Module

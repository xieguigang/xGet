Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports Readership

''' <summary>
''' one cached package version: the package id and the version which the folder
''' ``&lt;docs-root&gt;/&lt;id&gt;/&lt;version&gt;`` belongs to.
''' </summary>
Public Class DocCacheEntry
    Public Property package_id As String
    Public Property version As String
End Class

''' <summary>
''' the manifest of one generated package version folder. it records which type
''' pages were written, so that the reconciliation pass can tell a complete
''' folder from a partial one without parsing html.
''' </summary>
Public Class DocPageManifest
    ''' <summary>the format version of the manifest itself.</summary>
    Public Property format As Integer

    Public Property package_id As String
    Public Property version As String

    ''' <summary>the full names of the types whose page was written.</summary>
    Public Property types As New List(Of String)

    ''' <summary>the utc timestamp of the generation.</summary>
    Public Property generated As String
End Class

''' <summary>
''' the static html cache of the api document pages.
''' 
''' the pages are written below the web root (``{wwwroot}/docs`` by default) so
''' that nginx can answer ``/docs/{id}/{version}/{type}.html`` with a plain file:
''' the folder tree mirrors the public url one to one. the module is shared by
''' the api document daemon (which fills the cache) and the server (which only
''' reads it during the reconciliation of the daemon, and falls back to the
''' dynamic renderer when a file is still missing).
''' </summary>
''' <remarks>
''' the file names use the **decoded** package id / version / type full name,
''' while the urls use <see cref="Uri.EscapeDataString"/>. that is intentional:
''' nginx decodes the request uri before it maps it onto the file system, so
''' ``/docs/pkg/1.0/Type%2BName.html`` must be stored as ``Type+Name.html``.
''' the generation and the reconciliation must therefore always share the
''' helpers of this module instead of building a path by hand.
''' </remarks>
Public Module DocPageCache

    ''' <summary>the file name of the per package document index page.</summary>
    Public Const IndexFileName As String = "index.html"

    ''' <summary>the file name of the per package generation manifest.</summary>
    Public Const ManifestFileName As String = "_manifest.json"

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .WriteIndented = True,
        .PropertyNameCaseInsensitive = True
    }

#Region "path mapping"

    ''' <summary>
    ''' the physical root folder of the generated document pages, example as
    ''' ``{wwwroot}/docs``.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function PageRoot(config As NugetConfiguration) As String
        Return If(config?.DocsStaticDirectory, "")
    End Function

    ''' <summary>
    ''' the physical path of the global cross package document index page.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function GlobalIndexFilePath(config As NugetConfiguration) As String
        Dim root As String = PageRoot(config)

        If String.IsNullOrEmpty(root) Then
            Return Nothing
        End If

        Return Path.Combine(root, IndexFileName)
    End Function

    ''' <summary>
    ''' the physical folder of one package version, or <c>Nothing</c> when the
    ''' package id / version can not be used as a folder name.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function VersionDirectory(config As NugetConfiguration, packageId As String, version As String) As String
        Dim root As String = PageRoot(config)
        Dim id As String = safeSegment(packageId)
        Dim ver As String = safeSegment(version)

        If String.IsNullOrEmpty(root) OrElse id Is Nothing OrElse ver Is Nothing Then
            Return Nothing
        End If

        Return Path.Combine(root, id, ver)
    End Function

    ''' <summary>
    ''' the physical path of the document index page of one package version.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function VersionIndexFilePath(config As NugetConfiguration, packageId As String, version As String) As String
        Dim folder As String = VersionDirectory(config, packageId, version)

        If folder Is Nothing Then
            Return Nothing
        End If

        Return Path.Combine(folder, IndexFileName)
    End Function

    ''' <summary>
    ''' the physical path of the content page of one type, or <c>Nothing</c> when
    ''' the type full name can not be used as a file name.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="typeFullName"></param>
    ''' <returns></returns>
    Public Function TypeFilePath(config As NugetConfiguration, packageId As String,
                                 version As String, typeFullName As String) As String

        Dim folder As String = VersionDirectory(config, packageId, version)
        Dim name As String = fileNameOf(typeFullName)

        If folder Is Nothing OrElse name Is Nothing Then
            Return Nothing
        End If

        Return Path.Combine(folder, name & ".html")
    End Function

    ''' <summary>
    ''' test whether the given name can be used as one path segment. the package
    ''' id / version come from the database, so this is a defensive check against
    ''' an unexpected value (a path traversal attempt can never escape the root).
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns></returns>
    Private Function safeSegment(name As String) As String
        If String.IsNullOrEmpty(name) Then
            Return Nothing
        End If

        Dim text As String = name.Trim()

        If text.Length = 0 OrElse text = "." OrElse text = ".." Then
            Return Nothing
        End If

        If text.Contains("/"c) OrElse text.Contains("\"c) Then
            Return Nothing
        End If

        If text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then
            Return Nothing
        End If

        Return text
    End Function

    ''' <summary>
    ''' the file name (without the extension) of a type content page.
    ''' </summary>
    ''' <param name="typeFullName"></param>
    ''' <returns></returns>
    Private Function fileNameOf(typeFullName As String) As String
        If String.IsNullOrEmpty(typeFullName) Then
            Return Nothing
        End If

        Dim name As String = typeFullName.Trim()

        ' the per package index page owns the name index.html; a type of the
        ' global namespace called "index" would collide with it on the
        ' case-insensitive file systems of windows and macOS.
        If name.Equals("index", StringComparison.OrdinalIgnoreCase) Then
            Call $"the type '{typeFullName}' collides with the document index page and is not cached as a static page".warning()
            Return Nothing
        End If

        If name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then
            Call $"the type full name '{typeFullName}' contains a character which is not valid in a file name, " &
                 "its page is not cached as a static page".warning()
            Return Nothing
        End If

        Return name
    End Function

#End Region

#Region "generation"

    ''' <summary>
    ''' render and cache every page of one package version: the package document
    ''' index page and one content page per type. an existing folder is rewritten
    ''' in place (every file is replaced atomically) and the pages which are not
    ''' part of the current generation are removed, so a re-upload never leaves a
    ''' stale type page behind.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="records">the document rows of exactly this package version.</param>
    ''' <param name="versions">every version of the package which has documents.</param>
    ''' <returns>the number of the written pages.</returns>
    Public Function WritePackageVersion(config As NugetConfiguration, packageId As String, version As String,
                                        records As IList(Of PackageApiDocRecord),
                                        versions As IEnumerable(Of String)) As Integer

        If records Is Nothing OrElse records.Count = 0 Then
            Return 0
        End If

        Dim folder As String = VersionDirectory(config, packageId, version)

        If folder Is Nothing Then
            Call $"the document pages of '{packageId} {version}' can not be cached: the folder name is invalid".warning()
            Return 0
        End If

        Dim document As ApiDocDocument = ApiDocPages.BuildPackageDocument(records, packageId, version)

        If document?.types Is Nothing OrElse document.types.Count = 0 Then
            Return 0
        End If

        Call DocUrls.ApplyNugetScheme(document)
        Call Directory.CreateDirectory(folder)

        Dim expected As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim pages As Integer = 0
        Dim indexHtml As String = ApiDocPages.RenderPackageIndexFromDocument(document, packageId, version, versions, config)

        If Not String.IsNullOrEmpty(indexHtml) Then
            Call writeAtomic(Path.Combine(folder, IndexFileName), indexHtml)
            Call expected.Add(IndexFileName)
            pages += 1
        End If

        For Each t As ApiDocType In document.types
            Dim name As String = fileNameOf(t.fullName)

            If name Is Nothing Then
                Continue For
            End If

            Dim html As String = ApiDocPages.RenderTypePageFromDocument(document, t, packageId, version, config)

            If String.IsNullOrEmpty(html) Then
                Continue For
            End If

            Call writeAtomic(Path.Combine(folder, name & ".html"), html)
            Call expected.Add(name & ".html")
            pages += 1
        Next

        ' drop the pages of the types which the current document no longer has
        For Each file As String In Directory.EnumerateFiles(folder, "*.html")
            If Not expected.Contains(Path.GetFileName(file)) Then
                Call deleteFile(file)
            End If
        Next

        Call writeManifest(folder, packageId, version, expected)

        Return pages
    End Function

    ''' <summary>
    ''' rebuild the global cross package document index page.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="records">the type index rows of every package version.</param>
    ''' <returns>whether a page was written.</returns>
    Public Function WriteGlobalIndex(config As NugetConfiguration,
                                     records As IEnumerable(Of PackageApiDocRecord)) As Boolean

        Dim path As String = GlobalIndexFilePath(config)

        If path Is Nothing Then
            Return False
        End If

        Dim html As String = ApiDocPages.RenderGlobalIndex(records, config)

        If String.IsNullOrEmpty(html) Then
            Return False
        End If

        Call Directory.CreateDirectory(Path.GetDirectoryName(path))
        Call writeAtomic(path, html)

        Return True
    End Function

#End Region

#Region "reconciliation"

    ''' <summary>
    ''' test whether the static cache of one package version is complete: the
    ''' index page and every type page of the (database) document must exist and
    ''' the manifest must list exactly the expected types.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="typeFullNames">the type full names of the database rows.</param>
    ''' <returns></returns>
    Public Function IsUpToDate(config As NugetConfiguration, packageId As String, version As String,
                               typeFullNames As IEnumerable(Of String)) As Boolean

        Dim folder As String = VersionDirectory(config, packageId, version)

        If folder Is Nothing OrElse Not File.Exists(Path.Combine(folder, IndexFileName)) Then
            Return False
        End If

        Dim manifest As DocPageManifest = readManifest(folder)

        If manifest Is Nothing OrElse manifest.types Is Nothing Then
            Return False
        End If

        Dim expected As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) ' the cacheable full names
        Dim actual As New HashSet(Of String)(manifest.types, StringComparer.OrdinalIgnoreCase)

        For Each name As String In If(typeFullNames, Enumerable.Empty(Of String)())
            If fileNameOf(name) IsNot Nothing Then
                Call expected.Add(name)
            End If
        Next

        If Not expected.SetEquals(actual) Then
            Return False
        End If

        ' the manifest could survive a manual cleanup of the folder
        For Each name As String In expected
            If Not File.Exists(Path.Combine(folder, name & ".html")) Then
                Return False
            End If
        Next

        Return True
    End Function

    ''' <summary>
    ''' delete the whole static cache of one package id (every version).
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <returns>the number of the removed version folders.</returns>
    Public Function RemovePackage(config As NugetConfiguration, packageId As String) As Integer
        Dim root As String = PageRoot(config)
        Dim id As String = safeSegment(packageId)

        If String.IsNullOrEmpty(root) OrElse id Is Nothing Then
            Return 0
        End If

        Dim folder As String = Path.Combine(root, id)

        If Not Directory.Exists(folder) Then
            Return 0
        End If

        Dim count As Integer = Directory.EnumerateDirectories(folder).Count()

        Call deleteDirectory(folder)

        Return count
    End Function

    ''' <summary>
    ''' delete the static cache of one package version.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns>whether a folder was removed.</returns>
    Public Function RemoveVersion(config As NugetConfiguration, packageId As String, version As String) As Boolean
        Dim folder As String = VersionDirectory(config, packageId, version)

        If folder Is Nothing OrElse Not Directory.Exists(folder) Then
            Return False
        End If

        Call deleteDirectory(folder)

        ' keep the package folder tidy once its last version is gone
        Try
            Dim parent As String = Path.GetDirectoryName(folder)

            If Directory.Exists(parent) AndAlso Not Directory.EnumerateFileSystemEntries(parent).Any() Then
                Call Directory.Delete(parent)
            End If
        Catch
        End Try

        Return True
    End Function

    ''' <summary>
    ''' the package versions which actually exist in the static cache folder.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function EnumerateCache(config As NugetConfiguration) As List(Of DocCacheEntry)
        Dim result As New List(Of DocCacheEntry)
        Dim root As String = PageRoot(config)

        If String.IsNullOrEmpty(root) OrElse Not Directory.Exists(root) Then
            Return result
        End If

        For Each packageFolder As String In Directory.EnumerateDirectories(root)
            For Each versionFolder As String In Directory.EnumerateDirectories(packageFolder)
                If Not File.Exists(Path.Combine(versionFolder, IndexFileName)) Then
                    Continue For
                End If

                Call result.Add(New DocCacheEntry With {
                    .package_id = Path.GetFileName(packageFolder),
                    .version = Path.GetFileName(versionFolder)
                })
            Next
        Next

        Return result
    End Function

#End Region

#Region "manifest and file helpers"

    Private Function manifestPath(folder As String) As String
        Return Path.Combine(folder, ManifestFileName)
    End Function

    Private Sub writeManifest(folder As String, packageId As String, version As String, files As IEnumerable(Of String))
        Dim manifest As New DocPageManifest With {
            .format = 1,
            .package_id = packageId,
            .version = version,
            .generated = Date.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
            .types = files _
                .Where(Function(name) Not name.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase)) _
                .Select(Function(name) name.Substring(0, name.Length - ".html".Length)) _
                .OrderBy(Function(name) name, StringComparer.OrdinalIgnoreCase) _
                .ToList()
        }

        Call writeAtomic(manifestPath(folder), JsonSerializer.Serialize(manifest, JsonOptions))
    End Sub

    Private Function readManifest(folder As String) As DocPageManifest
        Dim path As String = manifestPath(folder)

        If Not File.Exists(path) Then
            Return Nothing
        End If

        Try
            Return JsonSerializer.Deserialize(Of DocPageManifest)(File.ReadAllText(path), JsonOptions)
        Catch ex As Exception
            Call $"the document page manifest '{path}' could not be read: {ex.Message}".debug()
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' write a text file by replacing it atomically, so that a concurrent nginx
    ''' reader never sees a half written page.
    ''' </summary>
    ''' <param name="path"></param>
    ''' <param name="content"></param>
    Private Sub writeAtomic(path As String, content As String)
        Dim temp As String = path & ".tmp"

        Call File.WriteAllText(temp, content, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
        Call File.Move(temp, path, overwrite:=True)
    End Sub

    Private Sub deleteFile(path As String)
        Try
            Call File.Delete(path)
        Catch ex As Exception
            Call $"the stale document page '{path}' could not be removed: {ex.Message}".warning()
        End Try
    End Sub

    Private Sub deleteDirectory(path As String)
        Try
            Call Directory.Delete(path, recursive:=True)
        Catch ex As Exception
            Call $"the document page folder '{path}' could not be removed: {ex.Message}".warning()
        End Try
    End Sub

#End Region
End Module

Imports System.Collections.Generic
Imports System.IO
Imports System.Text.RegularExpressions
Imports Nuget
Imports Readership

''' <summary>
''' the command line test of the static api document page cache (the html tree
''' which the api document daemon writes below the web root so that nginx can
''' serve it as plain files).
''' 
''' the test extracts the documents of one nupkg file in process, renders the
''' whole page tree into a temporary folder and validates:
''' 
''' + the package index page and one page per type exist;
''' + the url of every type maps onto the file which was actually written
'''   (nginx decodes the uri before it maps it to the file system);
''' + no ``{{placeholder}}`` token is left in the rendered html;
''' + every page carries the 1x1 view counter pixel;
''' + a second generation is idempotent and removes the stale pages.
''' 
''' usage: test staticdocs [nupkg] [template] [output]
''' </summary>
Module StaticDocsTest

    Public Function Run(args As String()) As Integer
        Dim nupkg As String = argAt(args, 0, "G:\xDoc\dist\bin\Nuget.1.0.0.nupkg")
        Dim template As String = argAt(args, 1, "G:\xDoc\dist\template")
        Dim output As String = argAt(args, 2, "")

        Call Console.WriteLine($"nupkg   : {nupkg}")
        Call Console.WriteLine($"template: {template}")

        If Not File.Exists(nupkg) Then
            Call Console.WriteLine("the nupkg file was not found.")
            Return 2
        End If

        Dim keep As Boolean = Not String.IsNullOrEmpty(output)

        If Not keep Then
            output = Path.Combine(Path.GetTempPath(), "xdoc-static-docs-" & Guid.NewGuid().ToString("N"))
        End If

        Call Console.WriteLine($"output  : {output}")
        Call Console.WriteLine()

        Try
            Dim metadata As NupkgMetadata = NupkgReader.ReadMetadata(nupkg)
            Dim warnings As New List(Of String)
            Dim document As ApiDocDocument = PackageApiDocs.ExtractInProcess(nupkg, warnings)

            If document Is Nothing Then
                Call Console.WriteLine("the api comment documents could not be extracted.")
                Return 2
            End If

            Dim records As List(Of PackageApiDocRecord) = PackageApiDocs.ToRecords(document, metadata.Id, metadata.Version)

            Call Console.WriteLine($"extracted: {records.Count} type document(s), {warnings.Count} warning(s)")

            If records.Count = 0 Then
                Call Console.WriteLine("the package ships no api comment document.")
                Return 2
            End If

            Dim config As NugetConfiguration = NugetConfiguration.FromConfig(
                New Dictionary(Of String, String) From {
                    {"template", template},
                    {"docs-static-output", output},
                    {"doc-view-pixel", "true"}
                })

            Dim versions As New List(Of String) From {metadata.Version}
            Dim pages As Integer = DocPageCache.WritePackageVersion(
                config, metadata.Id, metadata.Version, records, versions)

            Call Console.WriteLine($"written : {pages} page(s)")
            Call Console.WriteLine()

            Dim layoutOk As Boolean = checkLayout(config, metadata, records)
            Dim htmlOk As Boolean = checkPages(config, metadata, records)
            Dim urlOk As Boolean = checkUrls(config, metadata, records)
            Dim globalOk As Boolean = checkGlobalIndex(config, records)
            Dim idempotentOk As Boolean = checkIdempotent(config, metadata, records, versions)

            Dim ok As Boolean = layoutOk AndAlso htmlOk AndAlso urlOk AndAlso globalOk AndAlso idempotentOk

            Call Console.WriteLine()
            Call Console.WriteLine($"static docs validation: {If(ok, "PASS", "FAIL")}")

            Return If(ok, 0, 1)
        Finally
            If Not keep Then
                Try
                    If Directory.Exists(output) Then
                        Call Directory.Delete(output, recursive:=True)
                    End If
                Catch
                End Try
            Else
                Call Console.WriteLine()
                Call Console.WriteLine("the generated page tree was kept for the inspection.")
            End If
        End Try
    End Function

    ''' <summary>
    ''' the package folder must hold the index page, the manifest and one html
    ''' file per document row.
    ''' </summary>
    Private Function checkLayout(config As NugetConfiguration, metadata As NupkgMetadata,
                                 records As List(Of PackageApiDocRecord)) As Boolean

        Dim folder As String = DocPageCache.VersionDirectory(config, metadata.Id, metadata.Version)
        Dim index As String = Path.Combine(folder, DocPageCache.IndexFileName)
        Dim manifest As String = Path.Combine(folder, DocPageCache.ManifestFileName)
        Dim files As String() = Directory.GetFiles(folder, "*.html")

        Dim indexOk As Boolean = File.Exists(index)
        Dim manifestOk As Boolean = File.Exists(manifest)
        Dim countOk As Boolean = files.Length = records.Count + 1

        Call Console.WriteLine($"layout  : folder={folder}")
        Call Console.WriteLine($"          index.html={indexOk}, manifest={manifestOk}, html files={files.Length} (expected {records.Count + 1})")

        Return indexOk AndAlso manifestOk AndAlso countOk
    End Function

    ''' <summary>
    ''' every page must be complete (no unresolved placeholder), carry the page
    ''' shell and the counter pixel.
    ''' </summary>
    Private Function checkPages(config As NugetConfiguration, metadata As NupkgMetadata,
                                records As List(Of PackageApiDocRecord)) As Boolean

        Dim folder As String = DocPageCache.VersionDirectory(config, metadata.Id, metadata.Version)
        Dim ok As Boolean = True
        Dim checked As Integer = 0

        ' note: the loop variable must not be named 'file', it would shadow the
        ' System.IO.File class of the body.
        For Each page As String In Directory.GetFiles(folder, "*.html")
            Dim html As String = File.ReadAllText(page)

            ' only the structural placeholders must be resolved; a {{token}} which
            ' is a part of the package documentation itself is legitimate content.
            Dim unresolved As Integer = Regex.Matches(
                html, "\{\{(base|sidebar|breadcrumb|site_title|page_kind)\}\}").Count

            Dim shell As Boolean = html.Contains("doc-shell")
            Dim pixel As Boolean = html.Contains("/api/doc-view/")

            If unresolved <> 0 OrElse Not shell OrElse Not pixel Then
                Call Console.WriteLine($"  {Path.GetFileName(page)}: shell={shell}, pixel={pixel}, unresolved={unresolved}")
                ok = False
            End If

            checked += 1
        Next

        Call Console.WriteLine($"pages   : {checked} page(s) checked, complete={ok}")

        Return ok
    End Function

    ''' <summary>
    ''' the public url of a type escapes the id / version / full name, while nginx
    ''' decodes the uri back before it maps it onto the file system. the test
    ''' verifies that this decode lands exactly on the generated file.
    ''' </summary>
    Private Function checkUrls(config As NugetConfiguration, metadata As NupkgMetadata,
                               records As List(Of PackageApiDocRecord)) As Boolean

        Dim root As String = Path.GetFullPath(DocPageCache.PageRoot(config))
        Dim ok As Boolean = True

        ' the document root is served as the "/docs" url prefix, so the url path
        ' below that prefix is the file path below the root folder
        Const UrlPrefix As String = "/docs/"

        For Each record As PackageApiDocRecord In records
            Dim url As String = DocUrls.NugetTypeUrl("", metadata.Id, metadata.Version, record.type_fullname)

            If Not url.StartsWith(UrlPrefix, StringComparison.Ordinal) Then
                Call Console.WriteLine($"  unexpected type url: {url}")
                ok = False
                Exit For
            End If

            ' nginx: the request path is decoded, then appended to the doc root
            Dim decoded As String = Uri.UnescapeDataString(url.Substring(UrlPrefix.Length))
            Dim physical As String = Path.GetFullPath(Path.Combine(root, decoded))

            If Not File.Exists(physical) Then
                Call Console.WriteLine($"  url -> file mismatch: {url}")
                Call Console.WriteLine($"                        {physical}")
                ok = False
                Exit For
            End If
        Next

        Call Console.WriteLine($"urls    : id/version/type url -> file mapping symmetric={ok}")

        Return ok
    End Function

    ''' <summary>
    ''' the cross package index page must be written at the document root.
    ''' </summary>
    Private Function checkGlobalIndex(config As NugetConfiguration, records As List(Of PackageApiDocRecord)) As Boolean
        Dim ok As Boolean = DocPageCache.WriteGlobalIndex(config, records)
        Dim path As String = DocPageCache.GlobalIndexFilePath(config)
        Dim exists As Boolean = ok AndAlso File.Exists(path)
        Dim hasText As Boolean = exists AndAlso File.ReadAllText(path).Contains(records(0).package_id)

        Call Console.WriteLine($"global  : index.html written={exists}, contains '{records(0).package_id}'={hasText}")

        Return exists AndAlso hasText
    End Function

    ''' <summary>
    ''' a second generation must be a no op for the file set and it must remove the
    ''' pages which the current document no longer has.
    ''' </summary>
    Private Function checkIdempotent(config As NugetConfiguration, metadata As NupkgMetadata,
                                     records As List(Of PackageApiDocRecord), versions As List(Of String)) As Boolean

        Dim folder As String = DocPageCache.VersionDirectory(config, metadata.Id, metadata.Version)
        Dim stale As String = Path.Combine(folder, "Stale.Removed.Type.html")
        Dim before As Integer = Directory.GetFiles(folder, "*.html").Length

        Call File.WriteAllText(stale, "<html><body>stale</body></html>")

        Dim pages As Integer = DocPageCache.WritePackageVersion(config, metadata.Id, metadata.Version, records, versions)
        Dim after As Integer = Directory.GetFiles(folder, "*.html").Length
        Dim removed As Boolean = Not File.Exists(stale)
        Dim stable As Boolean = after = before

        Call Console.WriteLine($"rerun   : pages={pages}, files before={before}, after={after}, stale removed={removed}")

        Return stable AndAlso removed
    End Function

    Private Function argAt(args As String(), index As Integer, fallback As String) As String
        If args IsNot Nothing AndAlso args.Length > index AndAlso Not String.IsNullOrWhiteSpace(args(index)) Then
            Return args(index)
        End If

        Return fallback
    End Function
End Module

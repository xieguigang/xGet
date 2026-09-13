Imports System.Collections.Generic
Imports System.IO
Imports System.Text.RegularExpressions
Imports Nuget

''' <summary>
''' The command line test of the nuget server side api document pages. It
''' extracts the documents of one nupkg file, stores them into a temporary
''' database, rebuilds the document model from the stored rows and renders the
''' three document pages from the template files, without starting the http
''' server.
''' 
''' usage: test serverdocs [nupkg] [template]
''' </summary>
Module ServerDocsTest

    Public Function Run(args As String()) As Integer
        Dim nupkg As String = argAt(args, 0, "G:\xDoc\dist\bin\Nuget.1.0.0.nupkg")
        Dim template As String = argAt(args, 1, "G:\xDoc\dist\template")

        Call Console.WriteLine($"nupkg   : {nupkg}")
        Call Console.WriteLine($"template: {template}")
        Call Console.WriteLine()

        If Not File.Exists(nupkg) Then
            Call Console.WriteLine("the nupkg file was not found.")
            Return 2
        End If

        Dim metadata As NupkgMetadata = NupkgReader.ReadMetadata(nupkg)
        Dim warnings As New List(Of String)
        Dim records As List(Of PackageApiDocRecord) = PackageApiDocs.Extract(nupkg, metadata.Id, metadata.Version, warnings)

        Call Console.WriteLine($"extracted: {records.Count} type document(s), {warnings.Count} warning(s)")

        For Each message As String In warnings
            Call Console.WriteLine($"  warning: {message}")
        Next

        If records.Count = 0 Then
            Call Console.WriteLine("no api document was extracted from the package.")
            Return 2
        End If

        Dim tempDb As String = Path.Combine(Path.GetTempPath(), "xdoc-docs-test-" & Guid.NewGuid().ToString("N"))

        Try
            Dim store As New NugetStore(tempDb)
            Call store.ReplacePackageApiDocs(metadata.Id, metadata.Version, records)

            Call Console.WriteLine($"stored   : {store.ReadPackageApiDocIndex(metadata.Id, metadata.Version).Count} index row(s)")
            Call Console.WriteLine($"versions : {String.Join(", ", store.GetPackageApiDocVersions(metadata.Id))}")

            Dim config As NugetConfiguration = NugetConfiguration.FromConfig(
                New Dictionary(Of String, String) From {{"template", template}})

            Dim globalHtml As String = ApiDocPages.RenderGlobalIndex(store, config)
            Dim packageHtml As String = ApiDocPages.RenderPackageIndex(store, config, metadata.Id, metadata.Version)

            Dim sample As PackageApiDocRecord = records _
                .OrderByDescending(Function(r) r.member_count) _
                .First()
            Dim typeHtml As String = ApiDocPages.RenderTypePage(store, config, metadata.Id, metadata.Version, sample.type_fullname)

            Call Console.WriteLine()
            Call Console.WriteLine("rendered pages:")

            Dim globalOk As Boolean = checkHtml("global index ", globalHtml, metadata.Id)
            Dim packageOk As Boolean = checkHtml("package index", packageHtml, metadata.Id)
            Dim typeOk As Boolean = checkHtml("type page    ", typeHtml, sample.type_fullname)

            Dim ok As Boolean = globalOk AndAlso packageOk AndAlso typeOk

            Call Console.WriteLine()
            Call Console.WriteLine($"server docs validation: {If(ok, "PASS", "FAIL")}")

            Return If(ok, 0, 1)
        Finally
            Try
                If Directory.Exists(tempDb) Then
                    Call Directory.Delete(tempDb, recursive:=True)
                End If
            Catch
            End Try
        End Try
    End Function

    ''' <summary>
    ''' a rendered document page must contain the page shell, the expected text
    ''' and it must have no unresolved ``{{placeholder}}`` token.
    ''' </summary>
    Private Function checkHtml(label As String, html As String, expected As String) As Boolean
        If String.IsNullOrEmpty(html) Then
            Call Console.WriteLine($"  {label}: empty")
            Return False
        End If

        Dim unresolved As Integer = Regex.Matches(html, "\{\{[^}]*\}\}").Count
        Dim hasShell As Boolean = html.Contains("doc-shell")
        Dim hasText As Boolean = Not String.IsNullOrEmpty(expected) AndAlso html.Contains(expected)

        Call Console.WriteLine($"  {label}: {html.Length} bytes, shell={hasShell}, contains '{expected}'={hasText}, unresolved={unresolved}")

        Return hasShell AndAlso hasText AndAlso unresolved = 0
    End Function

    Private Function argAt(args As String(), index As Integer, fallback As String) As String
        If args IsNot Nothing AndAlso args.Length > index AndAlso Not String.IsNullOrWhiteSpace(args(index)) Then
            Return args(index)
        End If

        Return fallback
    End Function
End Module

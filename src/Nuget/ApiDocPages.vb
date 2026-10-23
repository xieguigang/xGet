Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports Readership

''' <summary>
''' The server side rendering of the nuget api document pages. The document data
''' is loaded from the <c>package_api_docs</c> table, rebuilt into an
''' <see cref="ApiDocDocument"/>, rendered into page fragments by
''' <see cref="ApiDocRenderer"/> and finally composed with the replaceable
''' template file of the <c>template</c> directory.
''' </summary>
Public Module ApiDocPages

    Public Const GlobalIndexTemplate As String = "docs-index.html"
    Public Const PackageIndexTemplate As String = "docs-package.html"
    Public Const TypeTemplate As String = "docs-type.html"

    Public Const GlobalIndexPath As String = "/docs/index.html"

    Private ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ''' <summary>
    ''' the url of the global cross package document index
    ''' </summary>
    ''' <returns></returns>
    Public Function GlobalIndexUrl() As String
        Return GlobalIndexPath
    End Function

    ''' <summary>
    ''' the url of the per package document index page
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function PackageIndexUrl(packageId As String, version As String) As String
        Return "/docs/" & Uri.EscapeDataString(If(packageId, "")) &
            "/" & Uri.EscapeDataString(If(version, "")) & "/index.html"
    End Function

    ''' <summary>
    ''' render the global cross package namespace / type index page.
    ''' </summary>
    ''' <param name="store"></param>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function RenderGlobalIndex(store As NugetStore, config As NugetConfiguration) As String
        Return RenderGlobalIndex(store.ReadApiDocIndex(), config)
    End Function

    ''' <summary>
    ''' render the global index page from the given type index rows. The overload
    ''' does not touch the database, so that the api document daemon can reuse
    ''' the very same renderer as the server side fall back.
    ''' </summary>
    ''' <param name="records"></param>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function RenderGlobalIndex(records As IEnumerable(Of PackageApiDocRecord), config As NugetConfiguration) As String
        Dim document As ApiDocDocument = BuildGlobalDocument(records)

        Call DocUrls.ApplyNugetScheme(document)

        Dim ctx As DocSiteContext = context(document)
        Dim model As DocPageModel = ApiDocRenderer.GlobalIndexPage(ctx, "", GlobalIndexUrl())

        Return renderTemplate(config, GlobalIndexTemplate, model)
    End Function

    ''' <summary>
    ''' render the per package api document index page. Nothing is returned when
    ''' the package version has no api document.
    ''' </summary>
    ''' <param name="store"></param>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version">the requested version, or empty for the latest one which has documents.</param>
    ''' <returns></returns>
    Public Function RenderPackageIndex(store As NugetStore, config As NugetConfiguration,
                                       packageId As String, version As String) As String

        Dim resolvedVersion As String = resolveVersion(store, packageId, version)

        If String.IsNullOrEmpty(resolvedVersion) Then
            Return Nothing
        End If

        Dim records As List(Of PackageApiDocRecord) = store.ReadPackageApiDocs(packageId, resolvedVersion)

        Return RenderPackageIndex(records, packageId, resolvedVersion,
                                  store.GetPackageApiDocVersions(packageId), config)
    End Function

    ''' <summary>
    ''' render the per package api document index page from the given document
    ''' rows. Nothing is returned when there is no document row at all.
    ''' </summary>
    ''' <param name="records">the document rows of exactly this package version.</param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="versions">every version of the package which has documents, for the version selector.</param>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function RenderPackageIndex(records As IList(Of PackageApiDocRecord), packageId As String,
                                       version As String, versions As IEnumerable(Of String),
                                       config As NugetConfiguration) As String

        If records Is Nothing OrElse records.Count = 0 Then
            Return Nothing
        End If

        Dim document As ApiDocDocument = BuildPackageDocument(records, packageId, version)

        Call DocUrls.ApplyNugetScheme(document)

        Return RenderPackageIndexFromDocument(document, packageId, version, versions, config)
    End Function

    ''' <summary>
    ''' render the content page of one type. Nothing is returned when the package
    ''' version or the type could not be found.
    ''' </summary>
    ''' <param name="store"></param>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="typeFullName"></param>
    ''' <returns></returns>
    Public Function RenderTypePage(store As NugetStore, config As NugetConfiguration,
                                   packageId As String, version As String, typeFullName As String) As String

        Dim resolvedVersion As String = resolveVersion(store, packageId, version)

        If String.IsNullOrEmpty(resolvedVersion) OrElse String.IsNullOrEmpty(typeFullName) Then
            Return Nothing
        End If

        Dim records As List(Of PackageApiDocRecord) = store.ReadPackageApiDocs(packageId, resolvedVersion)

        Return RenderTypePage(records, packageId, resolvedVersion, typeFullName,
                              store.GetPackageApiDocVersions(packageId), config)
    End Function

    ''' <summary>
    ''' render the content page of one type from the given document rows.
    ''' </summary>
    ''' <param name="records">the document rows of exactly this package version.</param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="typeFullName"></param>
    ''' <param name="versions"></param>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function RenderTypePage(records As IList(Of PackageApiDocRecord), packageId As String,
                                   version As String, typeFullName As String,
                                   versions As IEnumerable(Of String), config As NugetConfiguration) As String

        If records Is Nothing OrElse records.Count = 0 OrElse String.IsNullOrEmpty(typeFullName) Then
            Return Nothing
        End If

        Dim document As ApiDocDocument = BuildPackageDocument(records, packageId, version)
        Dim t As ApiDocType = findType(document, typeFullName)

        If t Is Nothing Then
            Return Nothing
        End If

        Call DocUrls.ApplyNugetScheme(document)

        Return RenderTypePageFromDocument(document, t, packageId, version, config)
    End Function

    ''' <summary>
    ''' render one type page from an already built (and url scheme applied)
    ''' document. The daemon builds the document exactly once per package
    ''' version and then renders every type page through this overload, so that
    ''' a package with many types does not rebuild the model for every page.
    ''' </summary>
    ''' <param name="document"></param>
    ''' <param name="t"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <param name="config"></param>
    ''' <returns></returns>
    Public Function RenderTypePageFromDocument(document As ApiDocDocument, t As ApiDocType,
                                               packageId As String, version As String,
                                               config As NugetConfiguration) As String

        If document Is Nothing OrElse t Is Nothing Then
            Return Nothing
        End If

        Dim ctx As DocSiteContext = context(document)
        Dim model As DocPageModel = ApiDocRenderer.TypePage(
            ctx, t, "", GlobalIndexUrl(), PackageIndexUrl(packageId, version))

        model("doc_pixel") = docPixel(config, packageId, version)

        Return renderTemplate(config, TypeTemplate, model)
    End Function

    ''' <summary>
    ''' render the per package index page from an already built (and url scheme
    ''' applied) document.
    ''' </summary>
    Public Function RenderPackageIndexFromDocument(document As ApiDocDocument, packageId As String,
                                                   version As String, versions As IEnumerable(Of String),
                                                   config As NugetConfiguration) As String

        If document Is Nothing OrElse document.types Is Nothing OrElse document.types.Count = 0 Then
            Return Nothing
        End If

        Dim ctx As DocSiteContext = context(document)
        Dim model As DocPageModel = ApiDocRenderer.PackageIndexPage(
            ctx, "", GlobalIndexUrl(), PackageIndexUrl(packageId, version),
            versionSelect(packageId, versions, version))

        model("doc_pixel") = docPixel(config, packageId, version)

        Return renderTemplate(config, PackageIndexTemplate, model)
    End Function

    Private Function findType(document As ApiDocDocument, typeFullName As String) As ApiDocType
        If document?.types Is Nothing Then
            Return Nothing
        End If

        Return document.types.FirstOrDefault(
            Function(item) String.Equals(item.fullName, typeFullName, StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>
    ''' rebuild the document model of one package version from its stored rows.
    ''' </summary>
    ''' <param name="records"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Public Function BuildPackageDocument(records As IEnumerable(Of PackageApiDocRecord), packageId As String, version As String) As ApiDocDocument
        Dim document As ApiDocDocument = newDocument(packageId, version)
        Dim namespaces As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each record As PackageApiDocRecord In If(records, Enumerable.Empty(Of PackageApiDocRecord)())
            If record Is Nothing OrElse String.IsNullOrEmpty(record.type_fullname) Then
                Continue For
            End If

            Dim t As ApiDocType = deserialize(record)

            Call document.types.Add(t)

            If Not String.IsNullOrEmpty(t.namespaceName) AndAlso namespaces.Add(t.namespaceName) Then
                Call document.namespaces.Add(New ApiDocNamespace With {
                    .name = t.namespaceName,
                    .project = packageId,
                    .summary = record.namespace_summary
                })
            End If
        Next

        Return document
    End Function

    ''' <summary>
    ''' rebuild a merged document from the type index rows of every package
    ''' version. The payload is not needed by the global index.
    ''' </summary>
    ''' <param name="records"></param>
    ''' <returns></returns>
    Public Function BuildGlobalDocument(records As IEnumerable(Of PackageApiDocRecord)) As ApiDocDocument
        Dim document As ApiDocDocument = newDocument("", "")
        Dim namespaces As New Dictionary(Of String, ApiDocNamespace)(StringComparer.OrdinalIgnoreCase)

        For Each record As PackageApiDocRecord In If(records, Enumerable.Empty(Of PackageApiDocRecord)())
            If record Is Nothing OrElse String.IsNullOrEmpty(record.type_fullname) Then
                Continue For
            End If

            Call document.types.Add(New ApiDocType With {
                .fullName = record.type_fullname,
                .name = record.type_name,
                .namespaceName = record.namespace_name,
                .project = record.package_id,
                .summary = record.summary,
                .memberCountHint = record.member_count,
                .packageId = record.package_id,
                .packageVersion = record.version,
                .members = New List(Of ApiDocMember),
                .typeParams = New List(Of ApiDocParam)
            })

            If Not String.IsNullOrEmpty(record.namespace_name) AndAlso Not namespaces.ContainsKey(record.namespace_name) Then
                Dim ns As New ApiDocNamespace With {
                    .name = record.namespace_name,
                    .project = record.package_id,
                    .summary = record.namespace_summary
                }

                namespaces(record.namespace_name) = ns
                Call document.namespaces.Add(ns)
            End If
        Next

        Return document
    End Function

    Private Function deserialize(record As PackageApiDocRecord) As ApiDocType
        Dim t As ApiDocType = Nothing

        If Not String.IsNullOrEmpty(record.payload) Then
            Try
                t = JsonSerializer.Deserialize(Of ApiDocType)(record.payload, JsonOptions)
            Catch ex As Exception
                t = Nothing
            End Try
        End If

        If t Is Nothing Then
            t = New ApiDocType()
        End If

        ' the scalar columns are always authoritative for the index data
        t.fullName = record.type_fullname
        t.namespaceName = record.namespace_name
        t.packageId = record.package_id
        t.packageVersion = record.version

        If String.IsNullOrEmpty(t.name) Then
            t.name = record.type_name
        End If

        If String.IsNullOrEmpty(t.summary) Then
            t.summary = record.summary
        End If

        t.memberCountHint = record.member_count

        If t.members Is Nothing Then
            t.members = New List(Of ApiDocMember)
        End If

        If t.typeParams Is Nothing Then
            t.typeParams = New List(Of ApiDocParam)
        End If

        Return t
    End Function

    Private Function newDocument(packageId As String, version As String) As ApiDocDocument
        Dim document As ApiDocDocument = ApiDocDocument.Empty()

        document.title = "API Reference"
        document.subTitle = "nuget package documentation"
        document.description = "Server side rendered api reference documents of the uploaded nuget packages."
        document.packageId = packageId
        document.packageVersion = version

        If Not String.IsNullOrEmpty(packageId) Then
            Call document.assemblies.Add(packageId)
        End If

        Return document
    End Function

    Private Function context(document As ApiDocDocument) As DocSiteContext
        Return New DocSiteContext With {
            .Document = document,
            .Index = New ApiDocIndex(document),
            .Options = Nothing,
            .Theme = Nothing,
            .AbsoluteUrls = True
        }
    End Function

    ''' <summary>
    ''' resolve the version of a package document request; an empty version means
    ''' the latest version which has api documents.
    ''' </summary>
    ''' <param name="store"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Private Function resolveVersion(store As NugetStore, packageId As String, version As String) As String
        If Not String.IsNullOrEmpty(version) AndAlso store.HasPackageApiDocs(packageId, version) Then
            Return version
        End If

        Dim versions As List(Of String) = store.GetPackageApiDocVersions(packageId)

        If versions.Count = 0 Then
            Return Nothing
        End If

        Return versions.Last()
    End Function

    Private Function versionSelect(store As NugetStore, packageId As String, selected As String) As String
        Return versionSelect(packageId, store.GetPackageApiDocVersions(packageId), selected)
    End Function

    ''' <summary>
    ''' build the ``&lt;select&gt;`` html of the document version switcher from the
    ''' given version list, so that the api document daemon does not need a
    ''' database round trip for every page.
    ''' </summary>
    ''' <param name="packageId"></param>
    ''' <param name="versions"></param>
    ''' <param name="selected"></param>
    ''' <returns></returns>
    Private Function versionSelect(packageId As String, versions As IEnumerable(Of String), selected As String) As String
        Dim list As List(Of String) = If(versions, Enumerable.Empty(Of String)()) _
            .Where(Function(v) Not String.IsNullOrEmpty(v)) _
            .ToList()

        If list.Count <= 1 Then
            Return ""
        End If

        Dim sb As New StringBuilder

        sb.Append("<select id=""doc-version"" class=""doc-version"" aria-label=""document version"" onchange=""if(this.value){location.href=this.value;}"">")

        For Each v As String In list
            Dim url$ = PackageIndexUrl(packageId, v)
            Dim onAttr$ = If(String.Equals(v, selected, StringComparison.OrdinalIgnoreCase), " selected=""selected""", "")

            sb.Append($"<option value=""{DocHtml.Attr(url)}""{onAttr}>{DocHtml.Escape(v)}</option>")
        Next

        sb.Append("</select>")

        Return sb.ToString()
    End Function

    ''' <summary>
    ''' read the template file and fill the placeholder values. a minimal built-in
    ''' template is used when the template file is missing.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="templateName"></param>
    ''' <param name="model"></param>
    ''' <returns></returns>
    Private Function renderTemplate(config As NugetConfiguration, templateName As String, model As DocPageModel) As String
        Dim template As String = Nothing
        Dim folder As String = If(config?.TemplateDirectory, "")

        If Not String.IsNullOrEmpty(folder) Then
            Dim templateFile As String = Path.Combine(folder, templateName)

            If File.Exists(templateFile) Then
                Try
                    template = File.ReadAllText(templateFile)
                Catch ex As Exception
                    Call $"failed to read the document template '{templateFile}': {ex.Message}".warning()
                End Try
            Else
                Call $"the document template was not found: {templateFile}".warning()
            End If
        End If

        If String.IsNullOrWhiteSpace(template) Then
            template = fallbackTemplate()
        End If

        Dim html As String = DocTemplate.Render(template, model.values)

        Return injectPixel(html, model("doc_pixel"))
    End Function

    ''' <summary>
    ''' the html of the 1x1 view counter pixel of one package document page. the
    ''' page is served by nginx as a plain static file, so the daily document view
    ''' statistic is collected by this tiny request instead of the page request.
    ''' </summary>
    ''' <param name="config"></param>
    ''' <param name="packageId"></param>
    ''' <param name="version"></param>
    ''' <returns></returns>
    Private Function docPixel(config As NugetConfiguration, packageId As String, version As String) As String
        If config Is Nothing OrElse Not config.DocViewPixelEnabled Then
            Return ""
        End If

        If String.IsNullOrEmpty(packageId) Then
            Return ""
        End If

        Dim src As String = "/api/doc-view/" & Uri.EscapeDataString(packageId) &
            "/" & Uri.EscapeDataString(If(version, ""))

        Return "<img class=""doc-view-pixel"" src=""" & DocHtml.Attr(src) & """" &
            " width=""1"" height=""1"" alt="""" aria-hidden=""true"" referrerpolicy=""no-referrer""" &
            " style=""position:absolute;left:-9999px;top:0;width:1px;height:1px;opacity:0"" />"
    End Function

    ''' <summary>
    ''' make sure that a page which was composed from a user supplied template also
    ''' carries the counter pixel: the shipped templates declare the ``doc_pixel``
    ''' placeholder, a custom template may not.
    ''' </summary>
    ''' <param name="html"></param>
    ''' <param name="pixel"></param>
    ''' <returns></returns>
    Private Function injectPixel(html As String, pixel As String) As String
        If String.IsNullOrEmpty(html) OrElse String.IsNullOrEmpty(pixel) Then
            Return html
        End If

        If html.IndexOf("/api/doc-view/", StringComparison.OrdinalIgnoreCase) >= 0 Then
            Return html
        End If

        Dim index As Integer = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase)

        If index < 0 Then
            Return html & pixel
        End If

        Return html.Substring(0, index) & pixel & html.Substring(index)
    End Function

    ''' <summary>
    ''' a minimal built-in page shell which is used when no template file exists
    ''' </summary>
    ''' <returns></returns>
    Private Function fallbackTemplate() As String
        Return "<!DOCTYPE html>" & vbLf &
            "<html lang=""en""><head><meta charset=""UTF-8"" />" & vbLf &
            "<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />" & vbLf &
            "<title>{{title}} · {{site_title}}</title>" & vbLf &
            "<link rel=""stylesheet"" href=""{{base}}/assets/css/scibasic.css"" />" & vbLf &
            "<link rel=""stylesheet"" href=""{{base}}/assets/css/docs.css"" /></head>" & vbLf &
            "<body data-page=""doc""><div class=""doc-shell"">" & vbLf &
            "<aside class=""doc-side"">{{sidebar}}</aside>" & vbLf &
            "<main class=""doc-main""><div class=""wrap doc-wrap"">" & vbLf &
            "<p class=""eyebrow"">{{breadcrumb}}</p>{{content}}" & vbLf &
            "</div></main></div>{{doc_pixel}}" & vbLf &
            "<script src=""{{base}}/assets/js/docs.js""></script></body></html>"
    End Function
End Module

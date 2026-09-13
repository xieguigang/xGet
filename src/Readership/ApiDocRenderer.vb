Imports System.Text

''' <summary>
''' The placeholder values of one rendered api document page. The nuget server
''' side document pages are composed from a replaceable html template (see
''' <see cref="DocTemplate"/>) and these values.
''' </summary>
Public Class DocPageModel

    Public ReadOnly values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Default Public Property Item(key As String) As String
        Get
            Dim value As String = Nothing

            If values.TryGetValue(key, value) Then
                Return value
            End If

            Return ""
        End Get
        Set(value As String)
            values(key) = value
        End Set
    End Property

    ''' <summary>
    ''' fill a value only when it has not been set yet
    ''' </summary>
    ''' <param name="key"></param>
    ''' <param name="value"></param>
    Public Sub SetDefault(key As String, value As String)
        If Not values.ContainsKey(key) Then
            values(key) = value
        End If
    End Sub
End Class

''' <summary>
''' The page fragment renderer of the nuget server side api document pages. It
''' produces the placeholder values of the global index page, the per package
''' index page and the type content page; the actual html shell is provided by
''' the replaceable template files.
''' </summary>
Public Module ApiDocRenderer

    ''' <summary>
    ''' render the global cross package namespace / type index page.
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <param name="base">the url prefix of the site assets</param>
    ''' <param name="globalIndexUrl">the url of the global index page</param>
    ''' <returns></returns>
    Public Function GlobalIndexPage(ctx As DocSiteContext, base As String, globalIndexUrl As String) As DocPageModel
        Dim model As DocPageModel = common(ctx, base, "API Documentation")

        model("content") = globalIndexContent(ctx)
        model("sidebar") = ctx.Sidebar("", globalIndexUrl)
        model("breadcrumb") = $"<a href=""{DocHtml.Attr(globalIndexUrl)}"">API Docs</a>"
        model("page_kind") = "global"
        model("title") = "API Documentation"

        Return model
    End Function

    ''' <summary>
    ''' render the per package api document index page.
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <param name="base"></param>
    ''' <param name="globalIndexUrl"></param>
    ''' <param name="packageIndexUrl"></param>
    ''' <param name="versionSelect">the prebuilt html of the version selector</param>
    ''' <returns></returns>
    Public Function PackageIndexPage(ctx As DocSiteContext, base As String, globalIndexUrl As String,
                                     packageIndexUrl As String, versionSelect As String) As DocPageModel

        Dim model As DocPageModel = common(ctx, base, "API Documentation")
        Dim packageId$ = If(ctx.Document.packageId, "")
        Dim version$ = If(ctx.Document.packageVersion, "")

        model("content") = packageIndexContent(ctx)
        model("sidebar") = ctx.Sidebar("", packageIndexUrl)
        model("breadcrumb") = $"<a href=""{DocHtml.Attr(globalIndexUrl)}"">API Docs</a> / <span class=""crumb on"">{DocHtml.Escape(packageId)}</span>"
        model("page_kind") = "package"
        model("title") = $"{packageId} {version}".Trim()
        model("package_id") = packageId
        model("package_version") = version
        model("version_select") = If(versionSelect, "")

        Return model
    End Function

    ''' <summary>
    ''' render one type content page.
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <param name="t"></param>
    ''' <param name="base"></param>
    ''' <param name="globalIndexUrl"></param>
    ''' <param name="packageIndexUrl"></param>
    ''' <returns></returns>
    Public Function TypePage(ctx As DocSiteContext, t As ApiDocType, base As String,
                             globalIndexUrl As String, packageIndexUrl As String) As DocPageModel

        Dim model As DocPageModel = common(ctx, base, DocHtml.DisplayTypeName(t.name))
        Dim packageId$ = If(t.packageId, ctx.Document.packageId)
        Dim version$ = If(t.packageVersion, ctx.Document.packageVersion)

        model("content") = TypePageWriter.RenderContent(ctx, t, includeBreadcrumb:=False)
        model("sidebar") = ctx.Sidebar(t.namespaceName, t.url)
        model("breadcrumb") = $"<a href=""{DocHtml.Attr(globalIndexUrl)}"">API Docs</a> / " &
            $"<a href=""{DocHtml.Attr(packageIndexUrl)}"">{DocHtml.Escape(packageId)}</a> / " &
            $"<span class=""crumb on"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.name))}</span>"
        model("page_kind") = "type"
        model("title") = DocHtml.DisplayTypeName(t.name)
        model("type_fullname") = If(t.fullName, "")
        model("package_id") = packageId
        model("package_version") = version

        Return model
    End Function

    Private Function common(ctx As DocSiteContext, base As String, pageTitle As String) As DocPageModel
        Dim model As New DocPageModel

        model("base") = If(base, "")
        model("site_title") = ctx.Title
        model("subtitle") = ctx.SubTitle
        model("description") = ctx.Description
        model("title") = pageTitle
        model("namespace_count") = ctx.Document.NamespaceCount().ToString
        model("type_count") = ctx.Document.TypeCount().ToString
        model("member_count") = ctx.Document.MemberCount().ToString
        model("year") = Date.Now.Year.ToString
        model("generated") = Date.Now.ToString("yyyy-MM-dd HH:mm")

        Return model
    End Function

    ''' <summary>
    ''' the content fragment of the global cross package namespace / type index.
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <returns></returns>
    Private Function globalIndexContent(ctx As DocSiteContext) As String
        Dim document As ApiDocDocument = ctx.Document
        Dim sb As New StringBuilder
        Dim packages = document.types _
            .Select(Function(t) t.packageId) _
            .Where(Function(id) Not String.IsNullOrWhiteSpace(id)) _
            .Distinct(StringComparer.OrdinalIgnoreCase) _
            .Count()

        sb.AppendLine("<h1 class=""headline"">API <span class=""u"">documentation</span> index.</h1>")

        sb.AppendLine("<div class=""meta"">")
        sb.AppendLine($"<span>Packages <b>{packages}</b></span>")
        sb.AppendLine($"<span>Namespaces <b>{document.NamespaceCount()}</b></span>")
        sb.AppendLine($"<span>Types <b>{document.TypeCount()}</b></span>")
        sb.AppendLine($"<span>Members <b>{document.MemberCount()}</b></span>")
        sb.AppendLine("</div>")

        If document.TypeCount() = 0 Then
            sb.AppendLine("<div class=""empty"">no api documentation has been published yet</div>")
            Return sb.ToString()
        End If

        sb.AppendLine("<section id=""namespaces"">")
        sb.AppendLine("<p class=""sec-label""><b>01</b> <span>Namespaces</span></p>")

        For Each g In document.types _
            .GroupBy(Function(t) If(t.namespaceName, "")) _
            .OrderBy(Function(x) x.Key)

            sb.AppendLine($"<section class=""doc-ns-block"">")
            sb.AppendLine($"<h2 class=""doc-ns-title"">{DocHtml.Escape(DocSiteContext.DisplayNamespace(g.Key))}</h2>")
            sb.AppendLine(typeTable(ctx, g.OrderBy(Function(t) t.fullName), showPackage:=True))
            sb.AppendLine("</section>")
        Next

        sb.AppendLine("</section>")

        Return sb.ToString()
    End Function

    ''' <summary>
    ''' the content fragment of the per package api document index.
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <returns></returns>
    Private Function packageIndexContent(ctx As DocSiteContext) As String
        Dim document As ApiDocDocument = ctx.Document
        Dim sb As New StringBuilder
        Dim packageId$ = If(document.packageId, "")
        Dim version$ = If(document.packageVersion, "")

        sb.AppendLine($"<h1 class=""headline""><span class=""u"">{DocHtml.Escape(packageId)}</span> <span class=""accent"">{DocHtml.Escape(version)}</span>.</h1>")

        sb.AppendLine("<div class=""meta"">")
        sb.AppendLine($"<span>Namespaces <b>{document.NamespaceCount()}</b></span>")
        sb.AppendLine($"<span>Types <b>{document.TypeCount()}</b></span>")
        sb.AppendLine($"<span>Members <b>{document.MemberCount()}</b></span>")
        sb.AppendLine("</div>")

        If document.TypeCount() = 0 Then
            sb.AppendLine("<div class=""empty"">this package version ships no api documentation</div>")
            Return sb.ToString()
        End If

        sb.AppendLine("<section id=""namespaces"">")
        sb.AppendLine("<p class=""sec-label""><b>01</b> <span>Namespaces</span></p>")

        For Each g In document.types _
            .GroupBy(Function(t) If(t.namespaceName, "")) _
            .OrderBy(Function(x) x.Key)

            Dim ns As ApiDocNamespace = ctx.Index.FindNamespace(g.Key)
            Dim summary$ = If(ns?.summary, "")

            sb.AppendLine("<section class=""doc-ns-block"">")
            sb.AppendLine($"<h2 class=""doc-ns-title"">{DocHtml.Escape(DocSiteContext.DisplayNamespace(g.Key))}</h2>")

            If Not String.IsNullOrWhiteSpace(summary) Then
                Dim md As CommentMarkdown = ctx.Markdown("")
                sb.AppendLine($"<div class=""ns-sum article-body"">{md.ToHtml(summary)}</div>")
            End If

            sb.AppendLine(typeTable(ctx, g.OrderBy(Function(t) t.fullName), showPackage:=False))
            sb.AppendLine("</section>")
        Next

        sb.AppendLine("</section>")

        Return sb.ToString()
    End Function

    Private Function typeTable(ctx As DocSiteContext, types As IEnumerable(Of ApiDocType), showPackage As Boolean) As String
        Dim sb As New StringBuilder

        sb.AppendLine("<div class=""tablewrap""><table><thead><tr><th>Type</th><th>Summary</th>")

        If showPackage Then
            sb.AppendLine("<th>Package</th>")
        End If

        sb.AppendLine("<th class=""num"">Members</th></tr></thead><tbody>")

        For Each t As ApiDocType In types
            sb.AppendLine("<tr>")
            sb.AppendLine($"<td class=""mono""><a class=""type-link"" href=""{DocHtml.Attr(t.url)}"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.name))}</a></td>")
            sb.AppendLine($"<td>{DocHtml.Escape(DocHtml.PlainSummary(t.summary))}</td>")

            If showPackage Then
                sb.AppendLine($"<td class=""mono"">{DocHtml.Escape(t.packageId)}<br /><span class=""ver"">{DocHtml.Escape(t.packageVersion)}</span></td>")
            End If

            sb.AppendLine($"<td class=""num"">{t.MemberCount()}</td>")
            sb.AppendLine("</tr>")
        Next

        sb.AppendLine("</tbody></table></div>")

        Return sb.ToString()
    End Function
End Module

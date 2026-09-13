Imports System.Text

''' <summary>
''' Write the namespace page of the api reference document site: the namespace
''' comment text and the overview table of the types that are declared in it.
''' </summary>
Public Class NamespacePageWriter

    Public Shared Function Render(ctx As DocSiteContext, ns As ApiDocNamespace) As String
        Return ctx.Page(ns.url, DocSiteContext.DisplayNamespace(ns.name), RenderContent(ctx, ns))
    End Function

    ''' <summary>
    ''' render the namespace page content fragment (without the page shell)
    ''' </summary>
    ''' <param name="ctx"></param>
    ''' <param name="ns"></param>
    ''' <returns></returns>
    Public Shared Function RenderContent(ctx As DocSiteContext, ns As ApiDocNamespace) As String
        Dim md As CommentMarkdown = ctx.Markdown(ns.url)
        Dim base$ = ctx.BaseUrl(ns.url)
        Dim types As List(Of ApiDocType) = ctx.Index.TypesOf(ns.name).ToList
        Dim sb As New StringBuilder

        sb.AppendLine($"<p class=""eyebrow""><b>NS</b> <span>{ctx.NamespaceBreadcrumb(ns.name, ns.url, lastIsText:=True)}</span></p>")
        sb.AppendLine($"<h1 class=""headline"">{DocHtml.Escape(DocSiteContext.DisplayNamespace(ns.name))}</h1>")

        sb.AppendLine("<div class=""meta"">")
        sb.AppendLine($"<span>Assembly <b>{DocHtml.Escape(ns.project)}</b></span>")
        sb.AppendLine($"<span>Types <b>{types.Count}</b></span>")
        sb.AppendLine("</div>")

        If Not String.IsNullOrWhiteSpace(ns.summary) Then
            sb.AppendLine("<div class=""article-body"">")
            sb.AppendLine(md.ToHtml(ns.summary))
            sb.AppendLine("</div>")
        End If

        sb.AppendLine("<section id=""types"">")
        sb.AppendLine("<p class=""sec-label""><b>01</b> <span>Types</span></p>")
        sb.AppendLine("<div class=""tablewrap""><table><thead><tr><th>Type</th><th>Summary</th><th class=""num"">Members</th></tr></thead><tbody>")

        For Each t As ApiDocType In types
            Dim summary$ = DocHtml.PlainSummary(t.summary)

            sb.AppendLine("<tr>")
            sb.AppendLine($"<td class=""mono""><a class=""type-link"" href=""{base}{t.url}"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.name))}</a></td>")
            sb.AppendLine($"<td>{DocHtml.Escape(summary)}</td>")
            sb.AppendLine($"<td class=""num"">{t.MemberCount()}</td>")
            sb.AppendLine("</tr>")
        Next

        sb.AppendLine("</tbody></table></div>")
        sb.AppendLine("</section>")

        Return sb.ToString()
    End Function
End Class

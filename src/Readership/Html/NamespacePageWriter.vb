Imports System.Text

''' <summary>
''' Write the namespace page of the api reference document site: the namespace
''' comment text and the overview table of the types that are declared in it.
''' </summary>
Public Class NamespacePageWriter

    Public Shared Function Render(ctx As DocSiteContext, ns As DocNamespaceEntry) As String
        Dim md As CommentMarkdown = ctx.Markdown(ns.Url)
        Dim base$ = ctx.BaseUrl(ns.Url)
        Dim sb As New StringBuilder

        sb.AppendLine($"<p class=""eyebrow""><b>NS</b> <span>{ctx.NamespaceBreadcrumb(ns.Name, ns.Url, lastIsText:=True)}</span></p>")
        sb.AppendLine($"<h1 class=""headline"">{DocHtml.Escape(DocSiteContext.DisplayNamespace(ns.Name))}</h1>")

        sb.AppendLine("<div class=""meta"">")
        sb.AppendLine($"<span>Assembly <b>{DocHtml.Escape(ns.Project)}</b></span>")
        sb.AppendLine($"<span>Types <b>{ns.Types.Count}</b></span>")
        sb.AppendLine("</div>")

        If Not String.IsNullOrWhiteSpace(ns.Summary) Then
            sb.AppendLine("<div class=""article-body"">")
            sb.AppendLine(md.ToHtml(ns.Summary))
            sb.AppendLine("</div>")
        End If

        sb.AppendLine("<section id=""types"">")
        sb.AppendLine("<p class=""sec-label""><b>01</b> <span>Types</span></p>")
        sb.AppendLine("<div class=""tablewrap""><table><thead><tr><th>Type</th><th>Summary</th><th class=""num"">Members</th></tr></thead><tbody>")

        For Each t As DocTypeEntry In ns.Types
            Dim summary$ = DocHtml.PlainSummary(If(t.Source, Nothing)?.Summary)

            sb.AppendLine("<tr>")
            sb.AppendLine($"<td class=""mono""><a class=""type-link"" href=""{base}{t.Url}"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.Name))}</a></td>")
            sb.AppendLine($"<td>{DocHtml.Escape(summary)}</td>")
            sb.AppendLine($"<td class=""num"">{t.Members.Count}</td>")
            sb.AppendLine("</tr>")
        Next

        sb.AppendLine("</tbody></table></div>")
        sb.AppendLine("</section>")

        Return ctx.Page(ns.Url, DocSiteContext.DisplayNamespace(ns.Name), sb.ToString)
    End Function
End Class

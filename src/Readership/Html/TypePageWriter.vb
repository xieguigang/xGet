Imports System.Text
Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Assembly
Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Serialization

''' <summary>
''' Write the type page of the api reference document site: the type comment
''' text, the member overview tables and the member details which are located
''' by their html anchors.
''' </summary>
Public Class TypePageWriter

    Public Shared Function Render(ctx As DocSiteContext, t As DocTypeEntry) As String
        Dim md As CommentMarkdown = ctx.Markdown(t.Url)
        Dim src As ProjectType = t.Source
        Dim base$ = ctx.BaseUrl(t.Url)
        Dim nsName$ = DocSiteContext.DisplayNamespace(t.ContainingNamespace.Name)
        Dim sb As New StringBuilder

        sb.AppendLine($"<p class=""eyebrow""><b>TYPE</b> <span><a href=""{base}index.html"">Overview</a> / <a href=""{base}{t.ContainingNamespace.Url}"">{DocHtml.Escape(nsName)}</a></span></p>")
        sb.AppendLine($"<h1 class=""headline"">{DocHtml.Escape(DocHtml.DisplayTypeName(t.Name))}</h1>")

        sb.AppendLine("<div class=""meta"">")
        sb.AppendLine($"<span>Full name <b class=""mono"">{DocHtml.Escape(t.FullName)}</b></span>")
        sb.AppendLine($"<span>Assembly <b>{DocHtml.Escape(t.Project)}</b></span>")
        sb.AppendLine($"<span>Members <b>{t.Members.Count}</b></span>")
        sb.AppendLine("</div>")

        If Not String.IsNullOrWhiteSpace(src.Summary) Then
            sb.AppendLine("<div class=""article-body"">")
            sb.AppendLine(md.ToHtml(src.Summary))
            sb.AppendLine("</div>")
        End If

        If Not String.IsNullOrWhiteSpace(src.Remarks) Then
            sb.AppendLine("<p class=""sec-label""><b>00</b> <span>Remarks</span></p>")
            sb.AppendLine("<div class=""article-body"">")
            sb.AppendLine(md.ToHtml(src.Remarks))
            sb.AppendLine("</div>")
        End If

        sb.AppendLine(paramTable(md, src.TypeParams, "Type Parameters"))

        sb.AppendLine("<p class=""sec-label""><b>01</b> <span>Syntax</span></p>")
        sb.AppendLine($"<div class=""sig ws-pre"">{DocHtml.Escape(t.FullName)}</div>")

        Dim no As Integer = 2

        no = memberSection(sb, t, "M"c, "Methods", no, md)
        no = memberSection(sb, t, "P"c, "Properties", no, md)
        no = memberSection(sb, t, "F"c, "Fields", no, md)
        no = memberSection(sb, t, "E"c, "Events", no, md)

        sb.AppendLine("<section id=""members"">")
        sb.AppendLine($"<p class=""sec-label""><b>{no.ToString("00")}</b> <span>Members</span></p>")

        For Each m As DocMemberEntry In t.Members
            sb.AppendLine(memberBlock(t, m, md))
        Next

        sb.AppendLine("</section>")

        Return ctx.Page(t.Url, DocHtml.DisplayTypeName(t.Name), sb.ToString)
    End Function

    Private Shared Function memberSection(sb As StringBuilder, t As DocTypeEntry, kind As Char, label As String, no As Integer, md As CommentMarkdown) As Integer
        Dim groups = t.GetMemberGroups(kind).ToList

        If groups.Count = 0 Then
            Return no
        End If

        sb.AppendLine($"<section id=""{label.ToLowerInvariant}"">")
        sb.AppendLine($"<p class=""sec-label""><b>{no.ToString("00")}</b> <span>{DocHtml.Escape(label)}</span></p>")
        sb.AppendLine("<div class=""tablewrap""><table><thead><tr><th>Name</th><th class=""num"">Overloads</th><th>Summary</th></tr></thead><tbody>")

        For Each g In groups
            Dim first As DocMemberEntry = g.OrderBy(Function(x) x.OverloadIndex).First
            Dim name$ = DocHtml.DisplayTypeName(g.Key)
            Dim summary$ = DocHtml.PlainSummary(first.Source.Summary)

            sb.AppendLine("<tr>")
            sb.AppendLine($"<td class=""mono""><a class=""member-link"" href=""#{DocHtml.Attr(first.Anchor)}"">{DocHtml.Escape(name)}</a></td>")
            sb.AppendLine($"<td class=""num"">{g.Count}</td>")
            sb.AppendLine($"<td>{DocHtml.Escape(summary)}</td>")
            sb.AppendLine("</tr>")
        Next

        sb.AppendLine("</tbody></table></div>")
        sb.AppendLine("</section>")

        Return no + 1
    End Function

    Private Shared Function memberBlock(t As DocTypeEntry, m As DocMemberEntry, md As CommentMarkdown) As String
        Dim src As ProjectMember = m.Source
        Dim sb As New StringBuilder

        sb.AppendLine($"<section class=""member member-{m.KindName}"" id=""{DocHtml.Attr(m.Anchor)}"">")
        sb.AppendLine("<div class=""member-head"">")
        sb.AppendLine($"<span class=""badge {m.KindName}"">{m.KindName}</span>")
        sb.AppendLine($"<span class=""member-name"">{DocHtml.Escape(DocHtml.DisplayTypeName(m.Name))}</span>")

        If m.OverloadIndex > 0 Then
            sb.AppendLine($"<span class=""overload"">overload {m.OverloadIndex + 1}</span>")
        End If

        sb.AppendLine($"<a class=""anchor"" href=""#{DocHtml.Attr(m.Anchor)}"" title=""link to this member"">#</a>")
        sb.AppendLine("</div>")
        sb.AppendLine($"<div class=""sig ws-pre"">{DocHtml.Escape(m.Signature(t))}</div>")

        If Not String.IsNullOrWhiteSpace(src.Summary) Then
            sb.AppendLine($"<div class=""member-body"">{md.ToHtml(src.Summary)}</div>")
        End If

        If Not String.IsNullOrWhiteSpace(src.Remarks) Then
            sb.AppendLine("<div class=""sub-label"">Remarks</div>")
            sb.AppendLine($"<div class=""member-body"">{md.ToHtml(src.Remarks)}</div>")
        End If

        sb.AppendLine(paramTable(md, src.TypeParams, "Type Parameters"))
        sb.AppendLine(paramTable(md, src.Params, "Parameters"))

        If Not String.IsNullOrWhiteSpace(src.Returns) Then
            sb.AppendLine("<div class=""sub-label"">Returns</div>")
            sb.AppendLine($"<div class=""member-body"">{md.ToHtml(src.Returns)}</div>")
        End If

        If Not String.IsNullOrWhiteSpace(src.example) Then
            sb.AppendLine("<div class=""sub-label"">Example</div>")
            sb.AppendLine($"<div class=""member-body example"">{md.ToHtml(src.example)}</div>")
        End If

        sb.AppendLine("</section>")

        Return sb.ToString
    End Function

    Private Shared Function paramTable(md As CommentMarkdown, items As param(), title As String) As String
        If items Is Nothing OrElse items.Length = 0 Then
            Return ""
        End If

        Dim sb As New StringBuilder

        sb.AppendLine($"<div class=""sub-label"">{DocHtml.Escape(title)}</div>")
        sb.AppendLine("<div class=""tablewrap""><table class=""param-table""><thead><tr><th>Name</th><th>Description</th></tr></thead><tbody>")

        For Each p As param In items
            sb.AppendLine($"<tr><td class=""pname""><code>{DocHtml.Escape(p.name)}</code></td><td class=""pdesc"">{md.ToHtml(p.text)}</td></tr>")
        Next

        sb.AppendLine("</tbody></table></div>")

        Return sb.ToString
    End Function
End Class
